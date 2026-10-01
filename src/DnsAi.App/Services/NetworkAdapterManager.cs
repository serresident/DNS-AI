using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.Json;
using DnsAi.App.Models;
using Microsoft.Win32;

namespace DnsAi.App.Services;

public class NetworkAdapterManager
{
    private static readonly string[] VirtualMarkers = new[]
    {
        "vethernet", "hyper-v", "vmware", "virtualbox", "vbox", "wsl",
        "docker", "loopback", "wi-fi direct", "wifi direct", "microsoft wi-fi direct",
        "teredo", "isatap", "bluetooth", "npcap", "packet scheduler"
    };

    private static readonly string[] VpnMarkers = new[]
    {
        "wireguard", "wintun", "openvpn", "tap-windows", "tap adapter",
        "tailscale", "zerotier", "anyconnect", "nordlynx", "globalprotect",
        "pangp", "forticlient", "amnezia", "mullvad", "proton", "outline",
        "wan miniport", "softether", "hamachi"
    };

    private readonly string _backupFilePath;

    public NetworkAdapterManager()
    {
        var localAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DNS-AI");
        Directory.CreateDirectory(localAppData);
        _backupFilePath = Path.Combine(localAppData, "dns_backup.json");
    }

    public bool HasPendingBackup => File.Exists(_backupFilePath);

    /// <summary>
    /// Enumerates adapters with optional VPN inclusion
    /// </summary>
    public List<NetworkAdapterInfo> GetAdapters(bool includeVpn = false)
    {
        var result = new List<NetworkAdapterInfo>();

        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var ni in interfaces)
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                var ipProps = ni.GetIPProperties();
                var v4Props = ipProps.GetIPv4Properties();
                int index = v4Props?.Index ?? 0;

                var info = new NetworkAdapterInfo
                {
                    InterfaceIndex = index,
                    Guid = ni.Id,
                    Name = ni.Name,
                    Description = ni.Description,
                    TypeName = ni.NetworkInterfaceType.ToString(),
                    IsUp = ni.OperationalStatus == OperationalStatus.Up
                };

                var dnsAddresses = ipProps.DnsAddresses.Select(a => a.ToString()).ToList();
                info.CurrentDnsServers = dnsAddresses;
                info.IsConfiguredToLocalhost = dnsAddresses.Any(d => d == "127.0.0.1" || d == "::1");

                var haystack = $"{ni.Name} {ni.Description}".ToLowerInvariant();
                var virtualMatch = VirtualMarkers.FirstOrDefault(m => haystack.Contains(m));
                var vpnMatch = VpnMarkers.FirstOrDefault(m => haystack.Contains(m));

                if (virtualMatch != null)
                {
                    info.IsPhysical = false;
                    info.IsVpn = false;
                    info.IsSelected = false;
                    info.SkipReason = virtualMatch;
                }
                else if (vpnMatch != null)
                {
                    info.IsPhysical = false;
                    info.IsVpn = true;
                    info.IsSelected = includeVpn && info.IsUp;
                    info.SkipReason = $"VPN: {vpnMatch}";
                }
                else
                {
                    info.IsPhysical = true;
                    info.IsVpn = false;
                    info.IsSelected = info.IsUp;
                }

                result.Add(info);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error querying adapters: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Applies given DNS servers to the specified target adapters, saving backup
    /// </summary>
    public bool ApplyDns(IEnumerable<NetworkAdapterInfo> targetAdapters, List<string> v4Servers, List<string>? v6Servers, out string error)
    {
        error = string.Empty;
        var adapters = targetAdapters.Where(a => a.IsUp && a.InterfaceIndex > 0).ToList();

        if (adapters.Count == 0)
        {
            error = "Не выбрано ни одного активного сетевого адаптера для настройки.";
            return false;
        }

        // 1. Create and save backup
        var backup = new DnsBackupState();
        foreach (var adapter in adapters)
        {
            var entry = new AdapterBackupEntry
            {
                InterfaceIndex = adapter.InterfaceIndex,
                Name = adapter.Name,
                Guid = adapter.Guid
            };

            ReadAdapterDnsRegistry(adapter.Guid, "Tcpip", out bool isDhcp4, out var static4);
            entry.WasDhcpV4 = isDhcp4;
            entry.StaticDnsV4 = static4;

            ReadAdapterDnsRegistry(adapter.Guid, "Tcpip6", out bool isDhcp6, out var static6);
            entry.WasDhcpV6 = isDhcp6;
            entry.StaticDnsV6 = static6;

            backup.Adapters.Add(entry);
        }

        try
        {
            var json = JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_backupFilePath, json);
        }
        catch (Exception ex)
        {
            error = $"Не удалось сохранить резервную копию DNS: {ex.Message}";
            return false;
        }

