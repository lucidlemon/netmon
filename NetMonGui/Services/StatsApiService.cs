using System.Diagnostics;
using System.Net;
using System.Text;
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

public sealed record StatusDto(
    string Target,
    DateTime UpdatedAtUtc,
    List<AdapterStatDto> Adapters,
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

    public void Start()
    {
        try
        {
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
        }
        catch (Exception ex)
        {
            // Most likely another NetMonGui instance already owns the port. Non-fatal:
            // the app works fine without the API, callers just won't get live data.
            Debug.WriteLine($"StatsApiService: failed to start on port {Port}: {ex.Message}");
            return;
        }

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
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

            var snapshot = _latest;
            byte[] body = snapshot is null
                ? Encoding.UTF8.GetBytes("{}")
                : JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);

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
