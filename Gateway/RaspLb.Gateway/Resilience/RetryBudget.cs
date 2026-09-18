using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace RaspLb.Gateway.Resilience;

public sealed class RetryBudget
{
    private readonly object _lock = new();
    private readonly int _capacity;
    private readonly int _tokensPerSecond;

    private double _tokens;
    private long _lastRefillTimestamp;

    public RetryBudget(
        IOptions<RaspRetryOptions> options)
    {
        _capacity = options.Value.BurstCapacity;
        _tokensPerSecond = options.Value.TokensPerSecond;
        _tokens = _capacity;
        _lastRefillTimestamp = Stopwatch.GetTimestamp();
    }

    public bool TryAcquire()
    {
        lock (_lock)
        {
            Refill();

            if (_tokens < 1)
            {
                return false;
            }

            _tokens--;
            return true;
        }
    }

    private void Refill()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsedSeconds =
            (double)(now - _lastRefillTimestamp) /
            Stopwatch.Frequency;

        _tokens = Math.Min(
            _capacity,
            _tokens + elapsedSeconds * _tokensPerSecond);

        _lastRefillTimestamp = now;
    }
}
