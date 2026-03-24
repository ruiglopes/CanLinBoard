# Log Replay (Plan 5D) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add log replay — load CSV log files and play them back through BusDataService at configurable speed, so all existing panels (trace, signals, graph, instruments) display the replayed data.

**Architecture:** A `LogReplayService` loads a CSV file into a `List<BusFrame>`, then uses a `DispatcherTimer` to feed frames into `BusDataService.OnFrame()` at the selected speed multiplier. The `LogReplayViewModel` exposes play/pause/stop/speed controls and a timeline position. The `LogReplayPanel` UserControl is added to the Data Logger tab.

**Tech Stack:** C# .NET 8 (WPF, CommunityToolkit.Mvvm, xUnit)

**Spec:** `docs/superpowers/specs/2026-03-23-bus-monitor-logger-design.md` § 5
**Master tracker:** `docs/bus-monitor-logger-master-plan.md` (Plan 5D)
**Branch:** `feature/bus-monitor-foundation`

**Important:** Do NOT commit specs or plans. Only commit actual code changes, and only after the feature is completed and tested.

---

## File Map

### New Files

| File | Responsibility |
|------|---------------|
| `software/CanLinConfig/Services/CsvLogImporter.cs` | Parse CSV log files back into BusFrame lists |
| `software/CanLinConfig/ViewModels/LogReplayViewModel.cs` | Replay controls: load, play, pause, stop, speed, timeline position |
| `software/CanLinConfig/Views/LogReplayPanel.xaml` | Replay panel UI |
| `software/CanLinConfig/Views/LogReplayPanel.xaml.cs` | Code-behind |
| `software/CanLinConfig.Tests/CsvLogImporterTests.cs` | Importer unit tests |

### Modified Files

| File | Changes |
|------|---------|
| `software/CanLinConfig/ViewModels/DataLoggerViewModel.cs` | Add LogReplay sub-VM |
| `software/CanLinConfig/Views/DataLoggerView.xaml` | Add LogReplayPanel row |

---

## Task 1: CSV Log Importer with Tests

**Files:**
- Create: `software/CanLinConfig.Tests/CsvLogImporterTests.cs`
- Create: `software/CanLinConfig/Services/CsvLogImporter.cs`

### Step-by-step:

- [ ] **Step 1: Write tests**

```csharp
// software/CanLinConfig.Tests/CsvLogImporterTests.cs
using CanLinConfig.Models;
using CanLinConfig.Services;

namespace CanLinConfig.Tests;

public class CsvLogImporterTests
{
    [Fact]
    public void Import_parses_standard_csv()
    {
        var csv = "timestamp_ms,bus,id,dlc,data,message\n" +
                  "1000,CAN1,0x100,3,AA BB CC,\n" +
                  "2000,CAN2,0x200,2,11 22,\n";

        var frames = CsvLogImporter.Import(csv);

        Assert.Equal(2, frames.Count);
        Assert.Equal(BusFrame.Bus.CAN1, frames[0].SourceBus);
        Assert.Equal(0x100u, frames[0].Id);
        Assert.Equal(3, frames[0].Dlc);
        Assert.Equal(0xAA, frames[0].Data[0]);
        Assert.Equal(BusFrame.Bus.CAN2, frames[1].SourceBus);
        Assert.Equal(0x200u, frames[1].Id);
    }

    [Fact]
    public void Import_parses_LIN_buses()
    {
        var csv = "timestamp_ms,bus,id,dlc,data,message\n" +
                  "0,LIN1,0x3C,8,01 02 03 04 05 06 07 08,\n" +
                  "100,LIN4,0x1A,2,FF 00,\n";

        var frames = CsvLogImporter.Import(csv);

        Assert.Equal(2, frames.Count);
        Assert.Equal(BusFrame.Bus.LIN1, frames[0].SourceBus);
        Assert.Equal(BusFrame.Bus.LIN4, frames[1].SourceBus);
    }

    [Fact]
    public void Import_handles_empty_file()
    {
        var frames = CsvLogImporter.Import("timestamp_ms,bus,id,dlc,data,message\n");
        Assert.Empty(frames);
    }

    [Fact]
    public void Import_skips_malformed_lines()
    {
        var csv = "timestamp_ms,bus,id,dlc,data,message\n" +
                  "bad,line\n" +
                  "1000,CAN1,0x100,2,AA BB,\n";

        var frames = CsvLogImporter.Import(csv);
        Assert.Single(frames);
    }

    [Fact]
    public void Import_from_stream()
    {
        var csv = "timestamp_ms,bus,id,dlc,data,message\n" +
                  "500,CAN1,0x100,1,FF,\n";

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv));
        var frames = CsvLogImporter.ImportFromStream(stream);

        Assert.Single(frames);
        Assert.Equal(0xFFu, frames[0].Data[0]);
    }
}
```

