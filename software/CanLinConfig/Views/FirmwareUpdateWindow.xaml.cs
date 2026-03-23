using System.Windows;
using System.Windows.Controls;
using MahApps.Metro.Controls;
using CanLinConfig.ViewModels;

namespace CanLinConfig.Views;

public partial class FirmwareUpdateWindow : MetroWindow
{
    public FirmwareUpdateWindow(MainViewModel mainVm)
    {
        InitializeComponent();
        DataContext = new FirmwareUpdateViewModel(mainVm);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void LogTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb)
            tb.ScrollToEnd();
    }
}
