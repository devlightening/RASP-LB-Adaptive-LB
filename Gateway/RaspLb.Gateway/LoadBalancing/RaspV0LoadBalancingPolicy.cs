using Yarp.ReverseProxy.LoadBalancing;
using Yarp.ReverseProxy.Model;

namespace RaspLb.Gateway.LoadBalancing;

public sealed class RaspV0LoadBalancingPolicy : ILoadBalancingPolicy
{
    private readonly DestinationMetricsStore _metrics;
    private readonly ILogger<RaspV0LoadBalancingPolicy> _logger;

    private int _warmupCounter;

    private const int MinimumSamplesPerDestination = 5;

    private const double ExplorationRate = 0.10;

    public RaspV0LoadBalancingPolicy(
        DestinationMetricsStore metrics,
        ILogger<RaspV0LoadBalancingPolicy> logger)
    {
        _metrics = metrics;
        _logger = logger;
    }

    public string Name => "RaspV0";

    public DestinationState? PickDestination(
        HttpContext context,
        ClusterState cluster,
        IReadOnlyList<DestinationState> availableDestinations)
    {
        if (availableDestinations.Count == 0)
        {
            _logger.LogWarning(
                "RASP-v0 could not find any available destination.");

            return null;
        }

        if (availableDestinations.Count == 1)
        {
            return availableDestinations[0];
        }

        /*
         * PHASE 1 - WARM-UP
         *
         * Do not trust a backend based on a single measurement.
         *
         * Every destination must collect a minimum number of
         * samples before latency-based routing starts.
         */
        var warmupDestinations = availableDestinations
            .Where(destination =>
                _metrics.Get(destination).Samples
                < MinimumSamplesPerDestination)
            .ToArray();

        if (warmupDestinations.Length > 0)
        {
            /*
             * Prefer destinations that currently have the
             * fewest measurements.
             */
            var minimumSampleCount = warmupDestinations
                .Min(destination =>
                    _metrics.Get(destination).Samples);

            var leastObservedDestinations =
                warmupDestinations
                    .Where(destination =>
                        _metrics.Get(destination).Samples
                        == minimumSampleCount)
                    .ToArray();

            /*
             * If multiple destinations have the same sample
             * count, rotate between them.
             */
            var counter =
                Interlocked.Increment(ref _warmupCounter);

            var index =
                (counter & int.MaxValue)
                % leastObservedDestinations.Length;

            var selected =
                leastObservedDestinations[index];

            _logger.LogInformation(
                "RASP-v0 warm-up selected {Destination}. Samples: {Samples}",
                selected.DestinationId,
                BuildMetricsLog(availableDestinations));

            return selected;
        }

        /*
         * PHASE 2 - EXPLORATION
         *
         * Occasionally probe another backend.
         *
         * Without exploration, a backend that improves later
         * might never be observed again.
         */
        if (Random.Shared.NextDouble() < ExplorationRate)
        {
            var selected =
                availableDestinations[
                    Random.Shared.Next(
                        availableDestinations.Count)];

            _logger.LogInformation(
                "RASP-v0 exploration selected {Destination}. Metrics: {Metrics}",
                selected.DestinationId,
                BuildMetricsLog(availableDestinations));

            return selected;
        }

        /*
         * PHASE 3 - EXPLOITATION
         *
         * Select the backend with the lowest observed
         * EWMA latency.
         */
        var winner = availableDestinations
            .OrderBy(destination =>
                _metrics.Get(destination).EwmaLatencyMs)
            .First();

        _logger.LogInformation(
            "RASP-v0 exploitation selected {Destination}. Metrics: {Metrics}",
            winner.DestinationId,
            BuildMetricsLog(availableDestinations));

        return winner;
    }

    private string BuildMetricsLog(
        IReadOnlyList<DestinationState> destinations)
    {
        return string.Join(
            ", ",
            destinations.Select(destination =>
            {
                var metrics = _metrics.Get(destination);

                return
                    $"{destination.DestinationId}" +
                    $"=[Samples:{metrics.Samples}, " +
                    $"EWMA:{metrics.EwmaLatencyMs:F2}ms, " +
                    $"Failures:{metrics.Failures}]";
            }));
    }
}