- [ ] **Step 2: Run tests — verify they fail**

Run:
```bash
cd software && dotnet test CanLinConfig.Tests --filter "CsvLogImporter" -v n
```
Expected: Compile error.

- [ ] **Step 3: Implement CsvLogImporter**

```csharp
// software/CanLinConfig/Services/CsvLogImporter.cs
using System.Globalization;
using System.IO;
using System.Text;
using CanLinConfig.Models;

namespace CanLinConfig.Services;

public static class CsvLogImporter
{
    public static List<BusFrame> Import(string csvText)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csvText));
        return ImportFromStream(stream);
    }

    public static List<BusFrame> ImportFromStream(Stream stream)
    {
        var frames = new List<BusFrame>();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

        // Skip header
        var header = reader.ReadLine();
        if (header == null) return frames;

        while (reader.ReadLine() is { } line)
        {
            try
            {
                var frame = ParseLine(line);
                if (frame != null)
                    frames.Add(frame);
            }
            catch
            {
                // Skip malformed lines
            }
        }

        return frames;
    }

    private static BusFrame? ParseLine(string line)
    {
        // Format: timestamp_ms,bus,id,dlc,data,message
        var parts = line.Split(',', 6);
        if (parts.Length < 5) return null;

        if (!uint.TryParse(parts[0].Trim(), out uint timestampMs))
            return null;

        var bus = ParseBus(parts[1].Trim());
        if (bus == BusFrame.Bus.Unknown) return null;

        var idStr = parts[2].Trim();
        uint id;
        if (idStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            id = uint.Parse(idStr[2..], NumberStyles.HexNumber);
        else
            id = uint.Parse(idStr);

        byte dlc = byte.Parse(parts[3].Trim());

        var data = new byte[8];
        var dataStr = parts[4].Trim();
        if (!string.IsNullOrEmpty(dataStr))
        {
            var bytes = dataStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < bytes.Length && i < 8; i++)
                data[i] = byte.Parse(bytes[i], NumberStyles.HexNumber);
        }

        var timestamp = DateTime.MinValue.AddMilliseconds(timestampMs);
        return new BusFrame(bus, id, dlc, data, timestamp, false);
    }

    private static BusFrame.Bus ParseBus(string name) => name.ToUpperInvariant() switch
    {
        "CAN1" => BusFrame.Bus.CAN1,
        "CAN2" => BusFrame.Bus.CAN2,
        "LIN1" => BusFrame.Bus.LIN1,
        "LIN2" => BusFrame.Bus.LIN2,
        "LIN3" => BusFrame.Bus.LIN3,
        "LIN4" => BusFrame.Bus.LIN4,
        _ => BusFrame.Bus.Unknown
    };
}
```

- [ ] **Step 4: Run tests — verify they pass**

Run:
```bash
cd software && dotnet test CanLinConfig.Tests --filter "CsvLogImporter" -v n
```
Expected: All 5 tests pass.

- [ ] **Step 5: Commit**

```bash
git add software/CanLinConfig/Services/CsvLogImporter.cs software/CanLinConfig.Tests/CsvLogImporterTests.cs
git commit -m "feat(config-tool): add CsvLogImporter for log replay file loading"
```

---

## Task 2: LogReplayViewModel

**Files:**
- Create: `software/CanLinConfig/ViewModels/LogReplayViewModel.cs`

### Step-by-step:

- [ ] **Step 1: Create LogReplayViewModel**

