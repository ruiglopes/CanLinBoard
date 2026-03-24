using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CanLinConfig.Helpers;

public class EnumMatchConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null || parameter == null)
            return targetType == typeof(Visibility) ? Visibility.Collapsed : (object)false;
        bool match = value.ToString() == parameter.ToString();
        if (targetType == typeof(Visibility))
            return match ? Visibility.Visible : Visibility.Collapsed;
        return match;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
