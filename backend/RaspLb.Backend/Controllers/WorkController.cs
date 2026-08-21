using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;

namespace RaspLb.Backend.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class WorkController : ControllerBase
    {

        public async Task<IActionResult> Get()
        {
            var stopwatch = Stopwatch.StartNew();

            var instanceName = Environment.GetEnvironmentVariable("INSTANCE_NAME") ?? Environment.MachineName;

            var latencyMs =
                int.TryParse(
                    Environment.GetEnvironmentVariable("BASE_LATENCY_MS"),
                    out var latency)
                    ? latency
                    : 50;
            var errorRate =
                double.TryParse(
                    Environment.GetEnvironmentVariable("ERROR_RATE"),
                    out var rate)
                    ? rate
                    : 0;

            await Task.Delay(latencyMs);

            var shouldFail = Random.Shared.NextDouble() < errorRate;
            stopwatch.Stop();

            if (shouldFail)
            {
                return StatusCode(503, new
                {
                    InstanceName = instanceName,
                    Status = "Failed",
                    ConfiguredLatencyMs = latencyMs,
                    ErrorRate = errorRate,
                    ElapsedMs = stopwatch.ElapsedMilliseconds
                });
            }

            return Ok(new
            {
                InstanceName = instanceName,
                Status = "Success",
                ConfiguredLatencyMs = latencyMs,
                ErrorRate = errorRate,
                ElapsedMs = stopwatch.ElapsedMilliseconds
            });
        }
    }
}
