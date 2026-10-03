using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using RaspLb.Backend.Admission;
using RaspLb.Backend.Brownout;

namespace RaspLb.Backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class WorkController : ControllerBase
{
    private static readonly int EnrichmentLatencyMs =
        GetIntEnvironmentVariable(
            "ENRICHMENT_LATENCY_MS",
            30);

    private readonly AdmissionGate _admissionGate;
    private readonly BrownoutState _brownoutState;

    public WorkController(
        AdmissionGate admissionGate,
        BrownoutState brownoutState)
    {
        _admissionGate = admissionGate;
        _brownoutState = brownoutState;
    }

    [HttpGet]
    public async Task<IActionResult> Get(
        CancellationToken cancellationToken)
    {
        var totalWatch =
            Stopwatch.StartNew();

        /*
         * QUEUE / ADMISSION
         *
         * If the backend is already at capacity, the request waits
         * here. With shedding enabled the wait is bounded (queue length
         * and queue timeout); past that the request is rejected early
         * instead of queueing until it is useless to the caller.
         */
        var queueWatch =
            Stopwatch.StartNew();

        var admission =
            await _admissionGate.EnterAsync(
                cancellationToken);

        queueWatch.Stop();

        if (admission != AdmissionResult.Admitted)
        {
            var reason =
                admission switch
                {
                    AdmissionResult.RejectedQueueFull => "queue-full",
                    AdmissionResult.RejectedPredictedWait => "predicted-wait",
                    _ => "queue-timeout"
                };

            Response.Headers.RetryAfter = "1";
            Response.Headers["X-Rasp-Shed"] = reason;

            return StatusCode(
                503,
                new
                {
                    Status = "Shed",
                    Reason = reason,
                    QueueDelayMs = queueWatch.ElapsedMilliseconds
                });
        }

        var activeRequestsAtStart =
            _admissionGate.GetSnapshot().Active;

        var serviceWatch =
            Stopwatch.StartNew();

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
             * Simulated CORE backend processing - never skipped,
             * this is the part the caller actually asked for.
             */
            await Task.Delay(
                latencyMs,
                cancellationToken);

            /*
             * BROWNOUT
             *
             * Optional "enrichment" work (e.g. recommendations,
             * extra formatting) - skipped while requests are piling
             * up behind this one, so the core response of the queued
             * requests is protected instead of non-essential work.
             */
            var reducedMode =
                _brownoutState.ShouldReduce(
                    _admissionGate.EstimatedQueueWaitMs());

            if (!reducedMode)
            {
                await Task.Delay(
                    EnrichmentLatencyMs,
                    cancellationToken);
            }

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
                    _admissionGate.Capacity,

                ActiveRequestsAtStart =
                    activeRequestsAtStart,

                QueueDelayMs =
                    queueWatch.ElapsedMilliseconds,

                Mode =
                    reducedMode
                        ? "Reduced"
                        : "Full",

                EnrichmentLatencyMs =
                    reducedMode
                        ? 0
                        : EnrichmentLatencyMs,

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
            _admissionGate.Exit(
                serviceWatch.Elapsed.TotalMilliseconds);
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
