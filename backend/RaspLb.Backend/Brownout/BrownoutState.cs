namespace RaspLb.Backend.Brownout;

public readonly record struct BrownoutSnapshot(
    bool Enabled,
    bool ReducedModeActive,
    double Dimmer,
    double LastQueueWaitMs,
    double SmoothedQueueWaitMs,
    double ActivationQueueWaitMs,
    long FullModeRequests,
    long ReducedModeRequests,
    long Activations,
    double TripQueueWaitMs);

// Decides, per request, whether to do the optional "enrichment" work.
//
// Instead of an on/off switch this is a dimmer (Klein et al., "Brownout",
// ICSE 2014): each request does the enrichment with probability θ, and a
// small controller moves θ so the backend's queue wait stays near a target.
// An on/off switch oscillates in the band where full mode is too slow but
// reduced mode is more than enough - it trips, the queue drains, it recovers,
// the queue builds again. The dimmer instead settles at the θ where capacity
// matches the load.
//
// The signal is the estimated queue wait (see AdmissionGate), not raw
// utilization: a load balancer is supposed to keep backends busy, so "80% of
// slots in use" is the normal operating point, not overload.
public sealed class BrownoutState
{
    // Smoothing time constant for the instantaneous queue-wait estimate.
    // Kept short: a lagging signal makes the integral controller overshoot
    // into a limit cycle (500 ms measured worse than 150 ms at 220 req/s).
    private const double SmoothingTauMs = 150;

    // θ change per second at 100% error. Dimming down is urgent (requests
    // are already queueing), brightening up can be gentle.
    private const double GainDownPerSecond = 2.0;
    private const double GainUpPerSecond = 0.5;

    // Reporting hysteresis only (the controller itself is continuous):
    // "brownout active" once θ < 0.9, "recovered" once θ is back at ≥ 0.99.
    private const double ActiveBelow = 0.9;
    private const double RecoveredAtOrAbove = 0.99;

    private readonly object _lock = new();

    private readonly bool _enabled;
    private readonly double _targetQueueWaitMs;
    private readonly Func<long> _clockMs;
    private readonly Func<double> _random;

    private double _dimmer = 1.0;
    private double _smoothedQueueWaitMs;
    private double _lastQueueWaitMs;
    private double _activationQueueWaitMs;
    private long _lastUpdateMs;
    private bool _reportedActive;
    private long _fullModeRequests;
    private long _reducedModeRequests;
    private long _activations;

    public BrownoutState(
        bool enabled,
        double targetQueueWaitMs,
        Func<long>? clockMs = null,
        Func<double>? random = null)
    {
        _enabled = enabled;
        _targetQueueWaitMs = Math.Max(1, targetQueueWaitMs);
        _clockMs = clockMs ?? (() => Environment.TickCount64);
        _random = random ?? Random.Shared.NextDouble;
        _lastUpdateMs = _clockMs();
    }

    // Called for every request: updates the controller, then draws whether
    // this particular request skips the enrichment.
    public bool ShouldReduce(
        double queueWaitMs)
    {
        lock (_lock)
        {
            Update(queueWaitMs);

            var reduce = _random() >= _dimmer;

            if (reduce)
            {
                _reducedModeRequests++;
            }
            else
            {
                _fullModeRequests++;
            }

            return reduce;
        }
    }

    // Also advances the controller with the current signal, so θ recovers
    // once traffic stops instead of staying frozen at the last request's view.
    public BrownoutSnapshot GetSnapshot(
        double currentQueueWaitMs)
    {
        lock (_lock)
        {
            Update(currentQueueWaitMs);

            return new BrownoutSnapshot(
                Enabled: _enabled,
                ReducedModeActive: _reportedActive,
                Dimmer: Math.Round(_dimmer, 3),
                LastQueueWaitMs: Math.Round(_lastQueueWaitMs, 1),
                SmoothedQueueWaitMs: Math.Round(_smoothedQueueWaitMs, 1),
                ActivationQueueWaitMs: Math.Round(_activationQueueWaitMs, 1),
                FullModeRequests: _fullModeRequests,
                ReducedModeRequests: _reducedModeRequests,
                Activations: _activations,
                TripQueueWaitMs: _targetQueueWaitMs);
        }
    }

    private void Update(
        double queueWaitMs)
    {
        _lastQueueWaitMs = queueWaitMs;

        if (!_enabled)
        {
            return;
        }

        var now = _clockMs();
        var dtMs = Math.Max(0, now - _lastUpdateMs);
        _lastUpdateMs = now;

        if (dtMs == 0)
        {
            return;
        }

        var alpha = 1 - Math.Exp(-dtMs / SmoothingTauMs);
        _smoothedQueueWaitMs += alpha * (queueWaitMs - _smoothedQueueWaitMs);

        // Positive: room to spare -> brighten. Negative: queue too long -> dim.
        // Clamped so one huge spike cannot slam θ to zero in a single step.
        var error = Math.Clamp(
            (_targetQueueWaitMs - _smoothedQueueWaitMs) / _targetQueueWaitMs,
            -1,
            1);

        var gain = error < 0 ? GainDownPerSecond : GainUpPerSecond;
        _dimmer = Math.Clamp(_dimmer + gain * error * dtMs / 1000.0, 0, 1);

        if (!_reportedActive && _dimmer < ActiveBelow)
        {
            _reportedActive = true;
            _activations++;
            _activationQueueWaitMs = _smoothedQueueWaitMs;
        }
        else if (_reportedActive && _dimmer >= RecoveredAtOrAbove)
        {
            _reportedActive = false;
        }
    }
}
