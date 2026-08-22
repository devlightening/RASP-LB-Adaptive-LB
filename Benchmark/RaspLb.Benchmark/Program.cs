using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;

const string GatewayUrl = "http://localhost:5200/api/work";

const int TotalRequests = 300;
const int Concurrency = 20;

using var httpClient = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(10)
};

var results = new ConcurrentBag<RequestResult>();

Console.WriteLine("========================================");
Console.WriteLine("          RASP-LB BENCHMARK");
Console.WriteLine("========================================");
Console.WriteLine();
Console.WriteLine($"Target      : {GatewayUrl}");
Console.WriteLine($"Requests    : {TotalRequests}");
Console.WriteLine($"Concurrency : {Concurrency}");
Console.WriteLine($"Started At  : {DateTime.Now:HH:mm:ss}");
Console.WriteLine();

var totalWatch = Stopwatch.StartNew();

await Parallel.ForEachAsync(
    Enumerable.Range(1, TotalRequests),
    new ParallelOptions
    {
        MaxDegreeOfParallelism = Concurrency
    },
    async (_, cancellationToken) =>
    {
        var requestWatch = Stopwatch.StartNew();

        try
        {
            using var response =
                await httpClient.GetAsync(
                    GatewayUrl,
                    cancellationToken);

            var json =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            requestWatch.Stop();

            var backendName =
                ExtractJsonValue(
                    json,
                    "instanceName")
                ?? "Unknown";

            var queueDelayMs =
                ExtractJsonDouble(
                    json,
                    "queueDelayMs");

            var activeRequestsAtStart =
                ExtractJsonInt(
                    json,
                    "activeRequestsAtStart");

            var maxConcurrency =
                ExtractJsonInt(
                    json,
                    "maxConcurrency");

            results.Add(
                new RequestResult(
                    Success: response.IsSuccessStatusCode,
                    StatusCode: response.StatusCode,
                    LatencyMs:
                        requestWatch.Elapsed.TotalMilliseconds,
                    Backend: backendName,
                    QueueDelayMs: queueDelayMs,
                    ActiveRequestsAtStart:
                        activeRequestsAtStart,
                    MaxConcurrency:
                        maxConcurrency
                )
            );
        }
        catch
        {
            requestWatch.Stop();

            results.Add(
                new RequestResult(
                    Success: false,
                    StatusCode: null,
                    LatencyMs:
                        requestWatch.Elapsed.TotalMilliseconds,
                    Backend: "Unknown",
                    QueueDelayMs: 0,
                    ActiveRequestsAtStart: 0,
                    MaxConcurrency: 0
                )
            );
        }
    });

totalWatch.Stop();

var allResults =
    results.ToArray();

var latencies =
    allResults
        .Select(x => x.LatencyMs)
        .OrderBy(x => x)
        .ToArray();

var successful =
    allResults.Count(x => x.Success);

var failed =
    allResults.Length - successful;


// ======================================================
// GENERAL RESULTS
// ======================================================

Console.WriteLine();
Console.WriteLine("========================================");
Console.WriteLine("               RESULTS");
Console.WriteLine("========================================");
Console.WriteLine();

Console.WriteLine(
    $"Total Requests : {allResults.Length}");

Console.WriteLine(
    $"Successful     : {successful}");

Console.WriteLine(
    $"Failed         : {failed}");

Console.WriteLine(
    $"Success Rate   : " +
    $"{(double)successful / allResults.Length * 100:F2}%");

Console.WriteLine();

Console.WriteLine(
    $"Average Latency: {latencies.Average():F2} ms");

Console.WriteLine(
    $"P50 Latency    : " +
    $"{Percentile(latencies, 0.50):F2} ms");

Console.WriteLine(
    $"P95 Latency    : " +
    $"{Percentile(latencies, 0.95):F2} ms");

Console.WriteLine(
    $"P99 Latency    : " +
    $"{Percentile(latencies, 0.99):F2} ms");

Console.WriteLine();

Console.WriteLine(
    $"Total Duration : " +
    $"{totalWatch.Elapsed.TotalSeconds:F2} sec");

Console.WriteLine(
    $"Throughput     : " +
    $"{allResults.Length / totalWatch.Elapsed.TotalSeconds:F2} req/sec");


// ======================================================
// BACKEND DISTRIBUTION
// ======================================================

Console.WriteLine();
Console.WriteLine("BACKEND DISTRIBUTION");
Console.WriteLine("----------------------------------------");

foreach (var group in allResults
             .Where(x =>
                 x.Backend != "Unknown")
             .GroupBy(x =>
                 x.Backend)
             .OrderBy(x =>
                 x.Key))
{
    var backendSuccess =
        group.Count(x => x.Success);

    var backendFailed =
        group.Count() - backendSuccess;

    Console.WriteLine(
        $"{group.Key,-12} " +
        $"Total: {group.Count(),4} | " +
        $"Success: {backendSuccess,4} | " +
        $"Failed: {backendFailed,4}");
}


// ======================================================
// QUEUE METRICS
// ======================================================

Console.WriteLine();
Console.WriteLine("QUEUE METRICS");
Console.WriteLine("----------------------------------------");

var queueValues =
    allResults
        .Select(x => x.QueueDelayMs)
        .OrderBy(x => x)
        .ToArray();

