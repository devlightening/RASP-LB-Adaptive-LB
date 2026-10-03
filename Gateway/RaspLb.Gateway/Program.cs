using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RaspLb.Gateway.LoadBalancing;
using RaspLb.Gateway.Resilience;
using RaspLb.Gateway.Slo;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.LoadBalancing;
using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Model;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<DestinationMetricsStore>();
builder.Services.AddSingleton<RetryBudget>();
builder.Services.AddSingleton<RetryMetrics>();
builder.Services.AddSingleton<SloMetrics>();
builder.Services.AddSingleton<RecentTrafficWindow>();

// Dashboard backend'lerin anlık durumunu gateway üzerinden okur
// (tarayıcı backend portlarına doğrudan gitmesin). Kısa timeout:
// aşırı yükte yanıt veremeyen backend paneli kilitlememeli.
builder.Services.AddHttpClient("backend-state", client =>
{
    client.Timeout = TimeSpan.FromMilliseconds(500);
});

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

builder.Services
    .AddOptions<SloOptions>()
    .Bind(
        builder.Configuration.GetSection(
            SloOptions.SectionName))
    .Validate(
        options => options.DeadlineMs > 0,
        "Slo:DeadlineMs must be greater than zero.")
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

builder.Services.AddSingleton<
    ILoadBalancingPolicy,
    RaspV3LoadBalancingPolicy>();

builder.Services
    .AddReverseProxy()
    .LoadFromConfig(
        builder.Configuration.GetSection("ReverseProxy"))
    .AddTransforms(transforms =>
    {
        transforms.AddShedRetryTransform();
        transforms.AddDeadlinePropagation();
    });

var app = builder.Build();

// Dashboard (wwwroot/index.html) - statik dosyalar en başta servis
// edilmeli ki /api/{**catch-all} proxy rotası ile çakışmasın.
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // Panel sık değişiyor; tarayıcı her açılışta ETag ile kontrol etsin.
    OnPrepareResponse = staticFile =>
        staticFile.Context.Response.Headers.CacheControl = "no-cache"
});

var metricsStore =
    app.Services.GetRequiredService<DestinationMetricsStore>();

var retryMetrics =
    app.Services.GetRequiredService<RetryMetrics>();

var sloMetrics =
    app.Services.GetRequiredService<SloMetrics>();

var retryOptions =
    app.Services.GetRequiredService<IOptions<RaspRetryOptions>>().Value;

var sloOptions =
    app.Services.GetRequiredService<IOptions<SloOptions>>().Value;

var clusterConfigSection =
    builder.Configuration.GetSection(
        "ReverseProxy:Clusters:work-cluster");

var activePolicyName =
    clusterConfigSection["LoadBalancingPolicy"] ?? "Unknown";

var configuredDestinations =
    clusterConfigSection.GetSection("Destinations")
        .GetChildren()
        .Select(destinationSection => new
        {
            Id = destinationSection.Key,
            Address = destinationSection["Address"],
            Capacity = destinationSection["Metadata:RaspCapacity"]
        })
        .ToArray();

app.MapReverseProxy(proxyPipeline =>
{
    // SLO en dışta ölçer, çünkü mantıksal isteğin uçtan uca süresini
    // (retry denemeleri dahil) görmesi gerekiyor - tek bir backend
    // denemesinin değil.
    proxyPipeline.UseMiddleware<SloMiddleware>();

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
                snapshot.UnhandledExceptions,
                snapshot.Sheds
            };
        });

    return Results.Ok(metrics);
});

app.MapGet("/debug/retries", () =>
{
    return Results.Ok(
        retryMetrics.GetSnapshot());
});

app.MapGet("/debug/slo", () =>
{
    return Results.Ok(
        sloMetrics.GetSnapshot());
});

app.MapGet("/debug/config", () =>
{
    return Results.Ok(new
    {
        ActivePolicy = activePolicyName,
        Slo = new
        {
            sloOptions.Enabled,
            sloOptions.DeadlineMs
        },
        Retry = new
        {
            retryOptions.Enabled,
            retryOptions.MaxAttempts,
            retryOptions.TimeBudgetMs,
            retryOptions.TokensPerSecond,
            retryOptions.BurstCapacity,
            retryOptions.RetryOnShed,
            retryOptions.ShedRetryMaxElapsedMs
        },
        Destinations = configuredDestinations
    });
});

// Dashboard'un tek veri kaynağı: gateway'in gördüğü (in-flight, EWMA),
// backend'lerin kendi gördüğü (kuyruk, brownout, shed) ve son 2 dakikanın
// saniyelik trafiği tek yanıtta.
app.MapGet("/debug/live", async (
    IProxyStateLookup proxyState,
    IHttpClientFactory httpClientFactory,
    RecentTrafficWindow trafficWindow,
    CancellationToken cancellationToken) =>
{
    var httpClient =
        httpClientFactory.CreateClient("backend-state");

    proxyState.TryGetCluster("work-cluster", out var cluster);

    var destinations =
        await Task.WhenAll(configuredDestinations.Select(async configured =>
        {
            DestinationState? state = null;
            cluster?.Destinations.TryGetValue(configured.Id, out state);

            var metrics =
                state is null
                    ? null
                    : (DestinationMetricsSnapshot?)metricsStore.Get(state).GetSnapshot();

            JsonElement? backendState = null;

            try
            {
                backendState =
                    await httpClient.GetFromJsonAsync<JsonElement>(
                        new Uri(new Uri(configured.Address!), "debug/state"),
                        cancellationToken);
            }
            catch (Exception exception) when (
                exception is HttpRequestException or TaskCanceledException or JsonException)
            {
                // Backend erişilemiyorsa panel bunu "yanıt yok" olarak gösterir.
            }

            return new
            {
                configured.Id,
                configured.Capacity,
                GatewayInFlight = state?.ConcurrentRequestCount ?? 0,
                EwmaLatencyMs = metrics is null ? 0 : Math.Round(metrics.Value.EwmaLatencyMs, 1),
                EwmaErrorRate = metrics is null ? 0 : Math.Round(metrics.Value.EwmaErrorRate, 4),
                Samples = metrics?.Samples ?? 0,
                Backend = backendState
            };
        }));

    return Results.Ok(new
    {
        ServerTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        ActivePolicy = activePolicyName,
        sloOptions.DeadlineMs,
        Slo = sloMetrics.GetSnapshot(),
        Retries = retryMetrics.GetSnapshot(),
        Destinations = destinations,
        Series = trafficWindow.GetSeries()
    });
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

    if (context.Response.StatusCode == StatusCodes.Status503ServiceUnavailable &&
        context.Response.Headers.ContainsKey("X-Rasp-Shed"))
    {
        return DestinationRequestOutcome.Shed;
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
