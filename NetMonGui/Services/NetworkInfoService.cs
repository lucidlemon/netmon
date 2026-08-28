using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetMonGui.Services;

public sealed record AdapterInfo(string Name, string Description, string SourceIp, int InterfaceIndex);

/// <summary>
/// Adapter discovery and OS routing-preference lookup via WMI (root\StandardCimv2),
/// the same CIM provider Get-NetAdapter / Get-NetRoute / Get-NetIPInterface use.
/// Also owns the (elevated) "prefer this adapter" action.
/// </summary>
public static class NetworkInfoService
{
    private const string Namespace = @"root\StandardCimv2";

    public static List<AdapterInfo> GetActiveInterfaces()
    {
        var result = new List<AdapterInfo>();
        using var searcher = new ManagementObjectSearcher(Namespace,
            "SELECT Name, InterfaceDescription, InterfaceIndex, Virtual, InterfaceOperationalStatus FROM MSFT_NetAdapter");

        foreach (ManagementObject mo in searcher.Get())
        {
            bool isVirtual = mo["Virtual"] is bool v && v;
            int opStatus = mo["InterfaceOperationalStatus"] is null ? 0 : Convert.ToInt32(mo["InterfaceOperationalStatus"]);
            if (isVirtual || opStatus != 1) continue; // 1 = up

            int ifIndex = Convert.ToInt32(mo["InterfaceIndex"]);
            string name = mo["Name"]?.ToString() ?? "";
            string desc = mo["InterfaceDescription"]?.ToString() ?? "";

            string? ip = GetIPv4Address(ifIndex);
            if (ip == null) continue;

            result.Add(new AdapterInfo(name, desc, ip, ifIndex));
        }

        return result;
    }

    private static string? GetIPv4Address(int ifIndex)
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            IPInterfaceProperties props;
            try { props = ni.GetIPProperties(); }
            catch { continue; }

            var v4 = props.GetIPv4Properties();
            if (v4 == null || v4.Index != ifIndex) continue;

            foreach (var addr in props.UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !addr.Address.ToString().StartsWith("169.254."))
                {
                    return addr.Address.ToString();
                }
            }
        }
        return null;
    }

    /// <summary>Name of the adapter Windows currently routes default IPv4 traffic through, if it's one we're monitoring.</summary>
    public static string? GetOsPreferredInterfaceName(IReadOnlyDictionary<int, string> ifIndexToName)
    {
        using var routeSearcher = new ManagementObjectSearcher(Namespace,
            "SELECT InterfaceIndex, RouteMetric FROM MSFT_NetRoute WHERE DestinationPrefix='0.0.0.0/0' AND AddressFamily=2");

        string? bestName = null;
        double bestMetric = double.MaxValue;

        foreach (ManagementObject mo in routeSearcher.Get())
        {
            int ifIndex = Convert.ToInt32(mo["InterfaceIndex"]);
            if (!ifIndexToName.TryGetValue(ifIndex, out var name)) continue;

            double routeMetric = Convert.ToDouble(mo["RouteMetric"]);
            double ifMetric = GetInterfaceMetric(ifIndex);
            double effective = routeMetric + ifMetric;
            if (effective < bestMetric)
            {
                bestMetric = effective;
                bestName = name;
            }
        }

        return bestName;
    }

    private static double GetInterfaceMetric(int ifIndex)
    {
        using var searcher = new ManagementObjectSearcher(Namespace,
            $"SELECT InterfaceMetric FROM MSFT_NetIPInterface WHERE InterfaceIndex={ifIndex} AND AddressFamily=2");
        foreach (ManagementObject mo in searcher.Get())
        {
            return Convert.ToDouble(mo["InterfaceMetric"]);
        }
        return 0;
    }

    /// <summary>
    /// Lowers the given adapter's IPv4 interface metric so Windows prefers it for routing,
    /// and resets the other monitored adapters back to automatic metric. Requires elevation
    /// (triggers a UAC prompt) - the app itself keeps running unelevated.
    /// </summary>
    public static (bool Success, string? Error) SetPreferredAdapter(int preferredIfIndex, IEnumerable<int> otherIfIndexes)
    {
        var cmd = new System.Text.StringBuilder();
        cmd.Append($"Set-NetIPInterface -InterfaceIndex {preferredIfIndex} -AddressFamily IPv4 -InterfaceMetric 1 -ErrorAction Stop;");
        foreach (var idx in otherIfIndexes.Distinct())
        {
            if (idx == preferredIfIndex) continue;
            cmd.Append($" Set-NetIPInterface -InterfaceIndex {idx} -AddressFamily IPv4 -AutomaticMetric Enabled -ErrorAction Stop;");
        }

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{cmd}\"",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            using var p = Process.Start(psi);
            p!.WaitForExit();
            return p.ExitCode == 0
                ? (true, null)
                : (false, $"powershell.exe exited with code {p.ExitCode}");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, "Elevation was cancelled.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
