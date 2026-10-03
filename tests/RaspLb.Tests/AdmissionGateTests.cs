using RaspLb.Backend.Admission;

namespace RaspLb.Tests;

public class AdmissionGateTests
{
    [Fact]
    public async Task Admits_immediately_while_slots_are_free()
    {
        var gate = CreateGate(capacity: 2);

        Assert.Equal(AdmissionResult.Admitted, await gate.EnterAsync(RequestPriority.Normal, default));
        Assert.Equal(AdmissionResult.Admitted, await gate.EnterAsync(RequestPriority.Normal, default));
        Assert.Equal(2, gate.GetSnapshot().Active);
    }

    [Fact]
    public async Task Sheddable_is_rejected_at_a_shorter_queue_than_critical()
    {
        // capacity 1, queue 10: sheddable may use ceil(10 * 0.3) = 3 spots.
        var gate = CreateGate(capacity: 1, maxQueueLength: 10, queueTimeoutMs: 10_000, serviceMs: 1);
        await gate.EnterAsync(RequestPriority.Critical, default);

        var queued = Enumerable.Range(0, 3)
            .Select(_ => gate.EnterAsync(RequestPriority.Critical, default))
            .ToList();

        Assert.Equal(
            AdmissionResult.RejectedQueueFull,
            await gate.EnterAsync(RequestPriority.Sheddable, default));

        var critical = gate.EnterAsync(RequestPriority.Critical, default);
        Assert.False(critical.IsCompleted);

        Drain(gate, queued.Append(critical).ToList());
        await Task.WhenAll(queued.Append(critical));
    }

    [Fact]
    public async Task Rejects_at_the_door_when_predicted_wait_exceeds_timeout()
    {
        // 100 ms per request, 1 slot, 250 ms timeout: the third waiter would
        // wait ~300 ms, so it is rejected without waiting at all.
        var gate = CreateGate(capacity: 1, maxQueueLength: 100, queueTimeoutMs: 250, serviceMs: 100);
        await gate.EnterAsync(RequestPriority.Critical, default);

        var first = gate.EnterAsync(RequestPriority.Critical, default);
        var second = gate.EnterAsync(RequestPriority.Critical, default);

        var third = gate.EnterAsync(RequestPriority.Critical, default);
        Assert.True(third.IsCompleted);
        Assert.Equal(AdmissionResult.RejectedPredictedWait, await third);

        Drain(gate, [first, second]);
        await Task.WhenAll(first, second);
    }

    [Fact]
    public async Task Rejects_at_the_door_when_the_deadline_leaves_no_time_to_wait()
    {
        // 100 ms of work and 120 ms of deadline left: at most 20 ms may be
        // spent queueing, but one request ahead means ~100 ms of waiting.
        var gate = CreateGate(capacity: 1, maxQueueLength: 10, queueTimeoutMs: 250, serviceMs: 100);
        await gate.EnterAsync(RequestPriority.Critical, default);

        var result = gate.EnterAsync(RequestPriority.Critical, default, remainingDeadlineMs: 120);

        Assert.True(result.IsCompleted);
        Assert.Equal(AdmissionResult.RejectedPredictedWait, await result);
    }

    [Fact]
    public async Task Deadline_does_not_reject_when_a_slot_is_free()
    {
        var gate = CreateGate(capacity: 1, serviceMs: 100);

        Assert.Equal(
            AdmissionResult.Admitted,
            await gate.EnterAsync(RequestPriority.Normal, default, remainingDeadlineMs: 10));
    }

    [Fact]
    public async Task Queues_normally_when_the_deadline_leaves_enough_time()
    {
        var gate = CreateGate(capacity: 1, maxQueueLength: 10, queueTimeoutMs: 250, serviceMs: 100);
        await gate.EnterAsync(RequestPriority.Critical, default);

        var queued = gate.EnterAsync(RequestPriority.Critical, default, remainingDeadlineMs: 450);
        Assert.False(queued.IsCompleted);

        gate.Exit(1);
        Assert.Equal(AdmissionResult.Admitted, await queued);
    }

    [Fact]
    public async Task Rejects_after_waiting_past_the_queue_timeout()
    {
        var gate = CreateGate(capacity: 1, maxQueueLength: 10, queueTimeoutMs: 50, serviceMs: 1);
        await gate.EnterAsync(RequestPriority.Critical, default);

        Assert.Equal(
            AdmissionResult.RejectedQueueTimeout,
            await gate.EnterAsync(RequestPriority.Critical, default));
    }

    [Fact]
    public async Task Never_rejects_when_shedding_is_disabled()
    {
        var gate = CreateGate(capacity: 1, maxQueueLength: 0, queueTimeoutMs: 1, serviceMs: 1000, shedding: false);
        await gate.EnterAsync(RequestPriority.Sheddable, default);

        var queued = gate.EnterAsync(RequestPriority.Sheddable, default);
        Assert.False(queued.IsCompleted);

        gate.Exit(1);
        Assert.Equal(AdmissionResult.Admitted, await queued);
    }

    [Fact]
    public async Task Counts_admissions_and_rejections_per_priority()
    {
        var gate = CreateGate(capacity: 1, maxQueueLength: 0, queueTimeoutMs: 10, serviceMs: 1);
        await gate.EnterAsync(RequestPriority.Critical, default);
        await gate.EnterAsync(RequestPriority.Sheddable, default);

        var byPriority = gate.GetSnapshot().ByPriority.ToDictionary(p => p.Priority);
        Assert.Equal(1, byPriority[RequestPriority.Critical].Admitted);
        Assert.Equal(1, byPriority[RequestPriority.Sheddable].Rejected);
    }

    private static AdmissionGate CreateGate(
        int capacity,
        int maxQueueLength = 10,
        int queueTimeoutMs = 250,
        double serviceMs = 50,
        bool shedding = true) =>
        new(capacity, shedding, maxQueueLength, queueTimeoutMs, serviceMs);

    // Releases one slot per waiting request so pending tasks can complete.
    private static void Drain(
        AdmissionGate gate,
        IReadOnlyCollection<Task<AdmissionResult>> pending)
    {
        for (var i = 0; i <= pending.Count; i++)
        {
            gate.Exit(1);
        }
    }
}
