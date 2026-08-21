namespace RaspLb.Gateway.LoadBalancing;

public sealed class DestinationMetrics
{
    private readonly object _lock = new();

    private long _samples;

    private double _ewmaLatencyMs;
    private double _ewmaErrorRate;

    private long _failures;

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

    public void Record(
        double latencyMs,
        bool success)
    {
        const double latencyAlpha = 0.20;
        const double errorAlpha = 0.20;

        lock (_lock)
        {
            var errorSample =
                success ? 0.0 : 1.0;

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
        }
    }
}