using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NetMonGui.Models;
using NetMonGui.Services;
using NetMonGui.ViewModels;

namespace NetMonGui;

public partial class MainWindow : Window
{
    private const int IntervalMs = 1000;
    private const int TimeoutMs = 1000;
    private const int RediscoverEverySec = 5;
    private const int ChartWindowSeconds = 120;
    private const string ElevatedRelaunchArg = "--per-process";
    private const double ProcessPanelHeight = 340;
    private const int MaxProcessRows = 10;

    private static readonly Brush[] Palette =
    {
        new SolidColorBrush(Color.FromRgb(0x4F, 0xA8, 0xE8)), // blue
        new SolidColorBrush(Color.FromRgb(0xF2, 0xA5, 0x3E)), // orange
        new SolidColorBrush(Color.FromRgb(0x5F, 0xD1, 0x7A)), // green
        new SolidColorBrush(Color.FromRgb(0xE0, 0x6E, 0xD8)), // magenta
        new SolidColorBrush(Color.FromRgb(0x4F, 0xE8, 0xD8)), // cyan
        new SolidColorBrush(Color.FromRgb(0xE8, 0xD3, 0x4F)), // gold
        new SolidColorBrush(Color.FromRgb(0xE8, 0x6E, 0x6E)), // tomato
    };

