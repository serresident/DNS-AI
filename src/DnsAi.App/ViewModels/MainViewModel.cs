using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Principal;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnsAi.App.Models;
using DnsAi.App.Services;

namespace DnsAi.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly DnsProtectionController _controller;
    private readonly AutostartService _autostartService;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusTitle = "Защита выключена";

    [ObservableProperty]
    private string _statusSubtitle = "Используется стандартный незашифрованный DNS";

    [ObservableProperty]
    private string _statusBadgeColor = "#7F8C8D";

    [ObservableProperty]
    private string _latencyText = "--";

    [ObservableProperty]
    private string _queriesText = "0";

    [ObservableProperty]
    private string _activeNodeText = "dns.dns-ai.ru";

    [ObservableProperty]
    private string _statusMessage = "Готов к работе";

    [ObservableProperty]
    private bool _isAdmin;

    [ObservableProperty]
    private bool _isWindows11;

    [ObservableProperty]
    private int _selectedModeIndex = 0; // 0 = LocalStub, 1 = NativeWindows11

    [ObservableProperty]
    private bool _includeVpnAdapters = false;

    [ObservableProperty]
    private bool _isAutostartEnabled;

    public ObservableCollection<NetworkAdapterInfo> Adapters { get; } = new();

    public MainViewModel()
    {
        _controller = new DnsProtectionController();
        _autostartService = new AutostartService();

        _controller.StateChanged += OnControllerStateChanged;
        _controller.StatsChanged += OnControllerStatsChanged;

        IsWindows11 = NativeDohService.IsWindows11Supported;
        IsAutostartEnabled = _autostartService.IsEnabled();

        CheckAdminPrivileges();
        RefreshAdapters();
        UpdateUiState();
    }

    partial void OnIncludeVpnAdaptersChanged(bool value)
    {
        RefreshAdapters();
    }

    partial void OnIsAutostartEnabledChanged(bool value)
    {
        _autostartService.SetEnabled(value);
        StatusMessage = value ? "Автозапуск включен" : "Автозапуск отключен";
    }

    private void CheckAdminPrivileges()
    {
        try
        {
            var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            IsAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            IsAdmin = false;
        }
    }

    [RelayCommand]
    public async Task ToggleProtectionAsync()
    {
        if (IsBusy) return;
        IsBusy = true;

        try
        {
            if (IsActive)
            {
                StatusMessage = "Отключение защиты...";
                var (success, msg) = await _controller.DisableProtectionAsync();
                StatusMessage = msg;
                IsActive = _controller.IsProtected;
            }
            else
            {
                StatusMessage = "Подключение к DNS-AI...";
                var mode = (DnsMode)SelectedModeIndex;
                var (success, msg) = await _controller.EnableProtectionAsync(mode, Adapters);
                StatusMessage = msg;
                IsActive = _controller.IsProtected;
            }
        }
        finally
        {
            IsBusy = false;
            UpdateUiState();
            RefreshAdapters();
        }
    }

    [RelayCommand]
    public void FlushDns()
    {
        _controller.FlushCache();
        StatusMessage = "Кэш DNS успешно очищен (ipconfig /flushdns)";
    }

    [RelayCommand]
    public void RefreshAdapters()
    {
        Adapters.Clear();
        var list = _controller.GetAdapters(IncludeVpnAdapters);
        foreach (var a in list)
        {
            Adapters.Add(a);
        }
    }

    private void OnControllerStateChanged()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsActive = _controller.IsProtected;
            UpdateUiState();
            RefreshAdapters();
        });
    }

    private void OnControllerStatsChanged()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            LatencyText = _controller.LatencyMs > 0 ? $"{_controller.LatencyMs} мс" : "< 10 мс";
            QueriesText = _controller.TotalQueries.ToString();
            ActiveNodeText = _controller.ActiveNodeIp;
        });
    }

    private void UpdateUiState()
    {
        if (IsActive)
        {
            var modeDesc = _controller.CurrentMode == DnsMode.NativeWindows11
                ? "Встроенный DoH Windows 11"
                : "Локальный DoH-резолвер (127.0.0.1)";

            StatusTitle = "Защита DNS активна";
            StatusSubtitle = $"Режим: {modeDesc} через dns.dns-ai.ru";
            StatusBadgeColor = "#2ECC71"; // Emerald green
        }
        else
        {
            StatusTitle = "Защита выключена";
            StatusSubtitle = "Используется стандартный незашифрованный DNS";
            StatusBadgeColor = "#7F8C8D"; // Muted gray
            LatencyText = "--";
        }
    }

    public void Shutdown()
    {
        _controller.Dispose();
    }
}
