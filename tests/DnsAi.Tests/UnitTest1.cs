using System;
using System.Threading.Tasks;
using DnsAi.App.Models;
using DnsAi.App.Services;
using Xunit;

namespace DnsAi.Tests;

public class DnsAiIntegrationTests
{
    [Fact]
    public void EndpointsConfig_LoadsDefaultsProperly()
    {
        var config = EndpointsConfig.Load();
        Assert.NotNull(config);
        Assert.NotEmpty(config.V4);
        Assert.Contains("192.144.59.14", config.V4);
    }

    [Fact]
    public void NetworkAdapterManager_FindsAdapters()
    {
        var manager = new NetworkAdapterManager();
        var adapters = manager.GetAdapters(includeVpn: false);
        Assert.NotNull(adapters);
        Assert.NotEmpty(adapters);
    }

    [Fact]
    public void NetworkAdapterManager_IncludesVpnWhenRequested()
    {
        var manager = new NetworkAdapterManager();
        var standardAdapters = manager.GetAdapters(includeVpn: false);
        var vpnAdapters = manager.GetAdapters(includeVpn: true);

        Assert.Equal(standardAdapters.Count, vpnAdapters.Count);
        // If there are VPN adapters, their IsSelected flag should differ based on includeVpn
        var hasVpn = vpnAdapters.Exists(a => a.IsVpn);
        if (hasVpn)
        {
            var vpnInStandard = standardAdapters.Find(a => a.IsVpn);
            var vpnInRequested = vpnAdapters.Find(a => a.IsVpn);
            Assert.False(vpnInStandard?.IsSelected);
            if (vpnInRequested?.IsUp == true)
            {
                Assert.True(vpnInRequested.IsSelected);
            }
        }
    }

    [Fact]
    public void AutostartService_CanCheckStatusWithoutError()
    {
        var service = new AutostartService();
        // Should not throw
        var enabled = service.IsEnabled();
        Assert.True(enabled || !enabled);
    }

    [Fact]
    public void NativeDohService_CanCheckWindows11Support()
    {
        var isWin11 = NativeDohService.IsWindows11Supported;
        var build = Environment.OSVersion.Version.Build;
        Assert.Equal(build >= 22000, isWin11);
    }

    [Fact]
    public async Task DohClient_CanProbeOrConnectToResolver()
    {
        using var client = new DohClient();
        var (success, latency, ip) = await client.ProbeLatencyAsync();
        
        Assert.NotNull(ip);
        Assert.NotEqual("N/A", ip);
    }
}