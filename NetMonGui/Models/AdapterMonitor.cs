namespace NetMonGui.Models;

public readonly record struct Sample(DateTime Time, bool Ok, double? Rtt);

public readonly record struct WindowStats(double? Avg, double? Jitter, double Loss, long Attempts);

/// <summary>
/// Per-adapter rolling ping history and aggregate stats. Direct port of the
/// $Monitors entries from netmon.ps1.
/// </summary>
public sealed class AdapterMonitor
{
    public required string Name { get; set; }
    public required string Description { get; set; }
    public required string SourceIp { get; set; }
    public required int InterfaceIndex { get; set; }

    public List<Sample> Samples { get; } = new();

    public long TotalAttempts { get; private set; }
    public long TotalSuccess { get; private set; }
    private double _sumRtt;
    private double _sumAbsDiff;
    private long _diffCount;
    private double? _prevRtt;

    public void RecordSample(bool ok, double? rtt, DateTime time)
    {
        Samples.Add(new Sample(time, ok, rtt));
        TotalAttempts++;
        if (ok && rtt.HasValue)
        {
            TotalSuccess++;
            _sumRtt += rtt.Value;
            if (_prevRtt.HasValue)
            {
                _sumAbsDiff += Math.Abs(rtt.Value - _prevRtt.Value);
                _diffCount++;
            }
            _prevRtt = rtt.Value;
        }

        var cutoff = time.AddHours(-1);
        int i = 0;
        while (i < Samples.Count && Samples[i].Time < cutoff) i++;
        if (i > 0) Samples.RemoveRange(0, i);
    }

    public WindowStats GetWindowStats(int seconds, DateTime now)
    {
        var cutoff = now.AddSeconds(-seconds);
        int attempts = 0;
        var oks = new List<double>();
        foreach (var s in Samples)
        {
            if (s.Time < cutoff) continue;
            attempts++;
            if (s.Ok && s.Rtt.HasValue) oks.Add(s.Rtt.Value);
        }

        double? avg = null;
        double? jitter = null;
        if (oks.Count > 0)
        {
            avg = oks.Average();
            double jSum = 0;
            int jCount = 0;
            for (int i = 1; i < oks.Count; i++)
            {
                jSum += Math.Abs(oks[i] - oks[i - 1]);
                jCount++;
            }
            if (jCount > 0) jitter = jSum / jCount;
        }

        double loss = attempts > 0 ? 100.0 * (1 - (double)oks.Count / attempts) : 0.0;
        return new WindowStats(avg, jitter, loss, attempts);
    }

    public WindowStats GetSessionStats()
    {
        double? avg = TotalSuccess > 0 ? _sumRtt / TotalSuccess : null;
        double? jitter = _diffCount > 0 ? _sumAbsDiff / _diffCount : null;
        double loss = TotalAttempts > 0 ? 100.0 * (1 - (double)TotalSuccess / TotalAttempts) : 0.0;
        return new WindowStats(avg, jitter, loss, TotalAttempts);
    }
}
