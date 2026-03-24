using System.Windows;
using System.Windows.Controls;

namespace CanLinConfig.Views.Widgets;

public partial class BitPanelConfigDialog : Window
{
    private readonly TextBox[] _labelBoxes = new TextBox[8];

    public int ResultBitCount { get; private set; }
    public List<string> ResultLabels { get; private set; } = [];

    public BitPanelConfigDialog(int bitCount, List<string> labels)
    {
        InitializeComponent();
        ResultBitCount = bitCount;
        ResultLabels = new List<string>(labels);
        BitCountBox.Text = bitCount.ToString();
        BitCountBox.TextChanged += (_, _) => RebuildLabels();
        RebuildLabels();
    }

    private void RebuildLabels()
    {
        if (!int.TryParse(BitCountBox.Text, out int count)) return;
        count = Math.Clamp(count, 1, 8);
        LabelPanel.Children.Clear();

        for (int i = 0; i < count; i++)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            sp.Children.Add(new TextBlock
            {
                Text = $"Bit {i}:", Width = 45, Foreground = System.Windows.Media.Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center
            });
            var tb = new TextBox { Width = 200, Text = i < ResultLabels.Count ? ResultLabels[i] : $"Bit {i}" };
            _labelBoxes[i] = tb;
            sp.Children.Add(tb);
            LabelPanel.Children.Add(sp);
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(BitCountBox.Text, out int count)) return;
        count = Math.Clamp(count, 1, 8);
        ResultBitCount = count;
        ResultLabels = new List<string>();
        for (int i = 0; i < count; i++)
            ResultLabels.Add(_labelBoxes[i]?.Text ?? $"Bit {i}");
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
