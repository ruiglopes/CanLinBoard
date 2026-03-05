using System.Windows.Controls;

namespace CanLinConfig.Views;

public partial class LinConfigView : UserControl
{
    public LinConfigView() => InitializeComponent();
}

public class LinDirectionItem
{
    public byte Value { get; set; }
    public string Name { get; set; } = "";
    public override string ToString() => Name;

    public static LinDirectionItem[] All { get; } =
    [
        new() { Value = 0, Name = "Subscribe" },
        new() { Value = 1, Name = "Publish" },
    ];
}
