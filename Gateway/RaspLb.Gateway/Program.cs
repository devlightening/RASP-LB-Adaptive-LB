using System.Diagnostics;
using RaspLb.Gateway.LoadBalancing;
using Yarp.ReverseProxy.LoadBalancing;
using Yarp.ReverseProxy.Model;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<DestinationMetricsStore>();

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

app.MapReverseProxy(proxyPipeline =>
{
    // VERY IMPORTANT:
    // Custom MapReverseProxy pipelines do not automatically
    // include YARP load balancing.
    proxyPipeline.UseLoadBalancing();

    proxyPipeline.Use(async (context, next) =>
    {
        var stopwatch = Stopwatch.StartNew();

        await next();

        stopwatch.Stop();

        var proxyFeature =
            context.GetReverseProxyFeature();

        var destination =
            proxyFeature.ProxiedDestination;

        if (destination is null)
        {
            return;
        }

        var success =
            context.Response.StatusCode < 500;

        metricsStore.Record(
            destination,
            stopwatch.Elapsed.TotalMilliseconds,
            success);
    });

    // Useful later when destinations actually fail.
    proxyPipeline.UsePassiveHealthChecks();
});

app.MapGet("/debug/metrics", () =>
{
    var metrics = metricsStore.GetAll()
        .Select(x => new
        {
            Destination = x.Key,
            Samples = x.Value.Samples,
            EwmaLatencyMs = Math.Round(x.Value.EwmaLatencyMs, 2),
            Failures = x.Value.Failures,
            FailureRate = Math.Round(x.Value.FailureRate * 100, 2)
        });

    return Results.Ok(metrics);
});

app.Run();
