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
                await httpClient.GetAsync(GatewayUrl, cancellationToken);

            var json =
                await response.Content.ReadAsStringAsync(cancellationToken);

            requestWatch.Stop();

            string backendName = ExtractJsonValue(json, "instanceName") ?? "Unknown";

            results.Add(
                new RequestResult(
                    Success: response.IsSuccessStatusCode,
                    StatusCode: response.StatusCode,
                    LatencyMs: requestWatch.Elapsed.TotalMilliseconds,
                    Backend: backendName
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
                    LatencyMs: requestWatch.Elapsed.TotalMilliseconds,
                    Backend: "Unknown"
                )
            );
        }
    });

totalWatch.Stop();

var allResults = results.ToArray();

var latencies = allResults
    .Select(x => x.LatencyMs)
    .OrderBy(x => x)
    .ToArray();

var successful = allResults.Count(x => x.Success);
var failed = allResults.Length - successful;

Console.WriteLine();
Console.WriteLine("========================================");
Console.WriteLine("               RESULTS");
Console.WriteLine("========================================");
Console.WriteLine();

Console.WriteLine($"Total Requests : {allResults.Length}");
Console.WriteLine($"Successful     : {successful}");
Console.WriteLine($"Failed         : {failed}");

Console.WriteLine(
    $"Success Rate   : {(double)successful / allResults.Length * 100:F2}%");

Console.WriteLine();

Console.WriteLine($"Average Latency: {latencies.Average():F2} ms");
Console.WriteLine($"P50 Latency    : {Percentile(latencies, 0.50):F2} ms");
Console.WriteLine($"P95 Latency    : {Percentile(latencies, 0.95):F2} ms");
Console.WriteLine($"P99 Latency    : {Percentile(latencies, 0.99):F2} ms");

Console.WriteLine();

Console.WriteLine($"Total Duration : {totalWatch.Elapsed.TotalSeconds:F2} sec");
Console.WriteLine(
    $"Throughput     : {allResults.Length / totalWatch.Elapsed.TotalSeconds:F2} req/sec");

Console.WriteLine();
Console.WriteLine("BACKEND DISTRIBUTION");
Console.WriteLine("----------------------------------------");

foreach (var group in allResults
             .Where(x => x.Backend != "Unknown")
             .GroupBy(x => x.Backend)
             .OrderBy(x => x.Key))
{
    var backendSuccess = group.Count(x => x.Success);
    var backendFailed = group.Count() - backendSuccess;

    Console.WriteLine(
        $"{group.Key,-12} Total: {group.Count(),4} | " +
        $"Success: {backendSuccess,4} | " +
        $"Failed: {backendFailed,4}");
}

Console.WriteLine();
Console.WriteLine("STATUS CODES");
Console.WriteLine("----------------------------------------");

foreach (var group in allResults
             .GroupBy(x => x.StatusCode?.ToString() ?? "Exception")
             .OrderBy(x => x.Key))
{
    Console.WriteLine($"{group.Key,-20}: {group.Count()}");
}

Console.WriteLine();
Console.WriteLine("Benchmark completed.");

static double Percentile(double[] sortedValues, double percentile)
{
    if (sortedValues.Length == 0)
        return 0;

    var index =
        (int)Math.Ceiling(percentile * sortedValues.Length) - 1;

    index = Math.Clamp(index, 0, sortedValues.Length - 1);

    return sortedValues[index];
}

static string? ExtractJsonValue(string json, string propertyName)
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

record RequestResult(
    bool Success,
    HttpStatusCode? StatusCode,
    double LatencyMs,
    string Backend
);