using Yarp.ReverseProxy.Model;

namespace RaspLb.Gateway.LoadBalancing;

// RASP v3 = v2's score (EWMA latency x load factor x error penalty) with
// "power of two choices" selection instead of a global argmin.
//
// v2 sends every request to the single best-scoring backend. Its score lags
// reality (the EWMA only moves after responses come back), so all traffic
// herds onto whichever backend looked best a moment ago until its latency
// catches up - then herds onto the next one. Measured as a ~5 s cycle
// between backend2 and backend3 at 220 req/s (SUMMARY-brownout-dimmer.md).
//
// Comparing two random candidates still avoids clearly bad backends (the
// worst of N never wins a pair) but spreads load, so a stale score can no
// longer pull everything in one direction (Mitzenmacher, "The Power of Two
// Choices in Randomized Load Balancing").
public sealed class RaspV3LoadBalancingPolicy : RaspV2LoadBalancingPolicy
{
    public RaspV3LoadBalancingPolicy(
        DestinationMetricsStore metrics,
        ILogger<RaspV3LoadBalancingPolicy> logger)
        : base(metrics, logger)
    {
    }

    public override string Name => "RaspV3";

    protected override DestinationState SelectWinner(
        IReadOnlyList<DestinationState> candidates)
    {
        if (candidates.Count <= 2)
        {
            return base.SelectWinner(candidates);
        }

        var first = Random.Shared.Next(candidates.Count);
        var second = Random.Shared.Next(candidates.Count - 1);

        if (second >= first)
        {
            second++;
        }

        var a = candidates[first];
        var b = candidates[second];

        return CalculateScore(a) <= CalculateScore(b) ? a : b;
    }
}
