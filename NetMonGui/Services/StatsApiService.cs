using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetMonGui.Services;

public sealed record ThresholdDto(double Ms, string Label, string Color);

public sealed record MetricDto(double? Ms, string? Tier, string? Color);

public sealed record AdapterStatDto(
    string Name,
    string Description,
    bool IsBest,
    bool IsOsPreferred,
    MetricDto Ping,
    MetricDto Jitter,
    double LossPercent);

public sealed record TargetStatusDto(
    string Id,
    string Label,
    string Host,
    List<AdapterStatDto> Adapters);

public sealed record StatusDto(
    DateTime UpdatedAtUtc,
    List<TargetStatusDto> Targets,
    List<ThresholdDto> LatencyThresholds,
    List<ThresholdDto> JitterThresholds);

/// <summary>
/// Tiny loopback-only JSON API so local tools (e.g. a Stream Deck plugin) can read live
/// per-adapter ping/jitter without any auth — nothing here is reachable off-box because
/// HttpListener is bound to the literal 127.0.0.1 host, never a wildcard.
/// </summary>
public sealed class StatsApiService : IDisposable
{
    public const int Port = 47115;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly HttpListener _listener = new();
    private volatile StatusDto? _latest;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Starts the API. Returns null on success, or an error message on failure - most likely
    /// another NetMonGui instance already owns the port, since only one process can bind it.
    /// Failure is non-fatal to the app itself (it works fine without the API), but callers should
    /// surface the message somewhere, since a silent failure here previously left users unable to
    /// tell why the Stream Deck plugin couldn't connect.
    /// </summary>
    public string? Start()
    {
        try
        {
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"StatsApiService: failed to start on port {Port}: {ex.Message}");
            return $"Local API (port {Port}) didn't start - probably another NetMon instance is already running. " +
                   "The Stream Deck plugin won't be able to connect until only one is open.";
        }

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        return null;
    }

    public void UpdateSnapshot(StatusDto snapshot) => _latest = snapshot;

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                if (token.IsCancellationRequested) return;
                continue;
            }

            _ = Task.Run(() => Handle(ctx), token);
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            var res = ctx.Response;
            res.Headers.Add("Access-Control-Allow-Origin", "*");

            if (ctx.Request.Url?.AbsolutePath != "/api/status")
            {
                res.StatusCode = 404;
                res.Close();
                return;
            }

            // A bare "{}" here (the previous fallback) is a different shape than StatusDto - every
            // client has to special-case it, and the Stream Deck plugin didn't: it read .targets off
            // that empty object during the ~1s window before the first tick populates _latest,
            // uncaught-TypeError'd on undefined.find(), and took the whole plugin process down with
            // it. A real (empty) StatusDto keeps the shape consistent so clients don't need to know
            // about this window at all.
            var snapshot = _latest ?? new StatusDto(DateTime.UtcNow, new List<TargetStatusDto>(), new List<ThresholdDto>(), new List<ThresholdDto>());
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);

            res.ContentType = "application/json; charset=utf-8";
            res.ContentLength64 = body.Length;
            res.OutputStream.Write(body, 0, body.Length);
            res.Close();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"StatsApiService: request handling failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
            if (_listener.IsListening) _listener.Stop();
            _listener.Close();
        }
        catch
        {
            // best effort shutdown
        }
    }
}
