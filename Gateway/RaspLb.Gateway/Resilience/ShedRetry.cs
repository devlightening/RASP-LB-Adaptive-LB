using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace RaspLb.Gateway.Resilience;

// Backend kuyruğu dolu diye isteği reddettiğinde (503 + X-Rasp-Shed) bu,
// "başka bir backend'de yer olabilir" demektir - gerçek bir hata değil.
// Normalde YARP 503'ü olduğu gibi istemciye kopyalar ve yanıt başlar; o
// noktadan sonra retry imkansızdır. Bu transform, retry middleware'i izin
// verdiyse 503'ün gövdesini bastırır: yanıt istemciye hiç başlamaz ve
// middleware isteği başka bir backend'e yeniden gönderebilir.
public static class ShedRetry
{
    public const string ShedHeader = "X-Rasp-Shed";

    // Middleware -> transform: bu deneme reddedilirse retry mümkün.
    internal const string AllowedKey = "rasp.shed-retry.allowed";

    // Transform -> middleware: 503 bastırıldı; değer reddetme nedenidir.
    internal const string SuppressedKey = "rasp.shed-retry.suppressed";

    public static void AddShedRetryTransform(
        this TransformBuilderContext builder)
    {
        builder.AddResponseTransform(transform =>
        {
            var response = transform.ProxyResponse;

            if (response is { StatusCode: System.Net.HttpStatusCode.ServiceUnavailable } &&
                response.Headers.TryGetValues(ShedHeader, out var reasons) &&
                transform.HttpContext.Items.ContainsKey(AllowedKey))
            {
                transform.SuppressResponseBody = true;
                transform.HttpContext.Items[SuppressedKey] =
                    reasons.FirstOrDefault() ?? "unknown";
            }

            return ValueTask.CompletedTask;
        });
    }
}