        // 2. Apply DNS servers to each chosen adapter
        foreach (var adapter in adapters)
        {
            for (int i = 0; i < v4Servers.Count; i++)
            {
                var server = v4Servers[i];
                if (i == 0)
                {
                    RunNetsh($"interface ipv4 set dnsservers name=\"{adapter.InterfaceIndex}\" static {server} primary");
                }
                else
                {
                    RunNetsh($"interface ipv4 add dnsservers name=\"{adapter.InterfaceIndex}\" {server} index={i + 1}");
                }
            }

            if (v6Servers != null && v6Servers.Count > 0)
            {
                for (int i = 0; i < v6Servers.Count; i++)
                {
                    var server = v6Servers[i];
                    if (i == 0)
                    {
                        RunNetsh($"interface ipv6 set dnsservers name=\"{adapter.InterfaceIndex}\" static {server} primary validate=no");
                    }
                    else
                    {
                        RunNetsh($"interface ipv6 add dnsservers name=\"{adapter.InterfaceIndex}\" {server} index={i + 1} validate=no");
                    }
                }
            }
        }

        FlushDnsCache();
        return true;
    }

    /// <summary>
    /// Restores DNS settings from the saved backup
    /// </summary>
    public bool RestoreDns(out string error)
    {
        error = string.Empty;

        if (!File.Exists(_backupFilePath))
        {
            var adapters = GetAdapters().Where(a => a.IsUp && a.InterfaceIndex > 0);
            foreach (var a in adapters)
            {
                RunNetsh($"interface ipv4 set dnsservers name=\"{a.InterfaceIndex}\" source=dhcp");
                RunNetsh($"interface ipv6 set dnsservers name=\"{a.InterfaceIndex}\" source=dhcp");
            }
            FlushDnsCache();
            return true;
        }

        try
        {
            var json = File.ReadAllText(_backupFilePath);
            var backup = JsonSerializer.Deserialize<DnsBackupState>(json);

            if (backup != null)
            {
                foreach (var entry in backup.Adapters)
                {
                    // Restore IPv4
                    if (entry.WasDhcpV4 || entry.StaticDnsV4.Count == 0)
                    {
                        RunNetsh($"interface ipv4 set dnsservers name=\"{entry.InterfaceIndex}\" source=dhcp");
                    }
                    else
                    {
                        for (int i = 0; i < entry.StaticDnsV4.Count; i++)
                        {
                            var ip = entry.StaticDnsV4[i];
                            if (i == 0)
                            {
                                RunNetsh($"interface ipv4 set dnsservers name=\"{entry.InterfaceIndex}\" static {ip} primary");
                            }
                            else
                            {
                                RunNetsh($"interface ipv4 add dnsservers name=\"{entry.InterfaceIndex}\" {ip} index={i + 1}");
                            }
                        }
                    }

                    // Restore IPv6
                    if (entry.WasDhcpV6 || entry.StaticDnsV6.Count == 0)
                    {
                        RunNetsh($"interface ipv6 set dnsservers name=\"{entry.InterfaceIndex}\" source=dhcp");
                    }
                    else
                    {
                        for (int i = 0; i < entry.StaticDnsV6.Count; i++)
                        {
                            var ip = entry.StaticDnsV6[i];
                            if (i == 0)
                            {
                                RunNetsh($"interface ipv6 set dnsservers name=\"{entry.InterfaceIndex}\" static {ip} primary validate=no");
                            }
                            else
                            {
                                RunNetsh($"interface ipv6 add dnsservers name=\"{entry.InterfaceIndex}\" {ip} index={i + 1} validate=no");
                            }
                        }
                    }
                }
            }

            File.Delete(_backupFilePath);
            FlushDnsCache();
            return true;
        }
        catch (Exception ex)
        {
            error = $"Ошибка при восстановлении DNS: {ex.Message}";
            return false;
        }
    }

    public void FlushDnsCache()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ipconfig",
                Arguments = "/flushdns",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
        }
        catch { }
    }

    private static void ReadAdapterDnsRegistry(string guid, string service, out bool isDhcp, out List<string> staticServers)
    {
        isDhcp = true;
        staticServers = new List<string>();

        try
        {
            var keyPath = $@"SYSTEM\CurrentControlSet\Services\{service}\Parameters\Interfaces\{guid}";
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            if (key != null)
            {
                var ns = key.GetValue("NameServer") as string;
                if (!string.IsNullOrWhiteSpace(ns))
                {
                    isDhcp = false;
                    staticServers = ns.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                }
            }
        }
        catch { }
    }

    private static bool RunNetsh(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p != null)
            {
                p.WaitForExit(5000);
                return p.ExitCode == 0;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"netsh failed ({arguments}): {ex.Message}");
        }
        return false;
    }
}
