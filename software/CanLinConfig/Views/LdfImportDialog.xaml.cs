using System.Windows;
using MahApps.Metro.Controls;
using CanLinConfig.Parsers;

namespace CanLinConfig.Views;

public partial class LdfImportDialog : MetroWindow
{
    public LdfScheduleTable? SelectedScheduleTable { get; private set; }
    public bool ImportSignals { get; private set; }

    public LdfImportDialog(LdfFile ldf)
    {
        InitializeComponent();

        // Populate header info
        SpeedText.Text = $"{ldf.SpeedKbps} kbps ({ldf.BaudRate} bps)";
        ProtocolText.Text = ldf.ProtocolVersion;
        MasterText.Text = ldf.MasterNode;
        SlavesText.Text = string.Join(", ", ldf.SlaveNodes);

        // Build frame list
        var frameItems = new List<LdfFrameItem>();
        foreach (var f in ldf.Frames)
        {
            frameItems.Add(new LdfFrameItem
            {
                Name = f.Name,
                Id = f.Id,
                Size = f.Size,
                Publisher = f.Publisher,
                Direction = f.DirectionForMaster(ldf.MasterNode),
            });
        }
        FrameGrid.ItemsSource = frameItems;

        // Populate schedule tables (only those with resolvable frames)
        var scheduleItems = new List<LdfScheduleItem>();
        foreach (var t in ldf.ScheduleTables)
        {
            if (t.Entries.Any(e => ldf.Frames.Any(f => f.Name == e.FrameName)))
                scheduleItems.Add(new LdfScheduleItem(t));
        }
        ScheduleList.ItemsSource = scheduleItems;
        if (scheduleItems.Count > 0) ScheduleList.SelectedIndex = 0;

        // Show warnings if empty
        if (ldf.Frames.Count == 0)
            FrameWarning.Text = "No frames found in LDF file.";
        else if (scheduleItems.Count == 0)
            FrameWarning.Text = "No schedule tables with resolvable frames found.";
        else
            FrameWarning.Visibility = Visibility.Collapsed;
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        SelectedScheduleTable = (ScheduleList.SelectedItem as LdfScheduleItem)?.Table;
        ImportSignals = ImportSignalsCheck.IsChecked == true;

        if (SelectedScheduleTable == null)
        {
            MessageBox.Show("Select a schedule table to import.", "Import",
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

public class LdfFrameItem
{
    public string Name { get; set; } = "";
    public byte Id { get; set; }
    public byte Size { get; set; }
    public string Publisher { get; set; } = "";
    public string Direction { get; set; } = "";
}

public class LdfScheduleItem
{
    public LdfScheduleTable Table { get; }
    public string DisplayText { get; }

    public LdfScheduleItem(LdfScheduleTable table)
    {
        Table = table;
        DisplayText = $"{table.Name} ({table.Entries.Count} entries, {table.TotalCycleMs:F0}ms cycle)";
    }
}