    private static readonly Brush LossBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x5A, 0x5A));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x76));
    private static readonly Brush GreenBrush = new SolidColorBrush(Color.FromRgb(0x5F, 0xD1, 0x7A));
    private static readonly Brush YellowBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xD3, 0x4F));
    private static readonly Brush RedBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x5A, 0x5A));
    private static readonly Brush GridBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x3A));
    private static readonly Brush AxisTextBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x92));

    private readonly Dictionary<string, AdapterMonitor> _monitors = new();
    private readonly Dictionary<string, AdapterRowViewModel> _rows = new();
    private readonly Dictionary<string, Brush> _colorByName = new();
    private readonly ObservableCollection<AdapterRowViewModel> _rowsView = new();

    private readonly DispatcherTimer _timer;
    private DateTime _startTime;
    private DateTime _lastDiscovery;
    private bool _busy;
    private string _target = "1.1.1.1";
    private int _nextColorIndex;

    private readonly ProcessBandwidthService _bandwidthService = new();
    private readonly ObservableCollection<ProcessBandwidthRowViewModel> _processRows = new();
    private readonly Dictionary<int, TrackedProcess> _knownProcesses = new();
    private bool _perProcessEnabled;
    private ProcessSortMode _sortMode = ProcessSortMode.Current;
    private DateTime _lastBandwidthDrain;

    private const int Avg30WindowSeconds = 30;

    private enum ProcessSortMode { Current, Avg30, Total }

    public MainWindow()
    {
        InitializeComponent();
        AdapterCards.ItemsSource = _rowsView;
        ProcessGrid.ItemsSource = _processRows;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(IntervalMs) };
        _timer.Tick += Timer_Tick;

        Loaded += MainWindow_Loaded;
        Closed += (_, _) =>
        {
            _timer.Stop();
            _bandwidthService.Dispose();
        };
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _startTime = DateTime.Now;
        _lastDiscovery = DateTime.MinValue;
        SyncMonitors();
        _lastDiscovery = DateTime.Now;
        _timer.Start();

        if (Environment.GetCommandLineArgs().Contains(ElevatedRelaunchArg))
        {
            PerProcessCheckBox.IsChecked = true;
        }

        _ = UpdateService.CheckForUpdatesAsync(msg => Dispatcher.Invoke(() =>
        {
            UpdateStatusText.Text = msg;
            UpdateStatusText.Visibility = Visibility.Visible;
        }));
    }

    private void ApplyTargetButton_Click(object sender, RoutedEventArgs e)
    {
        var text = TargetBox.Text.Trim();
        if (text.Length == 0) return;
        _target = text;
        StatusText.Text = $"Target changed to {_target}.";
    }

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var tickStart = DateTime.Now;

            if ((tickStart - _lastDiscovery).TotalSeconds >= RediscoverEverySec)
            {
                SyncMonitors();
                _lastDiscovery = tickStart;
            }

            SessionTimeText.Text = (tickStart - _startTime).ToString(@"hh\:mm\:ss");

            if (_monitors.Count == 0)
            {
                StatusText.Text = "No active, non-virtual network adapters found. Connect Ethernet / hotspot / Wi-Fi.";
                return;
            }

            var pingTasks = _monitors
                .Select(kv => (Name: kv.Key, SourceIp: kv.Value.SourceIp, Task: PingService.PingOnceAsync(kv.Value.SourceIp, _target, TimeoutMs)))
                .ToList();

            await Task.WhenAll(pingTasks.Select(t => t.Task));

            var sampleTime = DateTime.Now;
            foreach (var t in pingTasks)
            {
                if (!_monitors.TryGetValue(t.Name, out var mon)) continue;
                var (ok, rtt) = t.Task.Result;
                mon.RecordSample(ok, rtt, sampleTime);
            }

            StatusText.Text = $"Monitoring {_monitors.Count} adapter(s) against {_target}.";
            UpdateRows(sampleTime);
            RedrawCharts(sampleTime);

            if (_perProcessEnabled)
            {
                UpdateProcessBandwidth(sampleTime);
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private void SyncMonitors()
    {
        List<AdapterInfo> active;
        try
        {
            active = NetworkInfoService.GetActiveInterfaces();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Adapter discovery failed: {ex.Message}";
            return;
        }

        var activeNames = active.Select(a => a.Name).ToHashSet();

        foreach (var name in _monitors.Keys.ToList())
        {
            if (activeNames.Contains(name)) continue;
            _monitors.Remove(name);
            if (_rows.Remove(name, out var row)) _rowsView.Remove(row);
        }

        foreach (var a in active)
        {
            if (_monitors.TryGetValue(a.Name, out var mon))
            {
                mon.SourceIp = a.SourceIp;
                mon.InterfaceIndex = a.InterfaceIndex;
                continue;
            }

            var newMon = new AdapterMonitor
            {
                Name = a.Name,
                Description = a.Description,
                SourceIp = a.SourceIp,
                InterfaceIndex = a.InterfaceIndex,
            };
            _monitors[a.Name] = newMon;

            var newRow = new AdapterRowViewModel
            {
                Name = a.Name,
                Description = a.Description,
                InterfaceIndex = a.InterfaceIndex,
                Color = GetColorFor(a.Name),
            };
            _rows[a.Name] = newRow;
            _rowsView.Add(newRow);
        }
    }

    private Brush GetColorFor(string name)
    {
        if (_colorByName.TryGetValue(name, out var b)) return b;
        var color = Palette[_nextColorIndex % Palette.Length];
        _nextColorIndex++;
        _colorByName[name] = color;
        return color;
    }

    private void UpdateRows(DateTime now)
    {
        string? best = null;
        double bestScore = double.MaxValue;
        foreach (var kv in _monitors)
        {
            var s = kv.Value.GetSessionStats();
            if (!s.Avg.HasValue) continue;
            double jitter = s.Jitter ?? 0;
            double score = s.Avg.Value + 2 * jitter + 10 * s.Loss;
            if (score < bestScore)
            {
                bestScore = score;
                best = kv.Key;
            }
        }

        var ifIndexToName = _monitors.Values.ToDictionary(m => m.InterfaceIndex, m => m.Name);
        string? osPreferred = null;
        try { osPreferred = NetworkInfoService.GetOsPreferredInterfaceName(ifIndexToName); }
        catch { /* best effort */ }

        foreach (var kv in _monitors)
        {
            if (!_rows.TryGetValue(kv.Key, out var row)) continue;
            var mon = kv.Value;

            var nowStat = mon.GetWindowStats(3, now);
            var s30 = mon.GetWindowStats(30, now);
            var h1 = mon.GetWindowStats(3600, now);
            var sess = mon.GetSessionStats();

            row.NowMs = AdapterRowViewModel.FormatMs(nowStat.Avg);
            row.NowBrush = ColorForLatency(nowStat.Avg);

            row.Win30 = AdapterRowViewModel.FormatWindow(s30);
            row.Win30Brush = ColorForLatency(s30.Avg);
            row.Loss30 = AdapterRowViewModel.FormatLoss(s30.Loss);
            row.Loss30Brush = s30.Loss > 0 ? LossBrush : MutedBrush;

            row.Win1h = AdapterRowViewModel.FormatWindow(h1);
            row.Win1hBrush = ColorForLatency(h1.Avg);
            row.Loss1h = AdapterRowViewModel.FormatLoss(h1.Loss);
            row.Loss1hBrush = h1.Loss > 0 ? LossBrush : MutedBrush;

            row.Session = AdapterRowViewModel.FormatWindow(sess);
            row.SessionBrush = ColorForLatency(sess.Avg);
            row.LossSession = AdapterRowViewModel.FormatLoss(sess.Loss);
            row.LossSessionBrush = sess.Loss > 0 ? LossBrush : MutedBrush;

            row.IsBest = kv.Key == best;
            row.IsOsPreferred = kv.Key == osPreferred;
        }
    }

    private static Brush ColorForLatency(double? v)
    {
        if (!v.HasValue) return MutedBrush;
        if (v.Value < 40) return GreenBrush;
        if (v.Value < 80) return YellowBrush;
        return RedBrush;
    }

    private async void PreferButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not AdapterRowViewModel vm) return;

        var others = _monitors.Values
            .Where(m => m.InterfaceIndex != vm.InterfaceIndex)
            .Select(m => m.InterfaceIndex)
            .ToList();

        var originalContent = btn.Content;
        btn.IsEnabled = false;
        btn.Content = "Confirm UAC prompt…";

        var (success, error) = await Task.Run(() => NetworkInfoService.SetPreferredAdapter(vm.InterfaceIndex, others));

        btn.Content = originalContent;
        btn.IsEnabled = true;

        if (success)
        {
            StatusText.Text = $"{vm.Name}: interface metric lowered so Windows prefers it. This can take a few seconds to take effect.";
        }
        else
        {
            StatusText.Text = $"Could not change adapter priority: {error}";
            MessageBox.Show(this, error ?? "Unknown error", "NetMon", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PerProcessCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsAdministrator())
        {
            var result = MessageBox.Show(this,
                "Showing per-process bandwidth needs administrator rights to see which process owns each " +
                "packet. NetMon will restart as administrator (your current session stats will reset). Continue?",
                "NetMon", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes && TryRelaunchElevated())
            {
                return; // this instance is shutting down
            }

            PerProcessCheckBox.IsChecked = false;
            return;
        }

        var (success, error) = _bandwidthService.Start();
        if (success)
        {
            _perProcessEnabled = true;
            _lastBandwidthDrain = DateTime.Now;
            bool wasCollapsed = ProcessPanel.Visibility != Visibility.Visible;
            ProcessPanel.Visibility = Visibility.Visible;
            if (wasCollapsed) GrowForProcessPanel();
            StatusText.Text = "Per-process bandwidth tracking started.";
        }
        else
        {
            StatusText.Text = $"Could not start per-process bandwidth tracking: {error}";
            MessageBox.Show(this, error ?? "Unknown error", "NetMon", MessageBoxButton.OK, MessageBoxImage.Warning);
            PerProcessCheckBox.IsChecked = false;
        }
    }

    private void PerProcessCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        _perProcessEnabled = false;
        _bandwidthService.Stop();
        _processRows.Clear();
        _knownProcesses.Clear();

        bool wasVisible = ProcessPanel.Visibility == Visibility.Visible;
        ProcessPanel.Visibility = Visibility.Collapsed;
        if (wasVisible) ShrinkForProcessPanel();
    }

    private void ProcessSortMode_Checked(object sender, RoutedEventArgs e)
    {
        _sortMode = sender == SortByTotalRadio ? ProcessSortMode.Total
            : sender == SortByAvg30Radio ? ProcessSortMode.Avg30
            : ProcessSortMode.Current;
        RenderProcessRows();
    }

    private void GrowForProcessPanel()
    {
        if (WindowState != WindowState.Normal) return;
        Height = Math.Min(Height + ProcessPanelHeight, SystemParameters.WorkArea.Height);
    }

    private void ShrinkForProcessPanel()
    {
        if (WindowState != WindowState.Normal) return;
        Height = Math.Max(Height - ProcessPanelHeight, MinHeight);
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private bool TryRelaunchElevated()
    {
        try
        {
            string? exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (exePath == null)
            {
                StatusText.Text = "Could not determine the app's own path to restart elevated.";
                return false;
            }

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = ElevatedRelaunchArg,
                UseShellExecute = true,
                Verb = "runas",
            };
            Process.Start(psi);
            Application.Current.Shutdown();
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            StatusText.Text = "Elevation was cancelled.";
            return false;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not restart as administrator: {ex.Message}";
            return false;
        }
    }

    private void UpdateProcessBandwidth(DateTime now)
    {
        double elapsedSec = Math.Max(0.001, (now - _lastBandwidthDrain).TotalSeconds);
        _lastBandwidthDrain = now;

        var thisTickBytes = new Dictionary<int, long>();
        foreach (var (pid, sent, recv) in _bandwidthService.DrainSnapshot())
        {
            var tracked = GetOrCreateTracked(pid);
            tracked.DownKBs = recv / 1024.0 / elapsedSec;
            tracked.UpKBs = sent / 1024.0 / elapsedSec;
            thisTickBytes[pid] = sent + recv;
        }

        foreach (var (pid, sent, recv) in _bandwidthService.GetLifetimeTotals())
        {
            var tracked = GetOrCreateTracked(pid);
            tracked.TotalSentBytes = sent;
            tracked.TotalRecvBytes = recv;
        }

        // Every known process gets a sample every tick (0 if it was quiet), including ones with
        // no bytes this tick - that's what makes the 30s average reflect a spike that has since
        // gone quiet, rather than only ever showing the instantaneous rate.
        var cutoff = now.AddSeconds(-Avg30WindowSeconds);
        foreach (var tracked in _knownProcesses.Values)
        {
            if (!thisTickBytes.ContainsKey(tracked.Pid))
            {
                tracked.DownKBs = 0;
                tracked.UpKBs = 0;
            }

            thisTickBytes.TryGetValue(tracked.Pid, out long bytesThisTick);
            tracked.RecentSamples.Add((now, bytesThisTick));
            while (tracked.RecentSamples.Count > 0 && tracked.RecentSamples[0].Time < cutoff)
            {
                tracked.RecentSamples.RemoveAt(0);
            }
        }

        RenderProcessRows();
    }

    private TrackedProcess GetOrCreateTracked(int pid)
    {
        if (_knownProcesses.TryGetValue(pid, out var existing)) return existing;
        var created = new TrackedProcess { Pid = pid, ProcessName = ResolveProcessName(pid) };
        _knownProcesses[pid] = created;
        return created;
    }

    private void RenderProcessRows()
    {
        var ranked = _knownProcesses.Values
            .OrderByDescending(t => _sortMode switch
            {
                ProcessSortMode.Total => t.TotalSentBytes + t.TotalRecvBytes,
                ProcessSortMode.Avg30 => (long)(t.Avg30KBs * 1024),
                _ => (long)((t.DownKBs + t.UpKBs) * 1024),
            })
            .Take(MaxProcessRows)
            .Select(t => new ProcessBandwidthRowViewModel
            {
                Pid = t.Pid,
                ProcessName = t.ProcessName,
                DownKBs = t.DownKBs,
                UpKBs = t.UpKBs,
                Avg30KBs = t.Avg30KBs,
                TotalSentBytes = t.TotalSentBytes,
                TotalRecvBytes = t.TotalRecvBytes,
            });

        _processRows.Clear();
        foreach (var row in ranked) _processRows.Add(row);
    }

    private sealed class TrackedProcess
    {
        public required int Pid { get; init; }
        public required string ProcessName { get; init; }
        public double DownKBs { get; set; }
        public double UpKBs { get; set; }
        public long TotalSentBytes { get; set; }
        public long TotalRecvBytes { get; set; }
        public List<(DateTime Time, long Bytes)> RecentSamples { get; } = new();

        public double Avg30KBs
        {
            get
            {
                if (RecentSamples.Count == 0) return 0;
                double windowSec = Math.Max(1.0, Math.Min(Avg30WindowSeconds,
                    (RecentSamples[^1].Time - RecentSamples[0].Time).TotalSeconds));
                long totalBytes = RecentSamples.Sum(s => s.Bytes);
                return totalBytes / 1024.0 / windowSec;
            }
        }
    }

    private static string ResolveProcessName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch
        {
            return pid == 4 ? "System" : $"PID {pid}";
        }
    }

    private void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawCharts(DateTime.Now);

    private void RedrawCharts(DateTime now)
    {
        var windowStart = now.AddSeconds(-ChartWindowSeconds);

        var latencySeries = new Dictionary<string, List<(double T, double? V)>>();
        var jitterSeries = new Dictionary<string, List<(double T, double? V)>>();
        double maxLatency = 10;
        double maxJitter = 10;

        foreach (var kv in _monitors)
        {
            var latPts = new List<(double, double?)>();
            var jitPts = new List<(double, double?)>();
            double? prevRtt = null;

            foreach (var s in kv.Value.Samples)
            {
                double? rtt = s.Ok ? s.Rtt : null;

                double? jitter = null;
                if (rtt.HasValue)
                {
                    if (prevRtt.HasValue) jitter = Math.Abs(rtt.Value - prevRtt.Value);
                    prevRtt = rtt.Value;
                }

                if (s.Time < windowStart) continue;
                double t = (s.Time - windowStart).TotalSeconds;

                if (rtt.HasValue && rtt.Value > maxLatency) maxLatency = rtt.Value;
                latPts.Add((t, rtt));

                if (jitter.HasValue && jitter.Value > maxJitter) maxJitter = jitter.Value;
                jitPts.Add((t, jitter));
            }

            latencySeries[kv.Key] = latPts;
            jitterSeries[kv.Key] = jitPts;
        }

        DrawChart(LatencyChartCanvas, LatencyLegendPanel, latencySeries, maxLatency * 1.25,
            mon => $"{AdapterRowViewModel.FormatMs(mon.GetSessionStats().Avg)} ms avg");
        DrawChart(JitterChartCanvas, JitterLegendPanel, jitterSeries, maxJitter * 1.25,
            mon => $"{AdapterRowViewModel.FormatMs(mon.GetSessionStats().Jitter)} ms jitter");
    }

    private void DrawChart(Canvas canvas, StackPanel legendPanel, Dictionary<string, List<(double T, double? V)>> series,
        double yMax, Func<AdapterMonitor, string> legendSubtitle)
    {
        canvas.Children.Clear();
        legendPanel.Children.Clear();

        double w = canvas.ActualWidth;
        double h = canvas.ActualHeight;
        if (w <= 4 || h <= 4) return;

        DrawGridlines(canvas, w, h, yMax);

        foreach (var kv in _monitors)
        {
            var color = GetColorFor(kv.Key);
            if (series.TryGetValue(kv.Key, out var pts) && pts.Count >= 2)
            {
                DrawSeries(canvas, pts, color, w, h, yMax);
            }

            AddLegendEntry(legendPanel, kv.Key, color, legendSubtitle(kv.Value));
        }
    }

    private static void DrawGridlines(Canvas canvas, double w, double h, double yMax)
    {
        const int lines = 4;
        for (int i = 0; i <= lines; i++)
        {
            double y = h * i / lines;
            var line = new Line { X1 = 0, X2 = w, Y1 = y, Y2 = y, Stroke = GridBrush, StrokeThickness = 1 };
            canvas.Children.Add(line);

            double value = yMax * (lines - i) / lines;
            var label = new TextBlock
            {
                Text = $"{value:F0}ms",
                Foreground = AxisTextBrush,
                FontSize = 10,
            };
            Canvas.SetLeft(label, 2);
            Canvas.SetTop(label, Math.Max(0, y - 12));
            canvas.Children.Add(label);
        }
    }

    private static void DrawSeries(Canvas canvas, List<(double T, double? V)> pts, Brush color, double w, double h, double yMax)
    {
        List<Point>? segment = null;

        void FlushSegment()
        {
            if (segment is { Count: >= 2 })
            {
                canvas.Children.Add(new Polyline
                {
                    Points = new PointCollection(segment),
                    Stroke = color,
                    StrokeThickness = 2,
                    StrokeLineJoin = PenLineJoin.Round,
                });
            }
            segment = null;
        }

        foreach (var (t, v) in pts)
        {
            if (!v.HasValue)
            {
                FlushSegment();
                continue;
            }

            double x = t / ChartWindowSeconds * w;
            double y = h - Math.Min(1.0, v.Value / yMax) * h;
            segment ??= new List<Point>();
            segment.Add(new Point(x, y));
        }
        FlushSegment();

        var last = pts.LastOrDefault(p => p.V.HasValue);
        if (last.V.HasValue)
        {
            double x = last.T / ChartWindowSeconds * w;
            double y = h - Math.Min(1.0, last.V.Value / yMax) * h;
            var dot = new Ellipse { Width = 6, Height = 6, Fill = color };
            Canvas.SetLeft(dot, x - 3);
            Canvas.SetTop(dot, y - 3);
            canvas.Children.Add(dot);
        }
    }

    private static void AddLegendEntry(StackPanel legendPanel, string name, Brush color, string subtitle)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        row.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = color, Margin = new Thickness(0, 2, 6, 0) });
        row.Children.Add(new StackPanel
        {
            Children =
            {
                new TextBlock { Text = name, FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = subtitle, FontSize = 10, Foreground = MutedBrush },
            }
        });
        legendPanel.Children.Add(row);
    }
}
