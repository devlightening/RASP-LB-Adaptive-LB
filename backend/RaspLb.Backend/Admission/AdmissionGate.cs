namespace RaspLb.Backend.Admission;

public enum AdmissionResult
{
    Admitted,
    RejectedQueueFull,
    RejectedPredictedWait,
    RejectedQueueTimeout
}

public readonly record struct AdmissionSnapshot(
    int Capacity,
    int Active,
    int Waiting,
    double EwmaServiceMs,
    double EstimatedQueueWaitMs,
    bool SheddingEnabled,
    int MaxQueueLength,
    int QueueTimeoutMs,
    long Admitted,
    long RejectedQueueFull,
    long RejectedPredictedWait,
    long RejectedQueueTimeout);

// Backend'in kapasite kapısı. Önceden controller'daki çıplak bir
// SemaphoreSlim'di; şimdi kuyrukta kaç isteğin beklediğini bilen,
// kuyruğu sınırlayabilen (load shedding) ve bekleme süresini tahmin
// eden tek bir yer. Brownout da aşırı yük sinyalini buradan okur.
public sealed class AdmissionGate
{
    private const double ServiceTimeAlpha = 0.2;

    private readonly SemaphoreSlim _slots;
    private readonly int _capacity;
    private readonly bool _sheddingEnabled;
    private readonly int _maxQueueLength;
    private readonly int _queueTimeoutMs;

    private int _active;
    private int _waiting;
    private long _admitted;
    private long _rejectedQueueFull;
    private long _rejectedPredictedWait;
    private long _rejectedQueueTimeout;

    private readonly object _serviceLock = new();
    private double _ewmaServiceMs;

    public AdmissionGate(
        int capacity,
        bool sheddingEnabled,
        int maxQueueLength,
        int queueTimeoutMs,
        double initialServiceMs)
    {
        _capacity = Math.Max(1, capacity);
        _slots = new SemaphoreSlim(_capacity, _capacity);
        _sheddingEnabled = sheddingEnabled;
        _maxQueueLength = Math.Max(0, maxQueueLength);
        _queueTimeoutMs = Math.Max(1, queueTimeoutMs);
        _ewmaServiceMs = Math.Max(1, initialServiceMs);
    }

    public int Capacity => _capacity;

    public async Task<AdmissionResult> EnterAsync(
        CancellationToken cancellationToken)
    {
        // Fast path: boş slot varsa kuyruğa hiç girmeden geç.
        if (_slots.Wait(0))
        {
            OnAdmitted();
            return AdmissionResult.Admitted;
        }

        var waiting = Interlocked.Increment(ref _waiting);

        try
        {
            if (_sheddingEnabled && waiting > _maxQueueLength)
            {
                Interlocked.Increment(ref _rejectedQueueFull);
                return AdmissionResult.RejectedQueueFull;
            }

            // Fail fast: if the requests already queued ahead of this one
            // will not drain before the queue timeout, waiting is pointless -
            // the caller would get the same 503, just 250 ms later and after
            // holding a queue spot that could have gone to someone else.
            if (_sheddingEnabled && EstimatedQueueWaitMs() > _queueTimeoutMs)
            {
                Interlocked.Increment(ref _rejectedPredictedWait);
                return AdmissionResult.RejectedPredictedWait;
            }

            if (!_sheddingEnabled)
            {
                await _slots.WaitAsync(cancellationToken);
            }
            else if (!await _slots.WaitAsync(_queueTimeoutMs, cancellationToken))
            {
                Interlocked.Increment(ref _rejectedQueueTimeout);
                return AdmissionResult.RejectedQueueTimeout;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }

        OnAdmitted();
        return AdmissionResult.Admitted;
    }

    public void Exit(
        double serviceMs)
    {
        lock (_serviceLock)
        {
            _ewmaServiceMs =
                ServiceTimeAlpha * serviceMs +
                (1 - ServiceTimeAlpha) * _ewmaServiceMs;
        }

        Interlocked.Decrement(ref _active);
        _slots.Release();
    }

    // Little's law yaklaşımı: önümdeki N istek, kapasite kadar paralel
    // işleniyor ve her biri ortalama S ms sürüyor => ~N * S / C ms beklerim.
    // Anlık hesaplandığı için trafik kesilince kendiliğinden 0'a düşer.
    public double EstimatedQueueWaitMs()
    {
        double serviceMs;

        lock (_serviceLock)
        {
            serviceMs = _ewmaServiceMs;
        }

        return Volatile.Read(ref _waiting) * serviceMs / _capacity;
    }

    public AdmissionSnapshot GetSnapshot()
    {
        double serviceMs;

        lock (_serviceLock)
        {
            serviceMs = _ewmaServiceMs;
        }

        return new AdmissionSnapshot(
            Capacity: _capacity,
            Active: Volatile.Read(ref _active),
            Waiting: Volatile.Read(ref _waiting),
            EwmaServiceMs: Math.Round(serviceMs, 1),
            EstimatedQueueWaitMs: Math.Round(EstimatedQueueWaitMs(), 1),
            SheddingEnabled: _sheddingEnabled,
            MaxQueueLength: _maxQueueLength,
            QueueTimeoutMs: _queueTimeoutMs,
            Admitted: Interlocked.Read(ref _admitted),
            RejectedQueueFull: Interlocked.Read(ref _rejectedQueueFull),
            RejectedPredictedWait: Interlocked.Read(ref _rejectedPredictedWait),
            RejectedQueueTimeout: Interlocked.Read(ref _rejectedQueueTimeout));
    }

    private void OnAdmitted()
    {
        Interlocked.Increment(ref _active);
        Interlocked.Increment(ref _admitted);
    }
}
