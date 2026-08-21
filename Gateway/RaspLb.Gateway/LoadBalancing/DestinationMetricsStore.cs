using System.Collections.Concurrent;
using Yarp.ReverseProxy.Model;

namespace RaspLb.Gateway.LoadBalancing;

public sealed class DestinationMetricsStore
{
    private readonly ConcurrentDictionary<string, DestinationMetrics> _metrics
        = new();

    public DestinationMetrics Get(DestinationState destination)
    {
        return _metrics.GetOrAdd(
            destination.DestinationId,
            _ => new DestinationMetrics());
    }

    public void Record(
        DestinationState destination,
        double latencyMs,
        bool success)
    {
        Get(destination).Record(latencyMs, success);
    }

    public IReadOnlyDictionary<string, DestinationMetrics> GetAll()
    {
        return _metrics;
    }
}