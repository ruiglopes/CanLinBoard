using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;

namespace CanLinConfig.Views;

public partial class ProfilesView : UserControl
{
    public ProfilesView() => InitializeComponent();
}

public class NullToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value != null;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
