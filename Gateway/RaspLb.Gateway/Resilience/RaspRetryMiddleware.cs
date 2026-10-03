using System.Diagnostics;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

namespace RaspLb.Gateway.Resilience;

public sealed class RaspRetryMiddleware
{
    private const string PriorityHeader = "X-Rasp-Priority";

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
        var lastRetryWasShed = false;
        var attemptedDestinationIds =
            new HashSet<string>(StringComparer.Ordinal);
        var eligibleDestinations =
            context.GetReverseProxyFeature()
                .AvailableDestinations;

        while (true)
        {
            // Let the shed transform hold back a 503 only when a retry could
            // actually follow; otherwise the backend's 503 goes straight out.
            if (CanRetryShed(context, attempt, requestWatch, attemptedDestinationIds, eligibleDestinations))
            {
                context.Items[ShedRetry.AllowedKey] = true;
            }

            await _next(context);

            context.Items.Remove(ShedRetry.AllowedKey);

            if (context.Items.Remove(ShedRetry.SuppressedKey, out var shedReason))
            {
                if (TryPrepareRetry(context, attempt, requestWatch, attemptedDestinationIds, eligibleDestinations, $"shed ({shedReason})"))
                {
                    attempt++;
                    lastRetryWasShed = true;
                    _metrics.RecordShedRetryAttempt();
                    continue;
                }

                WriteShedResponse(context, shedReason as string);
                return;
            }

            var forwarderError =
                context.GetForwarderErrorFeature()?.Error;

            if (!IsRetryable(forwarderError))
            {
                if (attempt > 1 &&
                    context.Response.StatusCode is >= 200 and < 300)
                {
                    _metrics.RecordRetrySuccess();

                    if (lastRetryWasShed)
                    {
                        _metrics.RecordShedRetrySuccess();
                    }
                }

                return;
            }

            if (!TryPrepareRetry(context, attempt, requestWatch, attemptedDestinationIds, eligibleDestinations, forwarderError.ToString()!))
            {
                return;
            }

            attempt++;
            lastRetryWasShed = false;
        }
    }

    private bool CanRetryShed(
        HttpContext context,
        int attempt,
        Stopwatch requestWatch,
        HashSet<string> attemptedDestinationIds,
        IReadOnlyList<DestinationState> eligibleDestinations)
    {
        // Sheddable traffic was rejected on purpose; retrying it elsewhere
        // would push the overload it was meant to relieve onto another backend.
        var sheddable = string.Equals(
            context.Request.Headers[PriorityHeader].ToString().Trim(),
            "sheddable",
            StringComparison.OrdinalIgnoreCase);

        return _options.RetryOnShed &&
            !sheddable &&
            attempt < _options.MaxAttempts &&
            IsSafeMethod(context.Request.Method) &&
            requestWatch.ElapsedMilliseconds < _options.TimeBudgetMs &&
            eligibleDestinations.Count > attemptedDestinationIds.Count + 1;
    }

    // Shared guard rails for every kind of retry. Records why a retry was
    // refused, and on success excludes the failed destination and resets
    // the response so the next attempt starts clean.
    private bool TryPrepareRetry(
        HttpContext context,
        int attempt,
        Stopwatch requestWatch,
        HashSet<string> attemptedDestinationIds,
        IReadOnlyList<DestinationState> eligibleDestinations,
        string reason)
    {
        if (attempt >= _options.MaxAttempts)
        {
            _metrics.RecordExhausted();
            return false;
        }

        if (!IsSafeMethod(context.Request.Method))
        {
            _metrics.RecordUnsafeMethodRejected();
            return false;
        }

        if (context.Response.HasStarted)
        {
            _metrics.RecordResponseStartedRejected();
            return false;
        }

        if (requestWatch.ElapsedMilliseconds >=
            _options.TimeBudgetMs)
        {
            _metrics.RecordTimeBudgetRejected();
            return false;
        }

        var proxyFeature =
            context.GetReverseProxyFeature();

        var failedDestination =
            proxyFeature.ProxiedDestination;

        if (failedDestination is null)
        {
            _metrics.RecordNoAlternativeDestination();
            return false;
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
            return false;
        }

        if (!_budget.TryAcquire())
        {
            _metrics.RecordBudgetRejected();
            return false;
        }

        proxyFeature.AvailableDestinations = alternatives;
        proxyFeature.ProxiedDestination = null;

        context.Features.Set<IForwarderErrorFeature?>(null);
        context.Response.Clear();

        _metrics.RecordRetryAttempt();

        _logger.LogInformation(
            "Retrying request {TraceIdentifier} after {Reason}. " +
            "Attempt {Attempt}/{MaxAttempts}; excluding {Destination}.",
            context.TraceIdentifier,
            reason,
            attempt + 1,
            _options.MaxAttempts,
            failedDestination.DestinationId);

        return true;
    }

    // The backend's 503 body was held back for a retry that then could not
    // happen (no budget, no alternative) - send the client an equivalent 503.
    private static void WriteShedResponse(
        HttpContext context,
        string? reason)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = "1";
        context.Response.Headers[ShedRetry.ShedHeader] = reason ?? "unknown";
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
