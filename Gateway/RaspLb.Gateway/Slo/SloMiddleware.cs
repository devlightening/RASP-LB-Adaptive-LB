using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace RaspLb.Gateway.Slo;

// En dışta çalışır (RaspRetryMiddleware'den önce) - bu yüzden
// ölçtüğü süre, olası retry denemelerini de içeren tüm mantıksal
// isteğin uçtan uca süresidir; tek bir backend denemesinin değil.
public sealed class SloMiddleware
{
    private const string ShedHeader = "X-Rasp-Shed";

    private readonly RequestDelegate _next;
    private readonly SloOptions _options;
    private readonly SloMetrics _metrics;
    private readonly RecentTrafficWindow _window;

    public SloMiddleware(
        RequestDelegate next,
        IOptions<SloOptions> options,
        SloMetrics metrics,
        RecentTrafficWindow window)
    {
        _next = next;
        _options = options.Value;
        _metrics = metrics;
        _window = window;
    }

    public async Task InvokeAsync(
        HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await _next(context);

            stopwatch.Stop();

            var success =
                context.Response.StatusCode is >= 200 and < 300;

            var shed =
                !success &&
                context.Response.Headers.ContainsKey(ShedHeader);

            Record(success, shed, stopwatch.Elapsed.TotalMilliseconds);
        }
        catch
        {
            stopwatch.Stop();

            // Deadline'ı aşan bir işlem sonunda exception ile de
            // bitebilir (ör. istemci iptali) - yine de SLO'yu
            // kaçırmış sayılır, sadece başarısız değil.
            Record(success: false, shed: false, stopwatch.Elapsed.TotalMilliseconds);

            throw;
        }
    }

    private void Record(
        bool success,
        bool shed,
        double elapsedMs)
    {
        var withinDeadline =
            elapsedMs <= _options.DeadlineMs;

        if (_options.Enabled)
        {
            _metrics.Record(success, withinDeadline);
        }

        var outcome =
            success
                ? withinDeadline
                    ? RequestOutcomeKind.OnTime
                    : RequestOutcomeKind.Late
                : shed
                    ? RequestOutcomeKind.Shed
                    : RequestOutcomeKind.Error;

        _window.Record(outcome, elapsedMs);
    }
}
