using Yarp.ReverseProxy.LoadBalancing;
using Yarp.ReverseProxy.Model;

namespace RaspLb.Gateway.LoadBalancing;

public sealed class RaspV1LoadBalancingPolicy
    : ILoadBalancingPolicy
{
    private readonly DestinationMetricsStore _metrics;
    private readonly ILogger<RaspV1LoadBalancingPolicy> _logger;

    private int _warmupCounter;

    private const int MinimumSamplesPerDestination = 5;

    private const double ExplorationRate = 0.10;

    private const double ErrorPenaltyWeight = 4.0;

    public RaspV1LoadBalancingPolicy(
        DestinationMetricsStore metrics,
        ILogger<RaspV1LoadBalancingPolicy> logger)
    {
        _metrics = metrics;
        _logger = logger;
    }

    public string Name => "RaspV1";

    public DestinationState? PickDestination(
        HttpContext context,
        ClusterState cluster,
        IReadOnlyList<DestinationState> availableDestinations)
    {
        if (availableDestinations.Count == 0)
        {
            _logger.LogWarning(
                "RASP-v1 could not find any available destination.");

            return null;
        }

        if (availableDestinations.Count == 1)
        {
            return availableDestinations[0];
        }

        /*
         * PHASE 1 - WARM-UP
         */
        var warmupDestinations =
            availableDestinations
                .Where(destination =>
                    _metrics.Get(destination).Samples
                    < MinimumSamplesPerDestination)
                .ToArray();

        if (warmupDestinations.Length > 0)
        {
            var minimumSampleCount =
                warmupDestinations.Min(destination =>
                    _metrics.Get(destination).Samples);

            var leastObserved =
                warmupDestinations
                    .Where(destination =>
                        _metrics.Get(destination).Samples
                        == minimumSampleCount)
                    .ToArray();

            var counter =
                Interlocked.Increment(
                    ref _warmupCounter);

            var index =
                (counter & int.MaxValue)
                % leastObserved.Length;

            var selected =
                leastObserved[index];

            _logger.LogInformation(
                "RASP-v1 warm-up selected {Destination}. Metrics: {Metrics}",
                selected.DestinationId,
                BuildMetricsLog(availableDestinations));

            return selected;
        }

        /*
         * PHASE 2 - EXPLORATION
         */
        if (Random.Shared.NextDouble() < ExplorationRate)
        {
            var selected =
                availableDestinations[
                    Random.Shared.Next(
                        availableDestinations.Count)];

            _logger.LogInformation(
                "RASP-v1 exploration selected {Destination}. Metrics: {Metrics}",
                selected.DestinationId,
                BuildMetricsLog(availableDestinations));

            return selected;
        }

        /*
         * PHASE 3 - EXPLOITATION
         *
         * Score =
         * EWMA Latency
         * ×
         * (1 + ErrorPenaltyWeight × EWMA Error Rate)
         */
        var winner =
            availableDestinations
                .OrderBy(CalculateScore)
                .First();

        _logger.LogInformation(
            "RASP-v1 selected {Destination}. Metrics: {Metrics}",
            winner.DestinationId,
            BuildMetricsLog(availableDestinations));

        return winner;
    }

    private double CalculateScore(
        DestinationState destination)
    {
        var metrics =
            _metrics.Get(destination);

        return
            metrics.EwmaLatencyMs *
            (
                1 +
                ErrorPenaltyWeight *
                metrics.EwmaErrorRate
            );
    }

    private string BuildMetricsLog(
        IReadOnlyList<DestinationState> destinations)
    {
        return string.Join(
            ", ",
            destinations.Select(destination =>
            {
                var metrics =
                    _metrics.Get(destination);

                var score =
                    CalculateScore(destination);

                return
                    $"{destination.DestinationId}" +
                    $"=[Samples:{metrics.Samples}, " +
                    $"LatencyEWMA:{metrics.EwmaLatencyMs:F2}ms, " +
                    $"ErrorEWMA:{metrics.EwmaErrorRate:P2}, " +
                    $"Failures:{metrics.Failures}, " +
                    $"Score:{score:F2}]";
            }));
    }
}