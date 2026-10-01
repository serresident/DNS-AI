using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DnsAi.App.Models;

public partial class NetworkAdapterInfo : ObservableObject
{
    public int InterfaceIndex { get; set; }
    public string Guid { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public bool IsUp { get; set; }
    public bool IsPhysical { get; set; }
    public bool IsVpn { get; set; }
    public string SkipReason { get; set; } = string.Empty;
    public List<string> CurrentDnsServers { get; set; } = new();
    public bool IsConfiguredToLocalhost { get; set; }

    [ObservableProperty]
    private bool _isSelected = true;

    public bool CanSelect => IsUp && (IsPhysical || IsVpn);

    public string DisplayStatus => !IsUp 
        ? "Отключен" 
        : IsConfiguredToLocalhost 
            ? "Защищен (127.0.0.1)" 
            : IsVpn 
                ? (IsSelected ? "VPN (включен)" : "VPN (пропущен)")
                : !IsPhysical 
                    ? $"Пропущен ({SkipReason})" 
                    : "Обычный DNS";
}
