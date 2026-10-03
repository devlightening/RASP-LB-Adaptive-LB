using RaspLb.Backend.Brownout;

namespace RaspLb.Tests;

public class BrownoutStateTests
{
    private long _nowMs;

    private BrownoutState Create(
        bool enabled = true,
        double targetQueueWaitMs = 40,
        double draw = 0.5) =>
        new(enabled, targetQueueWaitMs, () => _nowMs, () => draw);

    // Feeds the same signal every 50 ms for the given duration.
    private void Run(
        BrownoutState state,
        double queueWaitMs,
        int durationMs)
    {
        for (var t = 0; t < durationMs; t += 50)
        {
            _nowMs += 50;
            state.ShouldReduce(queueWaitMs);
        }
    }

    [Fact]
    public void Stays_at_full_enrichment_while_the_queue_is_below_target()
    {
        var state = Create();

        Run(state, queueWaitMs: 20, durationMs: 5_000);

        var snapshot = state.GetSnapshot(20);
        Assert.Equal(1.0, snapshot.Dimmer);
        Assert.Equal(0, snapshot.ReducedModeRequests);
        Assert.Equal(0, snapshot.Activations);
    }

    [Fact]
    public void Dims_under_sustained_queueing_and_skips_enrichment()
    {
        var state = Create(draw: 0.5);

        Run(state, queueWaitMs: 200, durationMs: 2_000);

        var snapshot = state.GetSnapshot(200);
        Assert.True(snapshot.Dimmer < 0.5);
        Assert.True(snapshot.ReducedModeActive);
        Assert.Equal(1, snapshot.Activations);
        Assert.True(state.ShouldReduce(200)); // draw 0.5 >= dimmer
    }

    [Fact]
    public void A_short_spike_only_dims_slightly_and_recovers()
    {
        var state = Create();

        Run(state, queueWaitMs: 300, durationMs: 50);
        Assert.True(state.GetSnapshot(300).Dimmer >= 0.85);

        Run(state, queueWaitMs: 0, durationMs: 1_000);
        Assert.Equal(1.0, state.GetSnapshot(0).Dimmer);
    }

    [Fact]
    public void Recovers_gradually_after_overload_ends()
    {
        var state = Create();
        Run(state, queueWaitMs: 200, durationMs: 2_000);
        var dimmed = state.GetSnapshot(200).Dimmer;

        // The smoothed signal needs ~0.25 s to fall below target from 200 ms,
        // so θ only starts climbing after that - a brief lull is not recovery.
        Run(state, queueWaitMs: 0, durationMs: 100);
        Assert.Equal(dimmed, state.GetSnapshot(0).Dimmer);

        Run(state, queueWaitMs: 0, durationMs: 1_000);
        var partly = state.GetSnapshot(0).Dimmer;

        Run(state, queueWaitMs: 0, durationMs: 3_000);
        var snapshot = state.GetSnapshot(0);

        Assert.True(partly > dimmed && partly < 1.0);
        Assert.Equal(1.0, snapshot.Dimmer);
        Assert.False(snapshot.ReducedModeActive);
    }

    [Fact]
    public void Snapshot_advances_the_controller_without_traffic()
    {
        var state = Create();
        Run(state, queueWaitMs: 200, durationMs: 2_000);

        for (var i = 0; i < 40; i++)
        {
            _nowMs += 100;
            state.GetSnapshot(0);
        }

        Assert.False(state.GetSnapshot(0).ReducedModeActive);
    }

    [Fact]
    public void Never_reduces_when_disabled()
    {
        var state = Create(enabled: false);

        Run(state, queueWaitMs: 10_000, durationMs: 2_000);

        var snapshot = state.GetSnapshot(10_000);
        Assert.Equal(1.0, snapshot.Dimmer);
        Assert.Equal(0, snapshot.ReducedModeRequests);
    }
}
