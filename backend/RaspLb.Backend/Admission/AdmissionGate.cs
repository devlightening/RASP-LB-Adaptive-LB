using System.Text.Json.Serialization;

namespace RaspLb.Backend.Admission;

public enum AdmissionResult
{
    Admitted,
    RejectedQueueFull,
    RejectedPredictedWait,
    RejectedQueueTimeout
}

[JsonConverter(typeof(JsonStringEnumConverter<RequestPriority>))]
public enum RequestPriority
{
    Critical,
    Normal,
    Sheddable
}

public readonly record struct PriorityAdmissionSnapshot(
    RequestPriority Priority,
    double QueueShare,
    long Admitted,
    long Rejected);

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
    long RejectedQueueTimeout,
    IReadOnlyList<PriorityAdmissionSnapshot> ByPriority);

// Backend'in kapasite kapısı. Önceden controller'daki çıplak bir
// SemaphoreSlim'di; şimdi kuyrukta kaç isteğin beklediğini bilen,
// kuyruğu sınırlayabilen (load shedding) ve bekleme süresini tahmin
// eden tek bir yer. Brownout da aşırı yük sinyalini buradan okur.
public sealed class AdmissionGate
{
    private const double ServiceTimeAlpha = 0.2;

    // Kuyruk bütçesinin (uzunluk ve bekleme süresi) her önceliğe düşen payı.
    // Kuyruk dolmaya başlayınca önce sheddable, sonra normal istekler kapıda
    // reddedilir; kuyruğun son kısmı yalnızca critical isteklere kalır.
    private static readonly double[] QueueShares = [1.0, 0.6, 0.3];

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
    private readonly long[] _admittedByPriority = new long[3];
    private readonly long[] _rejectedByPriority = new long[3];

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

    // remainingDeadlineMs: how much of the caller's end-to-end deadline is
    // left (X-Rasp-Deadline-Ms from the gateway). Null = no deadline known.
    public async Task<AdmissionResult> EnterAsync(
        RequestPriority priority,
        CancellationToken cancellationToken,
        double? remainingDeadlineMs = null)
    {
        var result =
            await TryEnterAsync(priority, remainingDeadlineMs, cancellationToken);

        var index = (int)priority;

        if (result == AdmissionResult.Admitted)
        {
            Interlocked.Increment(ref _admittedByPriority[index]);
        }
        else
        {
            Interlocked.Increment(ref _rejectedByPriority[index]);
        }

        return result;
    }

    private async Task<AdmissionResult> TryEnterAsync(
        RequestPriority priority,
        double? remainingDeadlineMs,
        CancellationToken cancellationToken)
    {
        // Fast path: boş slot varsa kuyruğa hiç girmeden geç.
        if (_slots.Wait(0))
        {
            OnAdmitted();
            return AdmissionResult.Admitted;
        }

        var share = QueueShares[(int)priority];
        var maxQueueLength = (int)Math.Ceiling(_maxQueueLength * share);
        var queueTimeoutMs = Math.Max(1, (int)(_queueTimeoutMs * share));

        if (_sheddingEnabled && remainingDeadlineMs is { } deadlineMs)
        {
            // Leave room for the work itself: any wait beyond this means the
            // response reaches the caller after its deadline - useless work.
            var usableWaitMs = deadlineMs - CurrentServiceMs();

            if (usableWaitMs <= 0)
            {
                Interlocked.Increment(ref _rejectedPredictedWait);
                return AdmissionResult.RejectedPredictedWait;
            }

            queueTimeoutMs = Math.Max(1, Math.Min(queueTimeoutMs, (int)usableWaitMs));
        }

        var waiting = Interlocked.Increment(ref _waiting);

        try
        {
            if (_sheddingEnabled && waiting > maxQueueLength)
            {
                Interlocked.Increment(ref _rejectedQueueFull);
                return AdmissionResult.RejectedQueueFull;
            }

            // Fail fast: if the requests already queued ahead of this one
            // will not drain before the queue timeout, waiting is pointless -
            // the caller would get the same 503, just 250 ms later and after
            // holding a queue spot that could have gone to someone else.
            if (_sheddingEnabled && EstimatedQueueWaitMs() > queueTimeoutMs)
            {
                Interlocked.Increment(ref _rejectedPredictedWait);
                return AdmissionResult.RejectedPredictedWait;
            }

            if (!_sheddingEnabled)
            {
                await _slots.WaitAsync(cancellationToken);
            }
            else if (!await _slots.WaitAsync(queueTimeoutMs, cancellationToken))
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
        return Volatile.Read(ref _waiting) * CurrentServiceMs() / _capacity;
    }

    private double CurrentServiceMs()
    {
        lock (_serviceLock)
        {
            return _ewmaServiceMs;
        }
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
            RejectedQueueTimeout: Interlocked.Read(ref _rejectedQueueTimeout),
            ByPriority: Enum.GetValues<RequestPriority>()
                .Select(priority => new PriorityAdmissionSnapshot(
                    priority,
                    QueueShares[(int)priority],
                    Interlocked.Read(ref _admittedByPriority[(int)priority]),
                    Interlocked.Read(ref _rejectedByPriority[(int)priority])))
                .ToArray());
    }

    private void OnAdmitted()
    {
        Interlocked.Increment(ref _active);
        Interlocked.Increment(ref _admitted);
    }
}
