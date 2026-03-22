using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
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

/// <summary>
/// Converts a bool to a SolidColorBrush: true = green, false = OrangeRed.
/// </summary>
public class BoolToGreenRedBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => new SolidColorBrush(value is true ? Colors.LimeGreen : Colors.OrangeRed);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