```csharp
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanLinConfig.Models;
using CanLinConfig.Services;
using Microsoft.Win32;

namespace CanLinConfig.ViewModels;

public partial class LogReplayViewModel : ObservableObject
{
    private BusDataService? _busDataService;
    private List<BusFrame> _frames = new();
    private DispatcherTimer? _timer;
    private int _currentIndex;
    private DateTime _replayBaseTime;

    // Timestamps from the loaded file (ms offsets)
    private List<double> _timestampsMs = new();

    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private int _totalFrames;
    [ObservableProperty] private int _currentFrame;
    [ObservableProperty] private double _progress; // 0-100
    [ObservableProperty] private string _statusText = "No file loaded";
    [ObservableProperty] private int _selectedSpeedIndex = 0; // 0=1x

    public string[] SpeedNames { get; } = ["1x", "2x", "5x", "10x"];
    private static readonly double[] SpeedMultipliers = [1.0, 2.0, 5.0, 10.0];

    public void SetBusDataService(BusDataService? service)
    {
        _busDataService = service;
    }

    [RelayCommand]
    private void LoadFile()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "CSV Log Files (*.csv)|*.csv|All Files (*.*)|*.*",
            Title = "Open Log File"
        };

        if (dlg.ShowDialog() != true) return;

        try
        {
            using var stream = File.OpenRead(dlg.FileName);
            _frames = CsvLogImporter.ImportFromStream(stream);

            if (_frames.Count == 0)
            {
                StatusText = "No frames found in file";
                IsLoaded = false;
                return;
            }

            // Extract timestamp offsets in ms
            var baseTime = _frames[0].Timestamp;
            _timestampsMs = _frames.Select(f => (f.Timestamp - baseTime).TotalMilliseconds).ToList();

            TotalFrames = _frames.Count;
            _currentIndex = 0;
            CurrentFrame = 0;
            Progress = 0;
            IsLoaded = true;
            IsPlaying = false;
            StatusText = $"Loaded {_frames.Count} frames from {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            StatusText = $"Error loading file: {ex.Message}";
            IsLoaded = false;
        }

        PlayCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        StopReplayCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private void Play()
    {
        if (_frames.Count == 0 || _busDataService == null) return;

        _replayBaseTime = DateTime.Now;
        IsPlaying = true;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1)
        };
        _timer.Tick += OnTimerTick;
        _timer.Start();

        StatusText = "Playing...";
        PlayCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        StopReplayCommand.NotifyCanExecuteChanged();
    }

    private bool CanPlay() => IsLoaded && !IsPlaying;

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause()
    {
        _timer?.Stop();
        _timer = null;
        IsPlaying = false;
        StatusText = $"Paused at frame {_currentIndex}/{TotalFrames}";
        PlayCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
    }

    private bool CanPause() => IsPlaying;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void StopReplay()
    {
        _timer?.Stop();
        _timer = null;
        IsPlaying = false;
        _currentIndex = 0;
        CurrentFrame = 0;
        Progress = 0;
        StatusText = $"Stopped — {TotalFrames} frames loaded";
        PlayCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        StopReplayCommand.NotifyCanExecuteChanged();
    }

    private bool CanStop() => IsLoaded;

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_currentIndex >= _frames.Count)
        {
            // Replay complete
            _timer?.Stop();
            _timer = null;
            IsPlaying = false;
            StatusText = $"Replay complete — {TotalFrames} frames";
            PlayCommand.NotifyCanExecuteChanged();
            PauseCommand.NotifyCanExecuteChanged();
            return;
        }

        double speed = SpeedMultipliers[SelectedSpeedIndex];
        double elapsedMs = (DateTime.Now - _replayBaseTime).TotalMilliseconds * speed;
        double offsetMs = _timestampsMs[_currentIndex] - _timestampsMs[_currentIndex > 0 ? 0 : 0];

        // Adjust: on resume after pause, recalculate base time
        // Actually simpler: use the first frame's offset as zero reference
        double startOffsetMs = _currentIndex > 0 ? _timestampsMs[_currentIndex] : 0;

        // Feed all frames whose timestamp has been reached
        while (_currentIndex < _frames.Count)
        {
            double frameOffsetMs = _timestampsMs[_currentIndex] - _timestampsMs[0];
            double targetMs = frameOffsetMs / speed;
            double wallMs = (DateTime.Now - _replayBaseTime).TotalMilliseconds;

            if (wallMs < targetMs) break;

            _busDataService!.OnFrame(_frames[_currentIndex]);
            _currentIndex++;
            CurrentFrame = _currentIndex;
            Progress = TotalFrames > 0 ? (double)_currentIndex / TotalFrames * 100 : 0;
        }
    }
}
```

- [ ] **Step 2: Build the solution**

Run:
```bash
cd software && dotnet build CanLinConfig.sln
```

- [ ] **Step 3: Commit**

```bash
git add software/CanLinConfig/ViewModels/LogReplayViewModel.cs
git commit -m "feat(config-tool): add LogReplayViewModel — playback with speed control"
```

---

## Task 3: LogReplayPanel and DataLogger Wiring

**Files:**
- Create: `software/CanLinConfig/Views/LogReplayPanel.xaml`
- Create: `software/CanLinConfig/Views/LogReplayPanel.xaml.cs`
- Modify: `software/CanLinConfig/ViewModels/DataLoggerViewModel.cs`
- Modify: `software/CanLinConfig/Views/DataLoggerView.xaml`

### Step-by-step:

- [ ] **Step 1: Create LogReplayPanel.xaml**

