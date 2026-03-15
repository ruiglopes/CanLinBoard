using System.Windows;
using MahApps.Metro.Controls;
using CanLinConfig.Parsers;

namespace CanLinConfig.Views;

public partial class DbcImportDialog : MetroWindow
{
    public DbcMessage? SelectedControlMessage { get; private set; }
    public DbcMessage? SelectedStatusMessage { get; private set; }

    public DbcImportDialog(DbcFile dbc)
    {
        InitializeComponent();

        var items = dbc.Messages.Select(m => new DbcMessageItem(m)).ToList();
        var noneItem = new DbcMessageItem(null);

        MessageList.ItemsSource = items;

        ControlCombo.ItemsSource = new[] { noneItem }.Concat(items).ToList();
        StatusCombo.ItemsSource = new[] { noneItem }.Concat(items).ToList();
        ControlCombo.SelectedIndex = 0;
        StatusCombo.SelectedIndex = 0;

        // Auto-select first item in list
        if (items.Count > 0) MessageList.SelectedIndex = 0;

        // Update signal preview when selection changes
        ControlCombo.SelectionChanged += (_, _) => UpdatePreview();
        StatusCombo.SelectionChanged += (_, _) => UpdatePreview();
    }

    private void UpdatePreview()
    {
        var signals = new List<SignalPreviewItem>();

        if (ControlCombo.SelectedItem is DbcMessageItem ctrl && ctrl.Message != null)
        {
            foreach (var s in ctrl.Message.Signals)
                signals.Add(new SignalPreviewItem(s, "Control"));
        }

        if (StatusCombo.SelectedItem is DbcMessageItem status && status.Message != null)
        {
            foreach (var s in status.Message.Signals)
                signals.Add(new SignalPreviewItem(s, "Status"));
        }

        SignalPreview.ItemsSource = signals;
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        SelectedControlMessage = (ControlCombo.SelectedItem as DbcMessageItem)?.Message;
        SelectedStatusMessage = (StatusCombo.SelectedItem as DbcMessageItem)?.Message;

        if (SelectedControlMessage == null && SelectedStatusMessage == null)
        {
            MessageBox.Show("Select at least one message to import.", "Import",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

public class DbcMessageItem
{
    public DbcMessage? Message { get; }
    public string DisplayText { get; }

    public DbcMessageItem(DbcMessage? msg)
    {
        Message = msg;
        DisplayText = msg != null
            ? $"{msg.Name} (0x{msg.Id:X3}, {msg.Dlc}B, {msg.Signals.Count} signals)"
            : "(none)";
    }
}

public class SignalPreviewItem
{
    public string Name { get; }
    public string BitInfo { get; }
    public string Factor { get; }
    public string Range { get; }
    public string Unit { get; }

    public SignalPreviewItem(DbcSignal sig, string frame)
    {
        Name = sig.Name;
        string endian = sig.IsLittleEndian ? "LE" : "BE";
        string sign = sig.IsSigned ? "-" : "+";
        BitInfo = $"{sig.StartBit}|{sig.BitLength}{sign}";
        Factor = sig.Factor != 1.0 || sig.Offset != 0.0 ? $"{sig.Factor}x+{sig.Offset}" : "1:1";
        Range = $"{sig.MinValue}..{sig.MaxValue}";
        Unit = sig.Unit;
    }
}
