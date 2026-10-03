namespace RaspLb.Gateway.Resilience;

public readonly record struct RetryMetricsSnapshot(
    long LogicalRequests,
    long RetryAttempts,
    long RetrySuccesses,
    long Exhausted,
    long BudgetRejected,
    long TimeBudgetRejected,
    long UnsafeMethodRejected,
    long ResponseStartedRejected,
    long NoAlternativeDestination,
    long ShedRetryAttempts,
    long ShedRetrySuccesses);

public sealed class RetryMetrics
{
    private long _logicalRequests;
    private long _retryAttempts;
    private long _retrySuccesses;
    private long _exhausted;
    private long _budgetRejected;
    private long _timeBudgetRejected;
    private long _unsafeMethodRejected;
    private long _responseStartedRejected;
    private long _noAlternativeDestination;
    private long _shedRetryAttempts;
    private long _shedRetrySuccesses;

    public void RecordLogicalRequest() =>
        Interlocked.Increment(ref _logicalRequests);

    public void RecordRetryAttempt() =>
        Interlocked.Increment(ref _retryAttempts);

    public void RecordRetrySuccess() =>
        Interlocked.Increment(ref _retrySuccesses);

    public void RecordExhausted() =>
        Interlocked.Increment(ref _exhausted);

    public void RecordBudgetRejected() =>
        Interlocked.Increment(ref _budgetRejected);

    public void RecordTimeBudgetRejected() =>
        Interlocked.Increment(ref _timeBudgetRejected);

    public void RecordUnsafeMethodRejected() =>
        Interlocked.Increment(ref _unsafeMethodRejected);

    public void RecordResponseStartedRejected() =>
        Interlocked.Increment(ref _responseStartedRejected);

    public void RecordNoAlternativeDestination() =>
        Interlocked.Increment(ref _noAlternativeDestination);

    public void RecordShedRetryAttempt() =>
        Interlocked.Increment(ref _shedRetryAttempts);

    public void RecordShedRetrySuccess() =>
        Interlocked.Increment(ref _shedRetrySuccesses);

    public RetryMetricsSnapshot GetSnapshot()
    {
        return new RetryMetricsSnapshot(
            LogicalRequests: Interlocked.Read(ref _logicalRequests),
            RetryAttempts: Interlocked.Read(ref _retryAttempts),
            RetrySuccesses: Interlocked.Read(ref _retrySuccesses),
            Exhausted: Interlocked.Read(ref _exhausted),
            BudgetRejected: Interlocked.Read(ref _budgetRejected),
            TimeBudgetRejected: Interlocked.Read(ref _timeBudgetRejected),
            UnsafeMethodRejected: Interlocked.Read(ref _unsafeMethodRejected),
            ResponseStartedRejected: Interlocked.Read(ref _responseStartedRejected),
            NoAlternativeDestination: Interlocked.Read(ref _noAlternativeDestination),
            ShedRetryAttempts: Interlocked.Read(ref _shedRetryAttempts),
            ShedRetrySuccesses: Interlocked.Read(ref _shedRetrySuccesses));
    }
}
