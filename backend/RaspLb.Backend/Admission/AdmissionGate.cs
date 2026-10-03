using System.Text.Json.Serialization;

namespace RaspLb.Backend.Admission;

public enum AdmissionResult
{
    Admitted,
    RejectedQueueFull,
    RejectedPredictedWait,
    RejectedQueueTimeout
}

// Sıra önemli: düşük değer = yüksek öncelik (kuyruk indeksi olarak da kullanılır).
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
    int Waiting,
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

// Backend'in kapasite kapısı: kuyrukta kaç isteğin beklediğini bilen,
// kuyruğu sınırlayabilen (load shedding) ve bekleme süresini tahmin eden
// tek bir yer. Brownout da aşırı yük sinyalini buradan okur.
//
// Kuyruk önceliklidir: her öncelik için ayrı bir FIFO sıra var ve boşalan
// slot önce critical, sonra normal, en son sheddable bekleyene verilir.
// Böylece critical bir istek, kendisinden önce gelmiş normal isteklerin
// arkasında beklemez.
public sealed class AdmissionGate
{
    private const double ServiceTimeAlpha = 0.2;
    private const int PriorityCount = 3;

    // Kuyruk bütçesinin (uzunluk ve bekleme süresi) her önceliğe düşen payı.
    // Kuyruk dolmaya başlayınca önce sheddable, sonra normal istekler kapıda
    // reddedilir; kuyruğun son kısmı yalnızca critical isteklere kalır.
    private static readonly double[] QueueShares = [1.0, 0.6, 0.3];

    private readonly int _capacity;
    private readonly bool _sheddingEnabled;
    private readonly int _maxQueueLength;
    private readonly int _queueTimeoutMs;

    // _queueLock korur: _freeSlots, _queues, _waiting.
    private readonly object _queueLock = new();
    private readonly LinkedList<Waiter>[] _queues =
        Enumerable.Range(0, PriorityCount).Select(_ => new LinkedList<Waiter>()).ToArray();
    private int _freeSlots;
    private int _waiting;

    private int _active;
    private long _admitted;
    private long _rejectedQueueFull;
    private long _rejectedPredictedWait;
    private long _rejectedQueueTimeout;
    private readonly long[] _admittedByPriority = new long[PriorityCount];
    private readonly long[] _rejectedByPriority = new long[PriorityCount];

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
        _freeSlots = _capacity;
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
            Interlocked.Increment(ref _active);
            Interlocked.Increment(ref _admitted);
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
        var index = (int)priority;
        var share = QueueShares[index];
        var maxQueueLength = (int)Math.Ceiling(_maxQueueLength * share);
        var queueTimeoutMs = Math.Max(1, (int)(_queueTimeoutMs * share));
        var serviceMs = CurrentServiceMs();

        Waiter waiter;

        lock (_queueLock)
        {
            // Fast path: boş slot varsa kuyruğa hiç girmeden geç. Boş slot
            // ancak bekleyen yokken olabilir - Exit slotu doğrudan bekleyene verir.
            if (_freeSlots > 0)
            {
                _freeSlots--;
                return AdmissionResult.Admitted;
            }

            if (_sheddingEnabled)
            {
                if (remainingDeadlineMs is { } deadlineMs)
                {
                    // Leave room for the work itself: any wait beyond this
                    // means the response reaches the caller after its
                    // deadline - useless work.
                    var usableWaitMs = deadlineMs - serviceMs;

                    if (usableWaitMs <= 0)
                    {
                        _rejectedPredictedWait++;
                        return AdmissionResult.RejectedPredictedWait;
                    }

                    queueTimeoutMs = Math.Max(1, Math.Min(queueTimeoutMs, (int)usableWaitMs));
                }

                if (_waiting + 1 > maxQueueLength)
                {
                    _rejectedQueueFull++;
                    return AdmissionResult.RejectedQueueFull;
                }

                // Fail fast: if the requests that will be served before this
                // one cannot drain before the queue timeout, waiting is
                // pointless - the caller would get the same 503, just later.
                // With a priority queue only same-or-higher priority waiters
                // are ahead; lower-priority ones will be served after it.
                var predictedWaitMs =
                    (WaitingAheadOf(index) + 1) * serviceMs / _capacity;

                if (predictedWaitMs > queueTimeoutMs)
                {
                    _rejectedPredictedWait++;
                    return AdmissionResult.RejectedPredictedWait;
                }
            }

            waiter = new Waiter();
            waiter.Node = _queues[index].AddLast(waiter);
            _waiting++;
        }

        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (_sheddingEnabled)
        {
            timeout.CancelAfter(queueTimeoutMs);
        }

