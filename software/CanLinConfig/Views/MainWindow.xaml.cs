using System.ComponentModel;
using System.Windows;
using MahApps.Metro.Controls;
using CanLinConfig.ViewModels;

namespace CanLinConfig.Views;

public partial class MainWindow : MetroWindow
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (DataContext is MainViewModel vm && !vm.CanClose())
        {
            e.Cancel = true;
            return;
        }

        (DataContext as IDisposable)?.Dispose();
    }
}
