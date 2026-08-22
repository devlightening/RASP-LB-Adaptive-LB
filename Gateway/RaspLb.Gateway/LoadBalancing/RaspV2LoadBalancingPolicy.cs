using Yarp.ReverseProxy.LoadBalancing;
using Yarp.ReverseProxy.Model;

namespace RaspLb.Gateway.LoadBalancing;

public sealed class RaspV2LoadBalancingPolicy : ILoadBalancingPolicy
{
	private readonly DestinationMetricsStore _metrics;
	private readonly ILogger<RaspV2LoadBalancingPolicy> _logger;

	private int _warmupCounter;

	private const int MinimumSamplesPerDestination = 5;
	private const double ExplorationRate = 0.10;
	private const double ErrorPenaltyWeight = 4.0;

	public RaspV2LoadBalancingPolicy(
		DestinationMetricsStore metrics,
		ILogger<RaspV2LoadBalancingPolicy> logger)
	{
		_metrics = metrics;
		_logger = logger;
	}

	public string Name => "RaspV2";

	public DestinationState? PickDestination(
		HttpContext context,
		ClusterState cluster,
		IReadOnlyList<DestinationState> availableDestinations)
	{
		if (availableDestinations.Count == 0)
		{
			_logger.LogWarning(
				"RASP-v2 could not find any available destination.");

			return null;
		}

		if (availableDestinations.Count == 1)
		{
			return availableDestinations[0];
		}

		// --------------------------------------------------
		// PHASE 1 - WARM-UP
		// --------------------------------------------------

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
				"RASP-v2 warm-up selected {Destination}.",
				selected.DestinationId);

			return selected;
		}

		// --------------------------------------------------
		// PHASE 2 - EXPLORATION
		// --------------------------------------------------

		if (Random.Shared.NextDouble() < ExplorationRate)
		{
			var selected =
				availableDestinations[
					Random.Shared.Next(
						availableDestinations.Count)];

			_logger.LogInformation(
				"RASP-v2 exploration selected {Destination}.",
				selected.DestinationId);

			return selected;
		}

		// --------------------------------------------------
		// PHASE 3 - PREDICTIVE EXPLOITATION
		// --------------------------------------------------

		var winner =
			availableDestinations
				.OrderBy(CalculateScore)
				.First();

		_logger.LogInformation(
			"RASP-v2 selected {Destination}. Metrics: {Metrics}",
			winner.DestinationId,
			BuildMetricsLog(availableDestinations));

		return winner;
	}

	private double CalculateScore(
		DestinationState destination)
	{
		var metrics =
			_metrics.Get(destination);

		var capacity =
			GetCapacity(destination);

		var inFlight =
			destination.ConcurrentRequestCount;

		var loadFactor =
			1.0 +
			((double)inFlight / capacity);

		var predictedCompletion =
			metrics.EwmaLatencyMs *
			loadFactor;

		var errorPenalty =
			1.0 +
			ErrorPenaltyWeight *
			metrics.EwmaErrorRate;

		return
			predictedCompletion *
			errorPenalty;
	}

	private static int GetCapacity(
		DestinationState destination)
	{
		var metadata =
			destination.Model.Config.Metadata;

		if (metadata is not null &&
			metadata.TryGetValue(
				"RaspCapacity",
				out var rawCapacity) &&
			int.TryParse(
				rawCapacity,
				out var capacity) &&
			capacity > 0)
		{
			return capacity;
		}

		return 1;
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

				var capacity =
					GetCapacity(destination);

				var inFlight =
					destination.ConcurrentRequestCount;

				var loadFactor =
					1.0 +
					((double)inFlight / capacity);

				var predictedCompletion =
					metrics.EwmaLatencyMs *
					loadFactor;

				var score =
					CalculateScore(destination);

				return
					$"{destination.DestinationId}" +
					$"=[Samples:{metrics.Samples}, " +
					$"LatencyEWMA:{metrics.EwmaLatencyMs:F2}ms, " +
					$"ErrorEWMA:{metrics.EwmaErrorRate:P2}, " +
					$"InFlight:{inFlight}, " +
					$"Capacity:{capacity}, " +
					$"Predicted:{predictedCompletion:F2}ms, " +
					$"Score:{score:F2}]";
			}));
	}
}