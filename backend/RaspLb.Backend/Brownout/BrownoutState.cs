namespace RaspLb.Backend.Brownout;

public readonly record struct BrownoutSnapshot(
    bool Enabled,
    bool ReducedModeActive,
    double LastQueueWaitMs,
    double ActivationQueueWaitMs,
    long FullModeRequests,
    long ReducedModeRequests,
    long Activations,
    double TripQueueWaitMs,
    double RecoveryQueueWaitMs,
    long MinDwellMs);

// Tracks whether the backend should skip its optional "enrichment" work.
//
// The signal is the estimated queue wait (see AdmissionGate), not raw
// utilization: a load balancer is supposed to keep backends busy, so
// "80% of slots in use" is the normal operating point, not overload.
// Requests piling up in front of the gate is what overload looks like.
public sealed class BrownoutState
{
    private readonly object _lock = new();

    private readonly bool _enabled;
    private readonly double _tripQueueWaitMs;
    private readonly double _recoveryQueueWaitMs;
    private readonly long _minDwellMs;

    private bool _reducedModeActive;
    private double _lastQueueWaitMs;
    private double _activationQueueWaitMs;
    private long _lastSwitchTimestamp;
    private long _lastAboveRecoveryTimestamp;
    private long _fullModeRequests;
    private long _reducedModeRequests;
    private long _activations;

    public BrownoutState(
        bool enabled,
        double tripQueueWaitMs,
        long minDwellMs)
    {
        _enabled = enabled;
        _tripQueueWaitMs = tripQueueWaitMs;

        // Hysteresis band: only returns to full mode once the queue has
        // drained well below the trip point, not the instant it dips
        // under it - otherwise borderline load flips mode every request.
        _recoveryQueueWaitMs = tripQueueWaitMs / 2;

        _minDwellMs = minDwellMs;
        _lastSwitchTimestamp = Environment.TickCount64;
    }

    // Called for every request: decides the mode and counts it.
    public bool ShouldReduce(
        double queueWaitMs)
    {
        lock (_lock)
        {
            Evaluate(queueWaitMs);

            if (_reducedModeActive)
            {
                _reducedModeRequests++;
            }
            else
            {
                _fullModeRequests++;
            }

            return _reducedModeActive;
        }
    }

    // Re-evaluates with the current signal without counting a request,
    // so the reported mode recovers once traffic stops instead of staying
    // frozen at whatever the last request saw.
    public BrownoutSnapshot GetSnapshot(
        double currentQueueWaitMs)
    {
        lock (_lock)
        {
            Evaluate(currentQueueWaitMs);

            return new BrownoutSnapshot(
                Enabled: _enabled,
                ReducedModeActive: _reducedModeActive,
                LastQueueWaitMs: Math.Round(_lastQueueWaitMs, 1),
                ActivationQueueWaitMs: Math.Round(_activationQueueWaitMs, 1),
                FullModeRequests: _fullModeRequests,
                ReducedModeRequests: _reducedModeRequests,
                Activations: _activations,
                TripQueueWaitMs: _tripQueueWaitMs,
                RecoveryQueueWaitMs: _recoveryQueueWaitMs,
                MinDwellMs: _minDwellMs);
        }
    }

    private void Evaluate(
        double queueWaitMs)
    {
        _lastQueueWaitMs = queueWaitMs;

        if (!_enabled)
        {
            return;
        }

        var now = Environment.TickCount64;

        if (queueWaitMs >= _recoveryQueueWaitMs)
        {
            _lastAboveRecoveryTimestamp = now;
        }

        var dwellElapsed =
            now - _lastSwitchTimestamp >= _minDwellMs;

        // The estimate is instantaneous, so under heavy load it still dips
        // to ~0 for a moment whenever the queue happens to drain. Recovery
        // therefore requires the queue to have stayed calm for a whole dwell
        // period, not just one calm sample.
        var calmLongEnough =
            now - _lastAboveRecoveryTimestamp >= _minDwellMs;

        if (!_reducedModeActive &&
            queueWaitMs >= _tripQueueWaitMs &&
            dwellElapsed)
        {
            _reducedModeActive = true;
            _activationQueueWaitMs = queueWaitMs;
            _lastSwitchTimestamp = now;
            _activations++;
        }
        else if (_reducedModeActive &&
            calmLongEnough &&
            dwellElapsed)
        {
            _reducedModeActive = false;
            _lastSwitchTimestamp = now;
        }
    }
}
