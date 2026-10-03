namespace RaspLb.Gateway.Slo;

public sealed class SloOptions
{
    public const string SectionName = "Slo";

    public bool Enabled { get; set; } = true;

    public int DeadlineMs { get; set; } = 500;
}
