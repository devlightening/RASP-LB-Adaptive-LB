using RaspLb.Backend.Brownout;

namespace RaspLb.Tests;

public class BrownoutStateTests
{
    [Fact]
    public void Trips_when_queue_wait_reaches_the_threshold()
    {
        var state = new BrownoutState(enabled: true, tripQueueWaitMs: 50, minDwellMs: 0);

        Assert.False(state.ShouldReduce(49));
        Assert.True(state.ShouldReduce(50));
    }

    [Fact]
    public void Recovers_only_after_the_queue_stays_calm_for_the_whole_dwell()
    {
        var state = new BrownoutState(enabled: true, tripQueueWaitMs: 50, minDwellMs: 100);
        Thread.Sleep(120); // let the initial dwell expire

        Assert.True(state.ShouldReduce(80));

        Thread.Sleep(120);
        Assert.True(state.ShouldReduce(60));  // still above recovery: calm timer restarts
        Assert.True(state.ShouldReduce(0));   // calm, but only for ~0 ms

        Thread.Sleep(60);
        Assert.True(state.ShouldReduce(0));   // calm for ~60 ms < dwell

        Thread.Sleep(60);
        Assert.False(state.ShouldReduce(0));  // calm for the whole dwell
    }

    [Fact]
    public void Snapshot_reevaluates_so_mode_does_not_stick_after_traffic_stops()
    {
        var state = new BrownoutState(enabled: true, tripQueueWaitMs: 50, minDwellMs: 50);
        Thread.Sleep(70);
        Assert.True(state.ShouldReduce(100));

        Thread.Sleep(30);
        state.GetSnapshot(0); // starts the calm period without any request
        Thread.Sleep(80);

        Assert.False(state.GetSnapshot(0).ReducedModeActive);
    }

    [Fact]
    public void Never_reduces_when_disabled()
    {
        var state = new BrownoutState(enabled: false, tripQueueWaitMs: 50, minDwellMs: 0);

        Assert.False(state.ShouldReduce(10_000));
        Assert.Equal(0, state.GetSnapshot(10_000).Activations);
    }
}
