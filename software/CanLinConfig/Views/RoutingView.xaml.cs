using System.Windows.Controls;

namespace CanLinConfig.Views;

public partial class RoutingView : UserControl
{
    public RoutingView() => InitializeComponent();
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