        // Timeout or client cancel: leave the queue unless Exit has already
        // handed this waiter a slot (then the slot is ours and we proceed).
        await using (timeout.Token.Register(() => Abandon(waiter)))
        {
            if (await waiter.Granted.Task)
            {
                return AdmissionResult.Admitted;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        Interlocked.Increment(ref _rejectedQueueTimeout);
        return AdmissionResult.RejectedQueueTimeout;
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

        Waiter? next = null;

        lock (_queueLock)
        {
            foreach (var queue in _queues)
            {
                if (queue.First is { } first)
                {
                    queue.RemoveFirst();
                    first.Value.Node = null;
                    _waiting--;
                    next = first.Value;
                    break;
                }
            }

            if (next is null)
            {
                _freeSlots++;
            }
        }

        // Outside the lock; continuations run asynchronously anyway.
        next?.Granted.TrySetResult(true);
    }

    private void Abandon(
        Waiter waiter)
    {
        lock (_queueLock)
        {
            if (waiter.Node is null)
            {
                return; // already granted a slot
            }

            waiter.Node.List!.Remove(waiter.Node);
            waiter.Node = null;
            _waiting--;
        }

        waiter.Granted.TrySetResult(false);
    }

    // Caller holds _queueLock.
    private int WaitingAheadOf(
        int priorityIndex)
    {
        var count = 0;

        for (var i = 0; i <= priorityIndex; i++)
        {
            count += _queues[i].Count;
        }

        return count;
    }

    // Little's law yaklaşımı: önümdeki N istek, kapasite kadar paralel
    // işleniyor ve her biri ortalama S ms sürüyor => ~N * S / C ms beklerim.
    // Anlık hesaplandığı için trafik kesilince kendiliğinden 0'a düşer.
    // Brownout sinyali: önceliğe bakmadan toplam kuyruk.
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
        int[] waitingByPriority;
        long rejectedQueueFull, rejectedPredictedWait;

        lock (_queueLock)
        {
            waitingByPriority = _queues.Select(q => q.Count).ToArray();
            rejectedQueueFull = _rejectedQueueFull;
            rejectedPredictedWait = _rejectedPredictedWait;
        }

        return new AdmissionSnapshot(
            Capacity: _capacity,
            Active: Volatile.Read(ref _active),
            Waiting: Volatile.Read(ref _waiting),
            EwmaServiceMs: Math.Round(CurrentServiceMs(), 1),
            EstimatedQueueWaitMs: Math.Round(EstimatedQueueWaitMs(), 1),
            SheddingEnabled: _sheddingEnabled,
            MaxQueueLength: _maxQueueLength,
            QueueTimeoutMs: _queueTimeoutMs,
            Admitted: Interlocked.Read(ref _admitted),
            RejectedQueueFull: rejectedQueueFull,
            RejectedPredictedWait: rejectedPredictedWait,
            RejectedQueueTimeout: Interlocked.Read(ref _rejectedQueueTimeout),
            ByPriority: Enum.GetValues<RequestPriority>()
                .Select(priority => new PriorityAdmissionSnapshot(
                    priority,
                    QueueShares[(int)priority],
                    waitingByPriority[(int)priority],
                    Interlocked.Read(ref _admittedByPriority[(int)priority]),
                    Interlocked.Read(ref _rejectedByPriority[(int)priority])))
                .ToArray());
    }

    private sealed class Waiter
    {
        public readonly TaskCompletionSource<bool> Granted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Non-null while queued; cleared under _queueLock when the waiter is
        // granted a slot or abandons the queue - whichever happens first wins.
        public LinkedListNode<Waiter>? Node;
    }
}
