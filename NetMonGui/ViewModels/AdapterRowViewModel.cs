using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using NetMonGui.Models;

namespace NetMonGui.ViewModels;

public sealed class AdapterRowViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public required string Name { get; init; }
    public required string Description { get; init; }
    public required int InterfaceIndex { get; init; }
    public required Brush Color { get; init; }

    private string _nowMs = "--";
    public string NowMs { get => _nowMs; set { _nowMs = value; Raise(); } }
    private Brush _nowBrush = Brushes.Gray;
    public Brush NowBrush { get => _nowBrush; set { _nowBrush = value; Raise(); } }

    private string _nowJitterMs = "--";
    public string NowJitterMs { get => _nowJitterMs; set { _nowJitterMs = value; Raise(); } }

    // Loss over a 60s window specifically for the compact card - none of the existing windows
    // (3s "now", 30s, 1h, session) match what it shows for ping/jitter.
    private string _loss60 = "0%";
    public string Loss60 { get => _loss60; set { _loss60 = value; Raise(); } }
    private Brush _loss60Brush = Brushes.Gray;
    public Brush Loss60Brush { get => _loss60Brush; set { _loss60Brush = value; Raise(); } }

    // The mini sparkline canvas stretches to fill the compact card, so its actual rendered width
    // (kept in sync by MainWindow.MiniSparkCanvas_SizeChanged) is what point coordinates scale
    // against - not a fixed constant. Height stays fixed (MainWindow.MiniChartHeight).
    public double SparkWidth { get; set; } = 120;

    // Point sets for the compact card's mini sparkline, recomputed every tick from the adapter's
    // last MiniChartWindowSeconds of samples.
    private PointCollection _pingSpark = new();
    public PointCollection PingSpark { get => _pingSpark; set { _pingSpark = value; Raise(); } }
    private PointCollection _jitterSpark = new();
    public PointCollection JitterSpark { get => _jitterSpark; set { _jitterSpark = value; Raise(); } }

    private string _win30 = "--";
    public string Win30 { get => _win30; set { _win30 = value; Raise(); } }
    private Brush _win30Brush = Brushes.Gray;
    public Brush Win30Brush { get => _win30Brush; set { _win30Brush = value; Raise(); } }

    private string _loss30 = "0%";
    public string Loss30 { get => _loss30; set { _loss30 = value; Raise(); } }
    private Brush _loss30Brush = Brushes.Gray;
    public Brush Loss30Brush { get => _loss30Brush; set { _loss30Brush = value; Raise(); } }

    private string _win1h = "--";
    public string Win1h { get => _win1h; set { _win1h = value; Raise(); } }
    private Brush _win1hBrush = Brushes.Gray;
    public Brush Win1hBrush { get => _win1hBrush; set { _win1hBrush = value; Raise(); } }

    private string _loss1h = "0%";
    public string Loss1h { get => _loss1h; set { _loss1h = value; Raise(); } }
    private Brush _loss1hBrush = Brushes.Gray;
    public Brush Loss1hBrush { get => _loss1hBrush; set { _loss1hBrush = value; Raise(); } }

    private string _session = "--";
    public string Session { get => _session; set { _session = value; Raise(); } }
    private Brush _sessionBrush = Brushes.Gray;
    public Brush SessionBrush { get => _sessionBrush; set { _sessionBrush = value; Raise(); } }

    private string _lossSession = "0%";
    public string LossSession { get => _lossSession; set { _lossSession = value; Raise(); } }
    private Brush _lossSessionBrush = Brushes.Gray;
    public Brush LossSessionBrush { get => _lossSessionBrush; set { _lossSessionBrush = value; Raise(); } }

    private bool _isBest;
    public bool IsBest { get => _isBest; set { _isBest = value; Raise(); Raise(nameof(Marker)); } }

    private bool _isOsPreferred;
    public bool IsOsPreferred { get => _isOsPreferred; set { _isOsPreferred = value; Raise(); Raise(nameof(DisplayName)); } }

    public string Marker => IsBest ? "★" : "";
    public string DisplayName => IsOsPreferred ? $"{Name}  [OS]" : Name;

    public double? SessionAvgForSort { get; set; }

    public static string FormatWindow(WindowStats s) =>
        $"{FormatMs(s.Avg)} / {FormatMs(s.Jitter)} ms";

    public static string FormatMs(double? v) => v.HasValue ? v.Value.ToString("F1") : "--";

    public static string FormatLoss(double loss) => $"{loss:F0}%";
}
