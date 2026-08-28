using Velopack;
using Velopack.Sources;

namespace NetMonGui.Services;

/// <summary>
/// Background self-update check via Velopack, against this repo's GitHub Releases.
/// A no-op when the app isn't running from a Velopack install (e.g. `dotnet run`, or the
/// portable single-file exe some users may still grab directly) - IsInstalled covers that.
/// Every step is best-effort: network hiccups, GitHub rate limits, or no releases yet must
/// never interrupt the app the user is actually here for.
/// </summary>
public static class UpdateService
{
    private const string RepoUrl = "https://github.com/lucidlemon/netmon";

    public static async Task CheckForUpdatesAsync(Action<string> onStatus)
    {
        UpdateManager mgr;
        try
        {
            mgr = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
        }
        catch
        {
            return;
        }

        if (!mgr.IsInstalled) return;

        UpdateInfo? info;
        try
        {
            info = await mgr.CheckForUpdatesAsync();
        }
        catch
        {
            return;
        }

        if (info == null) return;

        var version = info.TargetFullRelease.Version;
        try
        {
            onStatus($"Update {version} found — downloading…");
            await mgr.DownloadUpdatesAsync(info);
        }
        catch (Exception ex)
        {
            onStatus($"Update {version} found but download failed: {ex.Message}");
            return;
        }

        onStatus($"Update {version} ready — restarting to apply it…");
        await Task.Delay(TimeSpan.FromSeconds(5));
        mgr.ApplyUpdatesAndRestart(info);
    }
}
