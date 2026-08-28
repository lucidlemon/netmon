using System.Diagnostics;
using System.Text.RegularExpressions;

namespace NetMonGui.Services;

public static partial class PingService
{
    [GeneratedRegex(@"time[=<](\d+)ms")]
    private static partial Regex TimeRegex();

    /// <summary>
    /// Sends one ICMP echo sourced from the given adapter IP by shelling out to ping.exe -S,
    /// exactly like netmon.ps1 - this is what makes each adapter's measurement travel over
    /// its own physical link instead of whatever route Windows would pick by default.
    /// </summary>
    public static async Task<(bool Ok, double? Rtt)> PingOnceAsync(string sourceIp, string target, int timeoutMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ping.exe",
            Arguments = $"-n 1 -w {timeoutMs} -S {sourceIp} {target}",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        Process? p = null;
        try
        {
            p = new Process { StartInfo = psi };
            p.Start();
            var outputTask = p.StandardOutput.ReadToEndAsync();
            bool exited = await Task.Run(() => p.WaitForExit(timeoutMs + 500)).ConfigureAwait(false);
            if (!exited)
            {
                TryKill(p);
                return (false, null);
            }

            string output = await outputTask.ConfigureAwait(false);
            return ParsePingOutput(output);
        }
        catch
        {
            return (false, null);
        }
        finally
        {
            p?.Dispose();
        }
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(); } catch { /* best effort */ }
    }

    private static (bool Ok, double? Rtt) ParsePingOutput(string text)
    {
        var m = TimeRegex().Match(text);
        if (m.Success)
        {
            double t = double.Parse(m.Groups[1].Value);
            if (text.Contains("time<1ms")) t = 0.5;
            return (true, t);
        }
        return (false, null);
    }
}
