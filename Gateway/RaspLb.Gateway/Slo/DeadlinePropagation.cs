using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace RaspLb.Gateway.Slo;

// Her denemede (retry dahil) SLO deadline'ından kalan süreyi backend'e
// X-Rasp-Deadline-Ms başlığıyla iletir. Backend bu sayede, yanıtı zaten
// geç kalacak bir isteği kuyrukta bekletmek yerine hemen reddedebilir.
// Başlık her zaman gateway tarafından yazılır; istemcinin gönderdiği
// değer silinir, yoksa istemci kendine sınırsız bekleme süresi verebilirdi.
public static class DeadlinePropagation
{
    public const string Header = "X-Rasp-Deadline-Ms";

    internal const string StartTimestampKey = "rasp.request-start";

    public static void AddDeadlinePropagation(
        this TransformBuilderContext builder)
    {
        var options =
            builder.Services
                .GetRequiredService<IOptions<SloOptions>>()
                .Value;

        builder.AddRequestTransform(transform =>
        {
            var headers = transform.ProxyRequest.Headers;
            headers.Remove(Header);

            if (options.Enabled &&
                transform.HttpContext.Items.TryGetValue(StartTimestampKey, out var start) &&
                start is long startTimestamp)
            {
                var elapsedMs =
                    Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

                var remainingMs =
                    Math.Max(0, options.DeadlineMs - elapsedMs);

                headers.TryAddWithoutValidation(
                    Header,
                    ((int)remainingMs).ToString(CultureInfo.InvariantCulture));
            }

            return ValueTask.CompletedTask;
        });
    }
}
