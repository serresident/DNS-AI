using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using DnsAi.App.Models;

namespace DnsAi.App.Services;

public class NativeDohService
{
    public static bool IsWindows11Supported => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>
    /// Registers the DNS-AI DoH template into Windows 11 DoH client table
    /// </summary>
    public static bool RegisterDohServers(IEnumerable<string> serverIps, string dohTemplate = EndpointsConfig.DefaultDohUrl)
    {
        if (!IsWindows11Supported) return false;

        try
        {
            foreach (var ip in serverIps)
            {
                var script = $@"
$ip = '{ip}'
$tmpl = '{dohTemplate}'
try {{
    if (-not (Get-DnsClientDohServerAddress -ServerAddress $ip -ErrorAction SilentlyContinue)) {{
        Add-DnsClientDohServerAddress -ServerAddress $ip -DohTemplate $tmpl -AllowFallbackToUdp $false -AutoUpgrade $true -ErrorAction SilentlyContinue
    }}
}} catch {{}}
";
                RunPowerShell(script);
            }
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"RegisterDohServers failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Unregisters the DoH templates from Windows 11
    /// </summary>
    public static bool UnregisterDohServers(IEnumerable<string> serverIps)
    {
        if (!IsWindows11Supported) return false;

        try
        {
            foreach (var ip in serverIps)
            {
                var script = $"Remove-DnsClientDohServerAddress -ServerAddress '{ip}' -ErrorAction SilentlyContinue";
                RunPowerShell(script);
            }
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"UnregisterDohServers failed: {ex.Message}");
            return false;
        }
    }

    private static void RunPowerShell(string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command.Replace("\"", "`\"")}\"",
            CreateNoWindow = true,
            UseShellExecute = false
        };
        using var p = Process.Start(psi);
        p?.WaitForExit(5000);
    }
}