var queuedRequests =
    allResults.Count(
        x => x.QueueDelayMs > 0);

Console.WriteLine(
    $"Queued Requests : {queuedRequests}");

Console.WriteLine(
    $"Queue Rate      : " +
    $"{(double)queuedRequests / allResults.Length * 100:F2}%");

Console.WriteLine(
    $"Average Queue   : " +
    $"{queueValues.Average():F2} ms");

Console.WriteLine(
    $"P50 Queue       : " +
    $"{Percentile(queueValues, 0.50):F2} ms");

Console.WriteLine(
    $"P95 Queue       : " +
    $"{Percentile(queueValues, 0.95):F2} ms");

Console.WriteLine(
    $"P99 Queue       : " +
    $"{Percentile(queueValues, 0.99):F2} ms");

Console.WriteLine(
    $"Max Queue       : " +
    $"{queueValues.Max():F2} ms");


// ======================================================
// QUEUE BY BACKEND
// ======================================================

Console.WriteLine();
Console.WriteLine("QUEUE BY BACKEND");
Console.WriteLine("----------------------------------------");

foreach (var group in allResults
             .Where(x =>
                 x.Backend != "Unknown")
             .GroupBy(x =>
                 x.Backend)
             .OrderBy(x =>
                 x.Key))
{
    var backendQueue =
        group
            .Select(x =>
                x.QueueDelayMs)
            .OrderBy(x => x)
            .ToArray();

    var backendQueued =
        group.Count(
            x => x.QueueDelayMs > 0);

    Console.WriteLine(
        $"{group.Key,-12} " +
        $"AvgQueue: {backendQueue.Average(),7:F2} ms | " +
        $"P95Queue: {Percentile(backendQueue, 0.95),7:F2} ms | " +
        $"MaxQueue: {backendQueue.Max(),7:F2} ms | " +
        $"Queued: {backendQueued,4}/{group.Count(),4}");
}


// ======================================================
// LOAD / CAPACITY OBSERVATION
// ======================================================

Console.WriteLine();
Console.WriteLine("LOAD BY BACKEND");
Console.WriteLine("----------------------------------------");

foreach (var group in allResults
             .Where(x =>
                 x.Backend != "Unknown")
             .GroupBy(x =>
                 x.Backend)
             .OrderBy(x =>
                 x.Key))
{
    var maxConcurrency =
        group
            .Select(x => x.MaxConcurrency)
            .DefaultIfEmpty(0)
            .Max();

    var averageActive =
        group
            .Select(x =>
                (double)x.ActiveRequestsAtStart)
            .DefaultIfEmpty(0)
            .Average();

    var maxActive =
        group
            .Select(x =>
                x.ActiveRequestsAtStart)
            .DefaultIfEmpty(0)
            .Max();

    Console.WriteLine(
        $"{group.Key,-12} " +
        $"Capacity: {maxConcurrency,3} | " +
        $"AvgActive: {averageActive,6:F2} | " +
        $"MaxActive: {maxActive,3}");
}


// ======================================================
// STATUS CODES
// ======================================================

Console.WriteLine();
Console.WriteLine("STATUS CODES");
Console.WriteLine("----------------------------------------");

foreach (var group in allResults
             .GroupBy(x =>
                 x.StatusCode?.ToString()
                 ?? "Exception")
             .OrderBy(x =>
                 x.Key))
{
    Console.WriteLine(
        $"{group.Key,-20}: {group.Count()}");
}

Console.WriteLine();
Console.WriteLine("Benchmark completed.");


// ======================================================
// HELPERS
// ======================================================

static double Percentile(
    double[] sortedValues,
    double percentile)
{
    if (sortedValues.Length == 0)
    {
        return 0;
    }

    var index =
        (int)Math.Ceiling(
            percentile *
            sortedValues.Length)
        - 1;

    index =
        Math.Clamp(
            index,
            0,
            sortedValues.Length - 1);

    return sortedValues[index];
}


static string? ExtractJsonValue(
    string json,
    string propertyName)
{
    try
    {
        using var document =
            System.Text.Json.JsonDocument.Parse(json);

        if (document.RootElement.TryGetProperty(
                propertyName,
                out var property))
        {
            return property.GetString();
        }
    }
    catch
    {
        // Invalid or empty JSON body.
    }

    return null;
}


static double ExtractJsonDouble(
    string json,
    string propertyName)
{
    try
    {
        using var document =
            System.Text.Json.JsonDocument.Parse(json);

        if (document.RootElement.TryGetProperty(
                propertyName,
                out var property))
        {
            return property.GetDouble();
        }
    }
    catch
    {
        // Missing or invalid JSON value.
    }

    return 0;
}


static int ExtractJsonInt(
    string json,
    string propertyName)
{
    try
    {
        using var document =
            System.Text.Json.JsonDocument.Parse(json);

        if (document.RootElement.TryGetProperty(
                propertyName,
                out var property))
        {
            return property.GetInt32();
        }
    }
    catch
    {
        // Missing or invalid JSON value.
    }

    return 0;
}


// ======================================================
// RESULT MODEL
// ======================================================

record RequestResult(
    bool Success,
    HttpStatusCode? StatusCode,
    double LatencyMs,
    string Backend,
    double QueueDelayMs,
    int ActiveRequestsAtStart,
    int MaxConcurrency
);