```xml
<UserControl x:Class="CanLinConfig.Views.LogReplayPanel"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <GroupBox Header="Log Replay" Margin="5">
        <StackPanel>
            <!-- Status and progress -->
            <TextBlock Text="{Binding StatusText}" Margin="0,0,0,5"/>
            <ProgressBar Height="16" Minimum="0" Maximum="100"
                         Value="{Binding Progress, Mode=OneWay}" Margin="0,0,0,5"/>

            <!-- Frame counter -->
            <StackPanel Orientation="Horizontal" Margin="0,0,0,8"
                        Visibility="{Binding IsLoaded, Converter={StaticResource BoolToVisConverter}}">
                <TextBlock Text="Frame:" VerticalAlignment="Center" Margin="0,0,5,0"/>
                <TextBlock Text="{Binding CurrentFrame}" VerticalAlignment="Center" Margin="0,0,5,0"/>
                <TextBlock Text="/" VerticalAlignment="Center" Margin="0,0,5,0"/>
                <TextBlock Text="{Binding TotalFrames}" VerticalAlignment="Center" Margin="0,0,15,0"/>
                <TextBlock Text="Speed:" VerticalAlignment="Center" Margin="0,0,5,0"/>
                <ComboBox ItemsSource="{Binding SpeedNames}"
                          SelectedIndex="{Binding SelectedSpeedIndex}"
                          Width="60" VerticalAlignment="Center"/>
            </StackPanel>

            <!-- Controls -->
            <StackPanel Orientation="Horizontal">
                <Button Content="Load CSV..." Command="{Binding LoadFileCommand}" Width="80" Margin="0,0,5,0"/>
                <Button Content="Play" Command="{Binding PlayCommand}" Width="60" Margin="0,0,5,0"/>
                <Button Content="Pause" Command="{Binding PauseCommand}" Width="60" Margin="0,0,5,0"/>
                <Button Content="Stop" Command="{Binding StopReplayCommand}" Width="60"/>
            </StackPanel>
        </StackPanel>
    </GroupBox>
</UserControl>
```

- [ ] **Step 2: Create LogReplayPanel.xaml.cs**

```csharp
namespace CanLinConfig.Views;

public partial class LogReplayPanel
{
    public LogReplayPanel()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 3: Update DataLoggerViewModel**

Add `LogReplay` property and wire it:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CanLinConfig.Protocol;
using CanLinConfig.Services;

namespace CanLinConfig.ViewModels;

public partial class DataLoggerViewModel : ObservableObject
{
    public LogControlViewModel LogControl { get; }
    public LogDownloadViewModel LogDownload { get; }
    public LogReplayViewModel LogReplay { get; }

    public DataLoggerViewModel()
    {
        LogControl = new LogControlViewModel();
        LogDownload = new LogDownloadViewModel();
        LogReplay = new LogReplayViewModel();
    }

    public void SetProtocol(ConfigProtocol? protocol, BusDataService? busDataService)
    {
        LogControl.SetProtocol(protocol);
        LogDownload.SetProtocol(protocol, busDataService);
        LogReplay.SetBusDataService(busDataService);
    }
}
```

- [ ] **Step 4: Update DataLoggerView.xaml**

Add a third row for LogReplayPanel:

```xml
<UserControl x:Class="CanLinConfig.Views.DataLoggerView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:local="clr-namespace:CanLinConfig.Views">
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <local:LogControlPanel Grid.Row="0" DataContext="{Binding LogControl}"/>
        <local:LogDownloadPanel Grid.Row="1" DataContext="{Binding LogDownload}"/>
        <local:LogReplayPanel Grid.Row="2" DataContext="{Binding LogReplay}"/>
    </Grid>
</UserControl>
```

- [ ] **Step 5: Build the solution**

Run:
```bash
cd software && dotnet build CanLinConfig.sln
```

- [ ] **Step 6: Run all tests**

Run:
```bash
cd software && dotnet test CanLinConfig.Tests -v n
```

- [ ] **Step 7: Commit**

```bash
git add software/CanLinConfig/Views/LogReplayPanel.xaml software/CanLinConfig/Views/LogReplayPanel.xaml.cs software/CanLinConfig/ViewModels/DataLoggerViewModel.cs software/CanLinConfig/Views/DataLoggerView.xaml
git commit -m "feat(config-tool): add LogReplayPanel with playback controls in Data Logger tab"
```

---

## Task 4: Update Master Plan

**Files:**
- Modify: `docs/bus-monitor-logger-master-plan.md`

### Step-by-step:

- [ ] **Step 1: Update Plan 5D status to COMPLETE**

- [ ] **Step 2: Verify builds and tests pass**

- [ ] **Step 3: Commit**

```bash
git add docs/bus-monitor-logger-master-plan.md
git commit -m "docs: update master plan — Plan 5D log replay complete"
```

---

## Testing Notes

### Config tool (can test now)
- CsvLogImporter: 5 unit tests (standard parsing, LIN buses, empty file, malformed lines, stream loading)
- Replay UI manually testable: load CSV, play, pause, stop, speed change
- All existing tests still pass

### Integration (manual)
- Export frames from Bus Monitor as CSV → load in Log Replay → play → verify trace/signals/graph show data
- Speed multiplier verification (2x should play twice as fast)
