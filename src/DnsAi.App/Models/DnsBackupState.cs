using System;
using System.Collections.Generic;

namespace DnsAi.App.Models;

public class AdapterBackupEntry
{
    public int InterfaceIndex { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Guid { get; set; } = string.Empty;
    public bool WasDhcpV4 { get; set; }
    public List<string> StaticDnsV4 { get; set; } = new();
    public bool WasDhcpV6 { get; set; }
    public List<string> StaticDnsV6 { get; set; } = new();
}

public class DnsBackupState
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public List<AdapterBackupEntry> Adapters { get; set; } = new();
}
