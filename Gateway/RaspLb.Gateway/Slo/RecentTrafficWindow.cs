namespace RaspLb.Gateway.Slo;

public enum RequestOutcomeKind
{
    OnTime,
    Late,
    Shed,
    Error
}

public readonly record struct TrafficSecond(
    long UnixSecond,
    int OnTime,
    int Late,
    int Shed,
    int Errors,
    double? P50Ms,
    double? P95Ms);

// Son N saniyenin saniye saniye trafiği. SloMetrics sadece başlangıçtan
// beri birikmiş sayaçları tutuyor; aşırı yükte sistemin "şu an" nasıl
// davrandığını görmek için zaman serisi lazım. Gecikme yüzdelikleri
// yalnızca 2xx yanıtlardan hesaplanır - erken reddedilen (shed) 503'ler
// çok hızlı döndüğü için p95'i yapay olarak iyi gösterirdi.
public sealed class RecentTrafficWindow
{
    public const int WindowSeconds = 120;

    private readonly object _lock = new();
    private readonly Bucket[] _buckets = new Bucket[WindowSeconds];

    public RecentTrafficWindow()
    {
        for (var i = 0; i < _buckets.Length; i++)
        {
            _buckets[i] = new Bucket();
        }
    }

    public void Record(
        RequestOutcomeKind outcome,
        double elapsedMs)
    {
        var second = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        lock (_lock)
        {
            var bucket = _buckets[second % WindowSeconds];

            if (bucket.UnixSecond != second)
            {
                bucket.Reset(second);
            }

            switch (outcome)
            {
                case RequestOutcomeKind.OnTime:
                    bucket.OnTime++;
                    bucket.SuccessLatencies.Add(elapsedMs);
                    break;
                case RequestOutcomeKind.Late:
                    bucket.Late++;
                    bucket.SuccessLatencies.Add(elapsedMs);
                    break;
                case RequestOutcomeKind.Shed:
                    bucket.Shed++;
                    break;
                default:
                    bucket.Errors++;
                    break;
            }
        }
    }

    // Tamamlanmış saniyeleri döndürür (içinde bulunulan, yarım saniye hariç).
    public IReadOnlyList<TrafficSecond> GetSeries()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var series = new List<TrafficSecond>(WindowSeconds - 1);

        lock (_lock)
        {
            for (var second = now - WindowSeconds + 1; second < now; second++)
            {
                var bucket = _buckets[second % WindowSeconds];

                if (bucket.UnixSecond != second)
                {
                    series.Add(new TrafficSecond(second, 0, 0, 0, 0, null, null));
                    continue;
                }

                var sorted = bucket.SuccessLatencies.ToArray();
                Array.Sort(sorted);

                series.Add(new TrafficSecond(
                    second,
                    bucket.OnTime,
                    bucket.Late,
                    bucket.Shed,
                    bucket.Errors,
                    Percentile(sorted, 0.50),
                    Percentile(sorted, 0.95)));
            }
        }

        return series;
    }

    private static double? Percentile(
        double[] sorted,
        double percentile)
    {
        if (sorted.Length == 0)
        {
            return null;
        }

        var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;

        return Math.Round(sorted[Math.Clamp(index, 0, sorted.Length - 1)], 1);
    }

    private sealed class Bucket
    {
        public long UnixSecond = -1;
        public int OnTime;
        public int Late;
        public int Shed;
        public int Errors;
        public readonly List<double> SuccessLatencies = new();

        public void Reset(long second)
        {
            UnixSecond = second;
            OnTime = 0;
            Late = 0;
            Shed = 0;
            Errors = 0;
            SuccessLatencies.Clear();
        }
    }
}
