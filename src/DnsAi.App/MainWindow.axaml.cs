using System;
using Avalonia.Controls;
using DnsAi.App.ViewModels;

namespace DnsAi.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var vm = new MainViewModel();
        DataContext = vm;

        Closing += (s, e) =>
        {
            // Clean up resources and restore DNS if window is closed
            vm.Shutdown();
        };
    }
}