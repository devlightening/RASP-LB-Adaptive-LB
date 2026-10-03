namespace RaspLb.Gateway.Resilience;

public sealed class RaspRetryOptions
{
    public const string SectionName = "RaspRetry";

    public bool Enabled { get; set; } = true;

    public int MaxAttempts { get; set; } = 2;

    public int TimeBudgetMs { get; set; } = 5_000;

    public int TokensPerSecond { get; set; } = 20;

    public int BurstCapacity { get; set; } = 20;

    // Retry a backend's shed 503 (queue full) on another backend.
    // Sheddable-priority requests are never retried this way.
    public bool RetryOnShed { get; set; } = true;
}
