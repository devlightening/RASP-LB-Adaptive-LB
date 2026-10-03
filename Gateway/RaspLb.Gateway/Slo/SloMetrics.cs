namespace RaspLb.Gateway.Slo;

public readonly record struct SloMetricsSnapshot(
    long TotalRequests,
    long Successes,
    long DeadlineExceeded,
    long Goodput)
{
    // Goodput = SLO içinde (deadline aşılmadan) başarıyla tamamlanan
    // mantıksal istek sayısı. Ham throughput'tan farkı budur:
    // throughput her tamamlanan isteği sayar, goodput yalnızca
    // "kullanıcıya zamanında ve doğru yanıt ulaştı" olanı sayar.
    public double GoodputRate =>
        TotalRequests == 0
            ? 0
            : (double)Goodput / TotalRequests;
}

public sealed class SloMetrics
{
    private long _totalRequests;
    private long _successes;
    private long _deadlineExceeded;
    private long _goodput;

    public void Record(
        bool success,
        bool withinDeadline)
    {
        Interlocked.Increment(ref _totalRequests);

        if (success)
        {
            Interlocked.Increment(ref _successes);
        }

        if (!withinDeadline)
        {
            Interlocked.Increment(ref _deadlineExceeded);
        }

        if (success && withinDeadline)
        {
            Interlocked.Increment(ref _goodput);
        }
    }

    public SloMetricsSnapshot GetSnapshot()
    {
        return new SloMetricsSnapshot(
            TotalRequests: Interlocked.Read(ref _totalRequests),
            Successes: Interlocked.Read(ref _successes),
            DeadlineExceeded: Interlocked.Read(ref _deadlineExceeded),
            Goodput: Interlocked.Read(ref _goodput));
    }
}
