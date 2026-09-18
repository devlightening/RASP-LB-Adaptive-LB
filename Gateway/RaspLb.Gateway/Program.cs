using System.Diagnostics;
using RaspLb.Gateway.LoadBalancing;
using RaspLb.Gateway.Resilience;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.LoadBalancing;
using Yarp.ReverseProxy.Model;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<DestinationMetricsStore>();
builder.Services.AddSingleton<RetryBudget>();
builder.Services.AddSingleton<RetryMetrics>();

builder.Services
    .AddOptions<RaspRetryOptions>()
    .Bind(
        builder.Configuration.GetSection(
            RaspRetryOptions.SectionName))
    .Validate(
        options => options.MaxAttempts >= 1,
        "RaspRetry:MaxAttempts must be at least 1.")
    .Validate(
        options => options.TimeBudgetMs > 0,
        "RaspRetry:TimeBudgetMs must be greater than zero.")
    .Validate(
        options => options.TokensPerSecond > 0,
        "RaspRetry:TokensPerSecond must be greater than zero.")
    .Validate(
        options => options.BurstCapacity > 0,
        "RaspRetry:BurstCapacity must be greater than zero.")
    .ValidateOnStart();

builder.Services.AddSingleton<
    ILoadBalancingPolicy,
    RaspV0LoadBalancingPolicy>();
builder.Services.AddSingleton<
    ILoadBalancingPolicy,
    RaspV1LoadBalancingPolicy>();

builder.Services.AddSingleton<
    ILoadBalancingPolicy,
    RaspV2LoadBalancingPolicy>();

builder.Services
    .AddReverseProxy()
    .LoadFromConfig(
        builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();

var metricsStore =
    app.Services.GetRequiredService<DestinationMetricsStore>();

var retryMetrics =
    app.Services.GetRequiredService<RetryMetrics>();

app.MapReverseProxy(proxyPipeline =>
{
    // Retry wraps load balancing so a second attempt can exclude
    // the failed destination and run destination selection again.
    proxyPipeline.UseMiddleware<RaspRetryMiddleware>();

    // VERY IMPORTANT:
    // Custom MapReverseProxy pipelines do not automatically
    // include YARP load balancing.
    proxyPipeline.UseLoadBalancing();

    proxyPipeline.Use(async (context, next) =>
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await next();

            stopwatch.Stop();

            var destination =
                context.Features.Get<IReverseProxyFeature>()
                    ?.ProxiedDestination;

            if (destination is null)
            {
                return;
            }

            var outcome =
                ClassifyProxyOutcome(context);

            metricsStore.Record(
                destination,
                stopwatch.Elapsed.TotalMilliseconds,
                outcome);
        }
        catch
        {
            // Expected proxy failures are exposed through
            // IForwarderErrorFeature. This path is only for an
            // unexpected exception that escapes the proxy pipeline.
            stopwatch.Stop();

            var destination =
                context.Features.Get<IReverseProxyFeature>()
                    ?.ProxiedDestination;

            if (destination is not null)
            {
                var outcome =
                    context.RequestAborted.IsCancellationRequested
                        ? DestinationRequestOutcome.Canceled
                        : DestinationRequestOutcome.UnhandledException;

                metricsStore.Record(
                    destination,
                    stopwatch.Elapsed.TotalMilliseconds,
                    outcome);
            }

            throw;
        }
    });

    // Useful later when destinations actually fail.
    proxyPipeline.UsePassiveHealthChecks();
});

app.MapGet("/debug/metrics", () =>
{
    var metrics = metricsStore.GetAll()
        .Select(x =>
        {
            var snapshot = x.Value.GetSnapshot();

            return new
            {
                Destination = x.Key,
                snapshot.Samples,
                EwmaLatencyMs = Math.Round(snapshot.EwmaLatencyMs, 2),
                EwmaErrorRate = Math.Round(snapshot.EwmaErrorRate * 100, 2),
                snapshot.Failures,
                snapshot.DestinationFailures,
                FailureRate = Math.Round(snapshot.FailureRate * 100, 2),
                snapshot.Successes,
                snapshot.HttpClientErrors,
                snapshot.HttpServerErrors,
                snapshot.OtherHttpFailures,
                snapshot.ForwarderFailures,
                snapshot.Timeouts,
                snapshot.Cancellations,
                snapshot.UnhandledExceptions
            };
        });

    return Results.Ok(metrics);
});

app.MapGet("/debug/retries", () =>
{
    return Results.Ok(
        retryMetrics.GetSnapshot());
});

static DestinationRequestOutcome ClassifyProxyOutcome(
    HttpContext context)
{
    var forwarderError =
        context.GetForwarderErrorFeature()?.Error;

    if (forwarderError is not null and not ForwarderError.None)
    {
        return forwarderError switch
        {
            ForwarderError.RequestTimedOut or
            ForwarderError.UpgradeActivityTimeout =>
                DestinationRequestOutcome.Timeout,

            ForwarderError.RequestCanceled or
            ForwarderError.RequestBodyCanceled or
            ForwarderError.ResponseBodyCanceled or
            ForwarderError.UpgradeRequestCanceled or
            ForwarderError.UpgradeResponseCanceled =>
                DestinationRequestOutcome.Canceled,

            _ => DestinationRequestOutcome.ForwarderFailure
        };
    }

    return context.Response.StatusCode switch
    {
        >= 200 and < 300 => DestinationRequestOutcome.Success,
        >= 400 and < 500 => DestinationRequestOutcome.HttpClientError,
        >= 500 and < 600 => DestinationRequestOutcome.HttpServerError,
        _ => DestinationRequestOutcome.OtherHttpFailure
    };
}

app.Run();
