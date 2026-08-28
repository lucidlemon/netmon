using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace NetMonGui.Services;

/// <summary>
/// Attributes network bytes to the owning process via a realtime ETW kernel trace
/// (the same "NT Kernel Logger" / NetworkTCPIP provider Resource Monitor and Task
/// Manager's Network column use). Only works elevated - starting the kernel session
/// requires administrator rights.
/// </summary>
public sealed class ProcessBandwidthService : IDisposable
{
    private const string KernelSessionName = "NT Kernel Logger";

    private TraceEventSession? _session;
    private readonly ConcurrentDictionary<int, (long Sent, long Recv)> _accum = new();
    private readonly ConcurrentDictionary<int, (long Sent, long Recv)> _lifetime = new();

    public bool IsRunning => _session != null;

    public (bool Success, string? Error) Start()
    {
        if (_session != null) return (true, null);

        try
        {
            EnsureNativeDependencyExtracted();

            var session = new TraceEventSession(KernelSessionName) { StopOnDispose = true };
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP, KernelTraceEventParser.Keywords.None);

            var kernel = session.Source.Kernel;
            kernel.TcpIpSend += e => Record(e.ProcessID, e.size, sent: true);
            kernel.TcpIpRecv += e => Record(e.ProcessID, e.size, sent: false);
            kernel.TcpIpSendIPV6 += e => Record(e.ProcessID, e.size, sent: true);
            kernel.TcpIpRecvIPV6 += e => Record(e.ProcessID, e.size, sent: false);
            kernel.UdpIpSend += e => Record(e.ProcessID, e.size, sent: true);
            kernel.UdpIpRecv += e => Record(e.ProcessID, e.size, sent: false);
            kernel.UdpIpSendIPV6 += e => Record(e.ProcessID, e.size, sent: true);
            kernel.UdpIpRecvIPV6 += e => Record(e.ProcessID, e.size, sent: false);

            _session = session;

            // Source.Process() blocks pumping ETW events until the session is stopped/disposed.
            Task.Run(() =>
            {
                try { session.Source.Process(); }
                catch { /* session was stopped/disposed */ }
            });

            return (true, null);
        }
        catch (Exception ex)
        {
            _session?.Dispose();
            _session = null;
            return (false, ex.Message);
        }
    }

    private void Record(int pid, int size, bool sent)
    {
        if (pid <= 0 || size <= 0) return;
        _accum.AddOrUpdate(pid,
            _ => sent ? (size, 0L) : (0L, (long)size),
            (_, old) => sent ? (old.Sent + size, old.Recv) : (old.Sent, old.Recv + size));
        _lifetime.AddOrUpdate(pid,
            _ => sent ? (size, 0L) : (0L, (long)size),
            (_, old) => sent ? (old.Sent + size, old.Recv) : (old.Sent, old.Recv + size));
    }

    /// <summary>Returns and clears the byte counts accumulated since the last call - used for the current rate.</summary>
    public List<(int Pid, long Sent, long Recv)> DrainSnapshot()
    {
        var result = new List<(int, long, long)>();
        foreach (var pid in _accum.Keys)
        {
            if (_accum.TryRemove(pid, out var v)) result.Add((pid, v.Sent, v.Recv));
        }
        return result;
    }

    /// <summary>Total bytes seen per process since Start() - never drained, only reset by Stop().</summary>
    public List<(int Pid, long Sent, long Recv)> GetLifetimeTotals() =>
        _lifetime.Select(kv => (kv.Key, kv.Value.Sent, kv.Value.Recv)).ToList();

    public void Stop()
    {
        try { _session?.Stop(); } catch { /* best effort */ }
        try { _session?.Dispose(); } catch { /* best effort */ }
        _session = null;
        _accum.Clear();
        _lifetime.Clear();
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Writes the embedded KernelTraceControl.dll next to the running exe, in an "amd64"
    /// subfolder. TraceEventSession's kernel-session loader (ETWKernelControl.LoadKernelTraceControl)
    /// does NOT use the normal DllImport search path - it builds this exact relative path itself
    /// (matching the layout the TraceEvent NuGet package's own build target produces in a
    /// non-single-file build) and calls LoadLibrary on it directly. A single-file publish never
    /// gets that loose file, so without this the kernel session fails with "the specified module
    /// could not be found" - regardless of SetDllDirectory or preloading the DLL from elsewhere,
    /// since neither changes what path that internal LoadLibrary call actually asks for.
    /// </summary>
    private static void EnsureNativeDependencyExtracted()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "amd64");
        Directory.CreateDirectory(dir);

        string dllPath = Path.Combine(dir, "KernelTraceControl.dll");
        if (!File.Exists(dllPath))
        {
            var asm = Assembly.GetExecutingAssembly();
            using var resourceStream = asm.GetManifestResourceStream("KernelTraceControl.dll")
                ?? throw new InvalidOperationException("KernelTraceControl.dll embedded resource is missing from the build.");
            using var fileStream = File.Create(dllPath);
            resourceStream.CopyTo(fileStream);
        }
    }
}
