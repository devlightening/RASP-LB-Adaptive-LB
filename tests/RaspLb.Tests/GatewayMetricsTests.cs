using RaspLb.Gateway.LoadBalancing;
using RaspLb.Gateway.Slo;

namespace RaspLb.Tests;

public class DestinationMetricsTests
{
    [Fact]
    public void Shed_responses_raise_the_error_rate_but_not_the_latency_estimate()
    {
        var metrics = new DestinationMetrics();
        metrics.Record(100, DestinationRequestOutcome.Success);

        metrics.Record(1, DestinationRequestOutcome.Shed);

        var snapshot = metrics.GetSnapshot();
        Assert.Equal(100, snapshot.EwmaLatencyMs);
        Assert.True(snapshot.EwmaErrorRate > 0);
        Assert.Equal(1, snapshot.Sheds);
    }

    [Fact]
    public void First_latency_sample_is_taken_as_is_even_after_a_shed()
    {
        var metrics = new DestinationMetrics();
        metrics.Record(1, DestinationRequestOutcome.Shed);

        metrics.Record(80, DestinationRequestOutcome.Success);

        Assert.Equal(80, metrics.GetSnapshot().EwmaLatencyMs);
    }
}

public class RecentTrafficWindowTests
{
    [Theory]
    [InlineData("critical", 0)]
    [InlineData(" Sheddable ", 2)]
    [InlineData("normal", 1)]
    [InlineData(null, 1)]
    [InlineData("vip", 1)]
    public void Parses_priority_header_with_normal_as_default(
        string? header,
        int expected)
    {
        Assert.Equal(expected, RecentTrafficWindow.PriorityIndex(header));
    }

    [Fact]
    public void Completed_second_reports_outcomes_overall_and_per_priority()
    {
        var window = new RecentTrafficWindow();
        WaitForFreshSecond();

        window.Record(RequestOutcomeKind.OnTime, 100, priorityIndex: 0);
        window.Record(RequestOutcomeKind.Late, 600, priorityIndex: 1);
        window.Record(RequestOutcomeKind.Shed, 1, priorityIndex: 2);

        WaitForFreshSecond();
        var last = window.GetSeries()[^1];

        Assert.Equal((1, 1, 1, 0), (last.OnTime, last.Late, last.Shed, last.Errors));
        Assert.Equal(1, last.ByPriority[0].OnTime);
        Assert.Equal(1, last.ByPriority[1].Late);
        Assert.Equal(1, last.ByPriority[2].Shed);
        Assert.Equal(600, last.P95Ms); // shed responses are excluded from latency
    }

    // Recording right at a second boundary could split the samples across
    // two buckets; start just after a boundary instead.
    private static void WaitForFreshSecond()
    {
        var start = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        while (DateTimeOffset.UtcNow.ToUnixTimeSeconds() == start)
        {
            Thread.Sleep(10);
        }
    }
}
