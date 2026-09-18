using System.Diagnostics;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

namespace RaspLb.Gateway.Resilience;

public sealed class RaspRetryMiddleware
{
    private readonly RequestDelegate _next;
    private readonly RaspRetryOptions _options;
    private readonly RetryBudget _budget;
    private readonly RetryMetrics _metrics;
    private readonly ILogger<RaspRetryMiddleware> _logger;

    public RaspRetryMiddleware(
        RequestDelegate next,
        IOptions<RaspRetryOptions> options,
        RetryBudget budget,
        RetryMetrics metrics,
        ILogger<RaspRetryMiddleware> logger)
    {
        _next = next;
        _options = options.Value;
        _budget = budget;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext context)
    {
        _metrics.RecordLogicalRequest();

        if (!_options.Enabled)
        {
            await _next(context);
            return;
        }

        var requestWatch = Stopwatch.StartNew();
        var attempt = 1;
        var attemptedDestinationIds =
            new HashSet<string>(StringComparer.Ordinal);
        var eligibleDestinations =
            context.GetReverseProxyFeature()
                .AvailableDestinations;

        while (true)
        {
            await _next(context);

            var forwarderError =
                context.GetForwarderErrorFeature()?.Error;

            if (!IsRetryable(forwarderError))
            {
                if (attempt > 1 &&
                    context.Response.StatusCode is >= 200 and < 300)
                {
                    _metrics.RecordRetrySuccess();
                }

                return;
            }

            if (attempt >= _options.MaxAttempts)
            {
                _metrics.RecordExhausted();
                return;
            }

            if (!IsSafeMethod(context.Request.Method))
            {
                _metrics.RecordUnsafeMethodRejected();
                return;
            }

            if (context.Response.HasStarted)
            {
                _metrics.RecordResponseStartedRejected();
                return;
            }

            if (requestWatch.ElapsedMilliseconds >=
                _options.TimeBudgetMs)
            {
                _metrics.RecordTimeBudgetRejected();
                return;
            }

            var proxyFeature =
                context.GetReverseProxyFeature();

            var failedDestination =
                proxyFeature.ProxiedDestination;

            if (failedDestination is null)
            {
                _metrics.RecordNoAlternativeDestination();
                return;
            }

            attemptedDestinationIds.Add(
                failedDestination.DestinationId);

            var alternatives =
                eligibleDestinations
                    .Where(destination =>
                        !attemptedDestinationIds.Contains(
                            destination.DestinationId))
                    .ToArray();

            if (alternatives.Length == 0)
            {
                _metrics.RecordNoAlternativeDestination();
                return;
            }

            if (!_budget.TryAcquire())
            {
                _metrics.RecordBudgetRejected();
                return;
            }

            proxyFeature.AvailableDestinations = alternatives;
            proxyFeature.ProxiedDestination = null;

            context.Features.Set<IForwarderErrorFeature?>(null);
            context.Response.Clear();

            attempt++;
            _metrics.RecordRetryAttempt();

            _logger.LogInformation(
                "Retrying request {TraceIdentifier} after {ForwarderError}. " +
                "Attempt {Attempt}/{MaxAttempts}; excluding {Destination}.",
                context.TraceIdentifier,
                forwarderError,
                attempt,
                _options.MaxAttempts,
                failedDestination.DestinationId);
        }
    }

    private static bool IsSafeMethod(
        string method)
    {
        return HttpMethods.IsGet(method) ||
            HttpMethods.IsHead(method);
    }

    private static bool IsRetryable(
        ForwarderError? error)
    {
        return error is
            ForwarderError.Request or
            ForwarderError.RequestTimedOut or
            ForwarderError.RequestCreation or
            ForwarderError.ResponseHeaders;
    }
}
