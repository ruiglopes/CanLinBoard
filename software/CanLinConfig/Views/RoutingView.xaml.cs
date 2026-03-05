using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;

namespace CanLinConfig.Views;

public partial class RoutingView : UserControl
{
    public RoutingView() => InitializeComponent();
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

public class BusItem
{
    public byte Value { get; set; }
    public string Name { get; set; } = "";
    public override string ToString() => Name;

    public static BusItem[] All { get; } =
    [
        new() { Value = 0, Name = "CAN1" },
        new() { Value = 1, Name = "CAN2" },
        new() { Value = 2, Name = "LIN1" },
        new() { Value = 3, Name = "LIN2" },
        new() { Value = 4, Name = "LIN3" },
        new() { Value = 5, Name = "LIN4" },
    ];
}
