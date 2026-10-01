using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DnsAi.App.Models;

namespace DnsAi.App.Services;

public enum DnsMode
{
    LocalStub = 0,
    NativeWindows11 = 1
}

public class DnsProtectionController : IDisposable
{
    private readonly EndpointsConfig _config;
    private readonly DohClient _dohClient;
    private readonly DnsStubServer _stubServer;
    private readonly NetworkAdapterManager _adapterManager;
    private readonly Timer _probeTimer;

    public bool IsProtected { get; private set; }
    public DnsMode CurrentMode { get; private set; } = DnsMode.LocalStub;
    public string ActiveNodeIp => _dohClient.CurrentIpString;
    public int LatencyMs => _dohClient.LastLatencyMs;
    public long TotalQueries => _stubServer.TotalQueries;
    public long SuccessQueries => _stubServer.SuccessfulQueries;

    public event Action? StateChanged;
    public event Action? StatsChanged;

    public DnsProtectionController()
    {
        _config = EndpointsConfig.Load();
        _dohClient = new DohClient(_config);
        _stubServer = new DnsStubServer(_dohClient);
        _adapterManager = new NetworkAdapterManager();

        _stubServer.StatsUpdated += () => StatsChanged?.Invoke();

        // Periodic latency probe every 15 seconds
        _probeTimer = new Timer(async _ =>
        {
            if (IsProtected)
            {
                await _dohClient.ProbeLatencyAsync();
                StatsChanged?.Invoke();
            }
        }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(15));
    }

    public List<NetworkAdapterInfo> GetAdapters(bool includeVpn = false) => _adapterManager.GetAdapters(includeVpn);

    public async Task<(bool Success, string Message)> EnableProtectionAsync(DnsMode mode, IEnumerable<NetworkAdapterInfo> targetAdapters)
    {
        if (IsProtected)
            return (true, "Защита уже активна.");

        var selected = targetAdapters.Where(a => a.IsSelected && a.IsUp).ToList();
        if (selected.Count == 0)
        {
            return (false, "Выберите хотя бы один активный адаптер для защиты.");
        }

        // 1. Connectivity probe
        var probe = await _dohClient.ProbeLatencyAsync();
        if (!probe.Success)
        {
            probe = await _dohClient.ProbeLatencyAsync();
            if (!probe.Success)
            {
                return (false, "Не удалось связаться с серверами DNS-AI. Проверьте подключение к интернету.");
            }
        }

        CurrentMode = mode;

        if (mode == DnsMode.NativeWindows11)
        {
            if (!NativeDohService.IsWindows11Supported)
            {
                return (false, "Нативный DoH поддерживается только на Windows 11 (сборка 22000+). Используйте локальный резолвер.");
            }

            // Register DoH server templates in Windows 11
            NativeDohService.RegisterDohServers(_config.V4);

            // Apply DoH IP addresses directly to adapters
            if (!_adapterManager.ApplyDns(selected, _config.V4, _config.V6, out string adapterError))
            {
                NativeDohService.UnregisterDohServers(_config.V4);
                return (false, $"Не удалось переключить сетевой адаптер: {adapterError}");
            }
        }
        else
        {
            // Local stub mode (127.0.0.1:53)
            try
            {
                _stubServer.Start();
            }
            catch (Exception ex)
            {
                return (false, $"Ошибка запуска локального DNS: {ex.Message}");
            }

            if (!_adapterManager.ApplyDns(selected, new List<string> { "127.0.0.1" }, new List<string> { "::1" }, out string adapterError))
            {
                _stubServer.Stop();
                return (false, $"Не удалось переключить сетевой адаптер: {adapterError}");
            }
        }

        IsProtected = true;
        StateChanged?.Invoke();
        return (true, mode == DnsMode.NativeWindows11 
            ? "Защита успешно активирована (Встроенный DoH Windows 11)!" 
            : "Защита успешно активирована (Локальный резолвер)!");
    }

    public Task<(bool Success, string Message)> DisableProtectionAsync()
    {
        if (!IsProtected)
            return Task.FromResult((true, "Защита уже отключена."));

        // 1. Restore original DNS
        _adapterManager.RestoreDns(out string restoreError);

        // 2. Stop or unregister depending on mode
        if (CurrentMode == DnsMode.NativeWindows11)
        {
            NativeDohService.UnregisterDohServers(_config.V4);
        }
        else
        {
            _stubServer.Stop();
        }

        IsProtected = false;
        StateChanged?.Invoke();

        if (!string.IsNullOrEmpty(restoreError))
            return Task.FromResult((false, $"Предупреждение: {restoreError}"));

        return Task.FromResult((true, "Защита отключена, исходные настройки DNS восстановлены."));
    }

    public void FlushCache()
    {
        _adapterManager.FlushDnsCache();
    }

    public void Dispose()
    {
        if (IsProtected)
        {
            _adapterManager.RestoreDns(out _);
            if (CurrentMode == DnsMode.NativeWindows11)
            {
                NativeDohService.UnregisterDohServers(_config.V4);
            }
        }
        _probeTimer.Dispose();
        _stubServer.Dispose();
        _dohClient.Dispose();
    }
}
