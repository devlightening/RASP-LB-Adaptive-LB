namespace RaspLb.Gateway.LoadBalancing;

public enum DestinationRequestOutcome
{
    Success,
    HttpClientError,
    HttpServerError,
    OtherHttpFailure,
    ForwarderFailure,
    Timeout,
    Canceled,
    UnhandledException
}

public readonly record struct DestinationMetricsSnapshot(
    long Samples,
    double EwmaLatencyMs,
    double EwmaErrorRate,
    long Failures,
    long DestinationFailures,
    long Successes,
    long HttpClientErrors,
    long HttpServerErrors,
    long OtherHttpFailures,
    long ForwarderFailures,
    long Timeouts,
    long Cancellations,
    long UnhandledExceptions)
{
    public double FailureRate =>
        Samples == 0
            ? 0
            : (double)Failures / Samples;
}

public sealed class DestinationMetrics
{
    private readonly object _lock = new();

    private long _samples;

    private double _ewmaLatencyMs;
    private double _ewmaErrorRate;

    private long _failures;
    private long _destinationFailures;
    private long _successes;
    private long _httpClientErrors;
    private long _httpServerErrors;
    private long _otherHttpFailures;
    private long _forwarderFailures;
    private long _timeouts;
    private long _cancellations;
    private long _unhandledExceptions;

    public long Samples
    {
        get
        {
            lock (_lock)
            {
                return _samples;
            }
        }
    }

    public double EwmaLatencyMs
    {
        get
        {
            lock (_lock)
            {
                return _ewmaLatencyMs;
            }
        }
    }

    public double EwmaErrorRate
    {
        get
        {
            lock (_lock)
            {
                return _ewmaErrorRate;
            }
        }
    }

    public long Failures
    {
        get
        {
            lock (_lock)
            {
                return _failures;
            }
        }
    }

    public double FailureRate
    {
        get
        {
            lock (_lock)
            {
                if (_samples == 0)
                    return 0;

                return (double)_failures / _samples;
            }
        }
    }

    public DestinationMetricsSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return new DestinationMetricsSnapshot(
                Samples: _samples,
                EwmaLatencyMs: _ewmaLatencyMs,
                EwmaErrorRate: _ewmaErrorRate,
                Failures: _failures,
                DestinationFailures: _destinationFailures,
                Successes: _successes,
                HttpClientErrors: _httpClientErrors,
                HttpServerErrors: _httpServerErrors,
                OtherHttpFailures: _otherHttpFailures,
                ForwarderFailures: _forwarderFailures,
                Timeouts: _timeouts,
                Cancellations: _cancellations,
                UnhandledExceptions: _unhandledExceptions);
        }
    }

    public void Record(
        double latencyMs,
        DestinationRequestOutcome outcome)
    {
        const double latencyAlpha = 0.20;
        const double errorAlpha = 0.20;

        lock (_lock)
        {
            var success =
                outcome == DestinationRequestOutcome.Success;

            var destinationFailure =
                IsDestinationFailure(outcome);

            var errorSample =
                destinationFailure ? 1.0 : 0.0;

            if (_samples == 0)
            {
                _ewmaLatencyMs = latencyMs;
                _ewmaErrorRate = errorSample;
            }
            else
            {
                _ewmaLatencyMs =
                    latencyAlpha * latencyMs +
                    (1 - latencyAlpha) * _ewmaLatencyMs;

                _ewmaErrorRate =
                    errorAlpha * errorSample +
                    (1 - errorAlpha) * _ewmaErrorRate;
            }

            _samples++;

            if (!success)
            {
                _failures++;
            }

            if (destinationFailure)
            {
                _destinationFailures++;
            }

            IncrementOutcome(outcome);
        }
    }

    private static bool IsDestinationFailure(
        DestinationRequestOutcome outcome)
    {
        return outcome is
            DestinationRequestOutcome.HttpServerError or
            DestinationRequestOutcome.ForwarderFailure or
            DestinationRequestOutcome.Timeout or
            DestinationRequestOutcome.UnhandledException;
    }

    private void IncrementOutcome(
        DestinationRequestOutcome outcome)
    {
        switch (outcome)
        {
            case DestinationRequestOutcome.Success:
                _successes++;
                break;
            case DestinationRequestOutcome.HttpClientError:
                _httpClientErrors++;
                break;
            case DestinationRequestOutcome.HttpServerError:
                _httpServerErrors++;
                break;
            case DestinationRequestOutcome.OtherHttpFailure:
                _otherHttpFailures++;
                break;
            case DestinationRequestOutcome.ForwarderFailure:
                _forwarderFailures++;
                break;
            case DestinationRequestOutcome.Timeout:
                _timeouts++;
                break;
            case DestinationRequestOutcome.Canceled:
                _cancellations++;
                break;
            case DestinationRequestOutcome.UnhandledException:
                _unhandledExceptions++;
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(outcome),
                    outcome,
                    "Unknown destination request outcome.");
        }
    }
}
