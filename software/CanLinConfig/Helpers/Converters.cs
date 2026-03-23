using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace CanLinConfig.Helpers;

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : value;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : value;
}

public class ConnectButtonTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "Disconnect" : "Connect";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class ConnectionColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Colors.LimeGreen : Colors.Gray;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class HexUInt32Converter : IValueConverter
{
    public int Digits { get; set; } = 3;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        uint u = System.Convert.ToUInt32(value);
        return u == 0xFFFFFFFF ? "Passthrough" : u.ToString($"X{Digits}");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrWhiteSpace(s))
            return targetType == typeof(byte) ? (object)(byte)0 : (uint)0;
        s = s.Trim().Replace("0x", "").Replace("0X", "");
        if (uint.TryParse(s, NumberStyles.HexNumber, null, out uint result))
            return targetType == typeof(byte) ? (object)(byte)result : result;
        return Binding.DoNothing;
    }
}

public class PauseButtonConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "Resume" : "Pause";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class NullToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value != null;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
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

// Converter: inverse bool to Visibility
public class InverseBoolToVisConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Collapsed;
}

// Converter: null to Collapsed, non-null to Visible
public class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value != null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
