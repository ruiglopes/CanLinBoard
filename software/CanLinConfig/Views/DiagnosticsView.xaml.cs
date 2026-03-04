using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;

namespace CanLinConfig.Views;

public partial class DiagnosticsView : UserControl
{
    public DiagnosticsView() => InitializeComponent();
}

public class PauseButtonConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "Resume" : "Pause";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
