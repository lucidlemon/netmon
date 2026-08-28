namespace NetMonGui.ViewModels;

public sealed class ProcessBandwidthRowViewModel
{
    public required int Pid { get; init; }
    public required string ProcessName { get; init; }
    public required double DownKBs { get; init; }
    public required double UpKBs { get; init; }
    public required long TotalSentBytes { get; init; }
    public required long TotalRecvBytes { get; init; }

    public double RateTotalKBs => DownKBs + UpKBs;
    public long SessionTotalBytes => TotalSentBytes + TotalRecvBytes;

    public string DownDisplay => FormatRate(DownKBs);
    public string UpDisplay => FormatRate(UpKBs);
    public string RateTotalDisplay => FormatRate(RateTotalKBs);
    public string SessionDataDisplay => FormatBytes(SessionTotalBytes);

    private static string FormatRate(double kbs) =>
        kbs >= 1024 ? $"{kbs / 1024:F2} MB/s" : $"{kbs:F1} KB/s";

    private static string FormatBytes(long bytes)
    {
        const double kb = 1024, mb = kb * 1024, gb = mb * 1024;
        return bytes switch
        {
            >= (long)gb => $"{bytes / gb:F2} GB",
            >= (long)mb => $"{bytes / mb:F1} MB",
            >= (long)kb => $"{bytes / kb:F0} KB",
            _ => $"{bytes} B",
        };
    }
}
