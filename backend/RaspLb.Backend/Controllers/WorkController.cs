using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace RaspLb.Backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class WorkController : ControllerBase
{
    private static readonly int MaxConcurrency =
        GetIntEnvironmentVariable(
            "MAX_CONCURRENCY",
            100);

    private static readonly SemaphoreSlim CapacityGate =
        new(
            MaxConcurrency,
            MaxConcurrency);

    private static int _activeRequests;

    [HttpGet]
    public async Task<IActionResult> Get(
        CancellationToken cancellationToken)
    {
        var totalWatch =
            Stopwatch.StartNew();

        /*
         * QUEUE
         *
         * If the backend is already at capacity,
         * the request waits here.
         */
        var queueWatch =
            Stopwatch.StartNew();

        await CapacityGate.WaitAsync(
            cancellationToken);

        queueWatch.Stop();

        var activeRequestsAtStart =
            Interlocked.Increment(
                ref _activeRequests);

        try
        {
            var instanceName =
                Environment.GetEnvironmentVariable(
                    "INSTANCE_NAME")
                ?? Environment.MachineName;

            var latencyMs =
                GetIntEnvironmentVariable(
                    "BASE_LATENCY_MS",
                    50);

            var errorRate =
                GetDoubleEnvironmentVariable(
                    "ERROR_RATE",
                    0);

            /*
             * Simulated backend processing.
             */
            await Task.Delay(
                latencyMs,
                cancellationToken);

            var shouldFail =
                Random.Shared.NextDouble()
                < errorRate;

            totalWatch.Stop();

            var response = new
            {
                InstanceName = instanceName,

                Status =
                    shouldFail
                        ? "Failed"
                        : "Success",

                ConfiguredLatencyMs =
                    latencyMs,

                ErrorRate =
                    errorRate,

                MaxConcurrency =
                    MaxConcurrency,

                ActiveRequestsAtStart =
                    activeRequestsAtStart,

                QueueDelayMs =
                    queueWatch.ElapsedMilliseconds,

                ElapsedMs =
                    totalWatch.ElapsedMilliseconds
            };

            if (shouldFail)
            {
                return StatusCode(
                    503,
                    response);
            }

            return Ok(response);
        }
        finally
        {
            Interlocked.Decrement(
                ref _activeRequests);

            CapacityGate.Release();
        }
    }

    private static int GetIntEnvironmentVariable(
        string name,
        int defaultValue)
    {
        return int.TryParse(
            Environment.GetEnvironmentVariable(name),
            out var value)
            ? value
            : defaultValue;
    }

    private static double GetDoubleEnvironmentVariable(
        string name,
        double defaultValue)
    {
        return double.TryParse(
            Environment.GetEnvironmentVariable(name),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
            ? value
            : defaultValue;
    }
}