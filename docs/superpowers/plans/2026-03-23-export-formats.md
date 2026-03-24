# Export Formats — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Export captured bus frames from the Bus Monitor as CSV, Vector ASC, and Vector BLF files for analysis in external tools (Excel, MATLAB, CANalyzer, PCAN-View).

**Architecture:** Three exporter classes implementing a common `IFrameExporter` interface. Each consumes `IReadOnlyList<BusFrame>` and writes to a file stream. An export dialog in the Bus Monitor tab lets the user choose format and options. The DatabaseManager is optionally passed for signal-decoded CSV columns.

**Tech Stack:** .NET 8, System.IO.Compression (zlib for BLF), System.Text

**Spec:** `docs/superpowers/specs/2026-03-23-bus-monitor-logger-design.md` (Section 7)

**Important:** Do NOT commit specs or plans. Only commit actual code changes, and only after the feature is completed and tested.

---

## File Map

### New Files

| File | Responsibility |
|------|---------------|
| `software/CanLinConfig/Services/Export/IFrameExporter.cs` | Common exporter interface |
| `software/CanLinConfig/Services/Export/CsvExporter.cs` | CSV export with optional signal columns |
| `software/CanLinConfig/Services/Export/AscExporter.cs` | Vector ASC text format export |
| `software/CanLinConfig/Services/Export/BlfExporter.cs` | Vector BLF binary format export |
| `software/CanLinConfig.Tests/CsvExporterTests.cs` | CSV export tests |
| `software/CanLinConfig.Tests/AscExporterTests.cs` | ASC export tests |
| `software/CanLinConfig.Tests/BlfExporterTests.cs` | BLF export tests |

### Modified Files

| File | Changes |
|------|---------|
| `software/CanLinConfig/ViewModels/BusMonitorViewModel.cs` | Add ExportFrames command |
| `software/CanLinConfig/Views/BusMonitorView.xaml` | Add Export button to trace panel toolbar |

---

## Task 1: IFrameExporter Interface + CsvExporter (TDD)

**Files:**
- Create: `software/CanLinConfig/Services/Export/IFrameExporter.cs`
- Create: `software/CanLinConfig/Services/Export/CsvExporter.cs`
- Create: `software/CanLinConfig.Tests/CsvExporterTests.cs`

- [ ] **Step 1: Create interface**

```csharp
// software/CanLinConfig/Services/Export/IFrameExporter.cs
using CanLinConfig.Models;
using CanLinConfig.Services;

namespace CanLinConfig.Services.Export;

public interface IFrameExporter
{
    string FileExtension { get; }
    string FileFilter { get; }
    void Export(Stream stream, IReadOnlyList<BusFrame> frames, DatabaseManager? dbManager = null);
}
```

- [ ] **Step 2: Write failing CSV tests**

```csharp
// software/CanLinConfig.Tests/CsvExporterTests.cs
using CanLinConfig.Models;
using CanLinConfig.Services;
using CanLinConfig.Services.Export;

namespace CanLinConfig.Tests;

public class CsvExporterTests
{
    private static readonly DateTime T0 = new(2026, 3, 23, 10, 0, 0, DateTimeKind.Utc);

    private static BusFrame MakeFrame(uint id, byte[] data, BusFrame.Bus bus = BusFrame.Bus.CAN1, DateTime? ts = null)
        => new(bus, id, (byte)data.Length, data, ts ?? T0);

    [Fact]
    public void Export_writes_header_and_rows()
    {
        var exporter = new CsvExporter();
        var frames = new List<BusFrame>
        {
            MakeFrame(0x100, [0xAB, 0xCD]),
            MakeFrame(0x200, [0x01, 0x02, 0x03]),
        };

        using var ms = new MemoryStream();
        exporter.Export(ms, frames);
        var csv = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length); // header + 2 rows
        Assert.StartsWith("timestamp_ms", lines[0]);
        Assert.Contains("0x100", lines[1]);
        Assert.Contains("AB CD", lines[1]);
        Assert.Contains("0x200", lines[2]);
    }

    [Fact]
    public void Export_with_database_adds_signal_columns()
    {
        var dbMgr = new DatabaseManager();
        var testDbc = Path.Combine(AppContext.BaseDirectory, "TestData", "test.dbc");
        dbMgr.AssignDatabase(BusFrame.Bus.CAN1, testDbc);

        var exporter = new CsvExporter();
        // EngineData (ID=256): RPM=1000*0.25=250, Temp=200-40=160, EngineOn=1
        var frames = new List<BusFrame>
        {
            MakeFrame(256, [0xE8, 0x03, 200, 0x01, 0, 0, 0, 0]),
        };

        using var ms = new MemoryStream();
        exporter.Export(ms, frames, dbMgr);
        var csv = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Header should include signal names
        Assert.Contains("EngineRPM", lines[0]);
        Assert.Contains("CoolantTemp", lines[0]);
        // Data row should include physical values
        Assert.Contains("250", lines[1]);
        Assert.Contains("160", lines[1]);
    }

    [Fact]
    public void Export_empty_frames_writes_header_only()
    {
        var exporter = new CsvExporter();
        using var ms = new MemoryStream();
        exporter.Export(ms, []);
        var csv = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines); // header only
    }

    [Fact]
    public void FileExtension_is_csv()
    {
        Assert.Equal(".csv", new CsvExporter().FileExtension);
    }
}
```

- [ ] **Step 3: Implement CsvExporter**

```csharp
// software/CanLinConfig/Services/Export/CsvExporter.cs
using System.Globalization;
using System.IO;
using System.Text;
using CanLinConfig.Models;

namespace CanLinConfig.Services.Export;

public class CsvExporter : IFrameExporter
{
    public string FileExtension => ".csv";
    public string FileFilter => "CSV Files (*.csv)|*.csv";

    public void Export(Stream stream, IReadOnlyList<BusFrame> frames, DatabaseManager? dbManager = null)
    {
        using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true);

        // Collect all unique signal names if DB provided
        var signalNames = new List<string>();
        if (dbManager != null)
        {
            var seen = new HashSet<string>();
            foreach (var f in frames)
            {
                var signals = dbManager.GetSignals(f.SourceBus, f.Id);
                foreach (var s in signals)
                    if (seen.Add(s.Name))
                        signalNames.Add(s.Name);
            }
        }

        // Header
        var header = "timestamp_ms,bus,id,dlc,data,message";
        if (signalNames.Count > 0)
            header += "," + string.Join(",", signalNames);
        writer.WriteLine(header);

        // Rows
        foreach (var f in frames)
        {
            var dataHex = string.Join(" ", f.Data.Take(f.Dlc).Select(b => b.ToString("X2")));
            var msgName = dbManager?.GetMessageName(f.SourceBus, f.Id) ?? "";
            var line = string.Format(CultureInfo.InvariantCulture,
                "{0:F3},{1},0x{2:X3},{3},{4},{5}",
                (f.Timestamp - DateTime.UnixEpoch).TotalMilliseconds,
                f.BusName, f.Id, f.Dlc, dataHex, msgName);

            if (signalNames.Count > 0)
            {
                var decoded = dbManager!.DecodeFrame(f);
                var valueMap = decoded.ToDictionary(v => v.Name, v => v.PhysicalValue);
                foreach (var name in signalNames)
                {
                    line += ",";
                    if (valueMap.TryGetValue(name, out var val))
                        line += val.ToString(CultureInfo.InvariantCulture);
                }
            }

            writer.WriteLine(line);
        }
    }
}
```

- [ ] **Step 4: Run tests**

```bash
cd software && dotnet test CanLinConfig.Tests --filter "CsvExporterTests" -v n
```

Expected: 4 tests PASS.

- [ ] **Step 5: Commit**

```
feat: add CsvExporter for bus frame export
```

---

## Task 2: AscExporter (TDD)

**Files:**
- Create: `software/CanLinConfig/Services/Export/AscExporter.cs`
- Create: `software/CanLinConfig.Tests/AscExporterTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// software/CanLinConfig.Tests/AscExporterTests.cs
using CanLinConfig.Models;
using CanLinConfig.Services.Export;

namespace CanLinConfig.Tests;

public class AscExporterTests
{
    private static readonly DateTime T0 = new(2026, 3, 23, 10, 0, 0, 0, DateTimeKind.Utc);

    private static BusFrame MakeFrame(uint id, byte[] data, BusFrame.Bus bus = BusFrame.Bus.CAN1, int offsetMs = 0)
        => new(bus, id, (byte)data.Length, data, T0.AddMilliseconds(offsetMs));

    [Fact]
    public void Export_writes_asc_header()
    {
        var exporter = new AscExporter();
        var frames = new List<BusFrame> { MakeFrame(0x100, [0xAB]) };

        using var ms = new MemoryStream();
        exporter.Export(ms, frames);
        var text = System.Text.Encoding.UTF8.GetString(ms.ToArray());

        Assert.StartsWith("date", text);
        Assert.Contains("base hex", text);
        Assert.Contains("timestamps absolute", text);
    }

    [Fact]
    public void Export_writes_can_frame_line()
    {
        var exporter = new AscExporter();
        var frames = new List<BusFrame>
        {
            MakeFrame(0x123, [0x01, 0x02, 0x03], offsetMs: 1234),
        };

        using var ms = new MemoryStream();
        exporter.Export(ms, frames);
        var text = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Find the data line (after header lines)
        var dataLine = lines.Last();
        Assert.Contains("1", dataLine); // channel 1 for CAN1
        Assert.Contains("123", dataLine); // ID
        Assert.Contains("Rx", dataLine);
        Assert.Contains("d", dataLine); // data frame marker
        Assert.Contains("3", dataLine); // DLC
        Assert.Contains("01", dataLine);
        Assert.Contains("02", dataLine);
        Assert.Contains("03", dataLine);
    }

    [Fact]
    public void Export_maps_bus_to_channel()
    {
        var exporter = new AscExporter();
        var frames = new List<BusFrame>
        {
            MakeFrame(0x100, [0x01], BusFrame.Bus.CAN1),
            MakeFrame(0x200, [0x02], BusFrame.Bus.CAN2),
            MakeFrame(0x33, [0x03], BusFrame.Bus.LIN1),
        };

        using var ms = new MemoryStream();
        exporter.Export(ms, frames);
        var text = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // CAN1→channel 1, CAN2→channel 2, LIN1→channel 3
        var dataLines = lines.Where(l => !l.StartsWith("date") && !l.StartsWith("base") && !l.StartsWith("no") && !l.StartsWith("timestamps")).ToArray();
        Assert.Equal(3, dataLines.Length);
    }

    [Fact]
    public void FileExtension_is_asc()
    {
        Assert.Equal(".asc", new AscExporter().FileExtension);
    }
}
```

- [ ] **Step 2: Implement AscExporter**

```csharp
// software/CanLinConfig/Services/Export/AscExporter.cs
using System.Globalization;
using System.IO;
using System.Text;
using CanLinConfig.Models;

namespace CanLinConfig.Services.Export;

/// <summary>
/// Vector ASC text format exporter.
/// Compatible with CANalyzer, CANoe, PCAN-View, and other Vector tools.
/// </summary>
public class AscExporter : IFrameExporter
{
    public string FileExtension => ".asc";
    public string FileFilter => "ASC Files (*.asc)|*.asc";

    public void Export(Stream stream, IReadOnlyList<BusFrame> frames, DatabaseManager? dbManager = null)
    {
        using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true);

        // ASC header
        var startTime = frames.Count > 0 ? frames[0].Timestamp : DateTime.Now;
        writer.WriteLine($"date {startTime:ddd MMM dd hh:mm:ss tt yyyy}");
        writer.WriteLine("base hex  timestamps absolute");
        writer.WriteLine("no internal events logged");

        // Frames
        foreach (var f in frames)
        {
            var relTime = (f.Timestamp - startTime).TotalSeconds;
            var channel = BusToChannel(f.SourceBus);
            var dataBytes = string.Join(" ", f.Data.Take(f.Dlc).Select(b => b.ToString("x2")));

            // ASC format: <timestamp> <channel> <id>x Rx d <dlc> <data bytes>
            writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "   {0:F6} {1}  {2:X}x             Rx   d {3} {4}",
                relTime, channel, f.Id, f.Dlc, dataBytes));
        }
    }

    private static int BusToChannel(BusFrame.Bus bus) => bus switch
    {
        BusFrame.Bus.CAN1 => 1,
        BusFrame.Bus.CAN2 => 2,
        BusFrame.Bus.LIN1 => 3,
        BusFrame.Bus.LIN2 => 4,
        BusFrame.Bus.LIN3 => 5,
        BusFrame.Bus.LIN4 => 6,
        _ => 1
    };
}
```

- [ ] **Step 3: Run tests**

```bash
cd software && dotnet test CanLinConfig.Tests --filter "AscExporterTests" -v n
```

Expected: 4 tests PASS.

- [ ] **Step 4: Commit**

```
feat: add AscExporter for Vector ASC format export
```

---

## Task 3: BlfExporter (TDD)

BLF (Binary Logging Format) is a compressed binary format used by Vector tools. No .NET library exists — implementing from the public format specification.

**Files:**
- Create: `software/CanLinConfig/Services/Export/BlfExporter.cs`
- Create: `software/CanLinConfig.Tests/BlfExporterTests.cs`

**Reference:** [python-can BLF implementation](https://python-can.readthedocs.io/en/stable/_modules/can/io/blf.html), [Vector BLF C++ library](https://github.com/Technica-Engineering/vector_blf)

- [ ] **Step 1: Write failing tests**

```csharp
// software/CanLinConfig.Tests/BlfExporterTests.cs
using System.IO.Compression;
using CanLinConfig.Models;
using CanLinConfig.Services.Export;

namespace CanLinConfig.Tests;

public class BlfExporterTests
{
    private static readonly DateTime T0 = new(2026, 3, 23, 10, 0, 0, 0, DateTimeKind.Utc);

    private static BusFrame MakeFrame(uint id, byte[] data, BusFrame.Bus bus = BusFrame.Bus.CAN1, int offsetMs = 0)
        => new(bus, id, (byte)data.Length, data, T0.AddMilliseconds(offsetMs));

    [Fact]
    public void Export_writes_valid_blf_signature()
    {
        var exporter = new BlfExporter();
        var frames = new List<BusFrame> { MakeFrame(0x100, [0xAB]) };

        using var ms = new MemoryStream();
        exporter.Export(ms, frames);

        var bytes = ms.ToArray();
        // BLF file signature: "BLF0400\0" at offset 0 (7 bytes + null)
        Assert.True(bytes.Length > 8);
        var sig = System.Text.Encoding.ASCII.GetString(bytes, 0, 7);
        Assert.Equal("BLF0400", sig);
    }

    [Fact]
    public void Export_creates_nonzero_file()
    {
        var exporter = new BlfExporter();
        var frames = new List<BusFrame>
        {
            MakeFrame(0x100, [0x01, 0x02, 0x03]),
            MakeFrame(0x200, [0x04, 0x05], offsetMs: 100),
        };

        using var ms = new MemoryStream();
        exporter.Export(ms, frames);

        Assert.True(ms.Length > 144); // at least file header + some data
    }

    [Fact]
    public void Export_multiple_frames_increases_size()
    {
        var exporter = new BlfExporter();

        using var ms1 = new MemoryStream();
        exporter.Export(ms1, new List<BusFrame> { MakeFrame(0x100, [0x01]) });

        using var ms10 = new MemoryStream();
        var frames10 = Enumerable.Range(0, 10)
            .Select(i => MakeFrame(0x100, [0x01], offsetMs: i * 10))
            .ToList();
        exporter.Export(ms10, frames10);

        Assert.True(ms10.Length > ms1.Length);
    }

    [Fact]
    public void FileExtension_is_blf()
    {
        Assert.Equal(".blf", new BlfExporter().FileExtension);
    }
}
```

- [ ] **Step 2: Implement BlfExporter**

The BLF format structure:
1. **File header** (144 bytes): signature, stats, measurement start/end time
2. **Log containers**: each wraps one or more objects, optionally zlib-compressed
3. **CAN_MESSAGE objects** (object type 1): timestamp, channel, ID, DLC, data

```csharp
// software/CanLinConfig/Services/Export/BlfExporter.cs
using System.IO;
using System.IO.Compression;
using System.Text;
using CanLinConfig.Models;

namespace CanLinConfig.Services.Export;

/// <summary>
/// Vector BLF (Binary Logging Format) exporter.
/// Produces files compatible with CANalyzer, CANoe, and PCAN-View.
/// Implements BLF version 4.0.0 with uncompressed log containers.
/// </summary>
public class BlfExporter : IFrameExporter
{
    public string FileExtension => ".blf";
    public string FileFilter => "BLF Files (*.blf)|*.blf";

    // BLF constants
    private const string FileSignature = "BLF0400";
    private const uint HeaderSize = 144;
    private const uint ObjectSignature = 0x4F4A4C42; // "LOBJ"
    private const uint ObjectHeaderSize = 16;
    private const uint CanMsgObjectType = 1;
    private const uint CanMsgObjectSize = 40; // object header(16) + CAN msg data(24)
    private const uint ContainerObjectType = 10;

    // Windows FILETIME epoch: January 1, 1601
    private static readonly DateTime FileTimeEpoch = new(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public void Export(Stream stream, IReadOnlyList<BusFrame> frames, DatabaseManager? dbManager = null)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        var startTime = frames.Count > 0 ? frames[0].Timestamp : DateTime.UtcNow;
        var endTime = frames.Count > 0 ? frames[^1].Timestamp : startTime;

        // Write file header (144 bytes)
        WriteFileHeader(writer, startTime, endTime, (uint)frames.Count);

        // Write each frame as an uncompressed log container with a CAN_MESSAGE object
        foreach (var frame in frames)
        {
            WriteCanMessageContainer(writer, frame, startTime);
        }

        // Update file size in header
        var fileSize = stream.Position;
        stream.Seek(8, SeekOrigin.Begin);
        writer.Write((uint)fileSize); // statistics_size at offset 8
    }

    private static void WriteFileHeader(BinaryWriter w, DateTime start, DateTime end, uint objectCount)
    {
        var pos = w.BaseStream.Position;

        // Signature (7 bytes + null)
        w.Write(Encoding.ASCII.GetBytes(FileSignature));
        w.Write((byte)0);

        // Statistics size (total file size — filled later)
        w.Write((uint)0); // offset 8, placeholder

        // API version
        w.Write((uint)0x0403); // offset 12

        // Platform (Windows)
        w.Write((uint)1); // offset 16

        // Creation flags
        w.Write((uint)0); // offset 20

        // Measurement start time (SYSTEMTIME - 16 bytes)
        WriteSystemTime(w, start);

        // Last object timestamp (SYSTEMTIME - 16 bytes)
        WriteSystemTime(w, end);

        // Object count
        w.Write(objectCount); // offset 56

        // Object read (0 for new files)
        w.Write((uint)0); // offset 60

        // App-specific start/end timestamps (nanoseconds since measurement start)
        w.Write((long)0); // offset 64, start ns
        var durationNs = (long)((end - start).TotalSeconds * 1_000_000_000);
        w.Write(durationNs); // offset 72, end ns

        // Pad to 144 bytes
        var written = w.BaseStream.Position - pos;
        var padding = (int)(HeaderSize - written);
        if (padding > 0)
            w.Write(new byte[padding]);
    }

    private static void WriteCanMessageContainer(BinaryWriter w, BusFrame frame, DateTime startTime)
    {
        // Build CAN_MESSAGE object in memory
        using var objMs = new MemoryStream();
        using var objW = new BinaryWriter(objMs);

        // Object header (16 bytes)
        objW.Write(ObjectSignature);        // "LOBJ"
        objW.Write((ushort)ObjectHeaderSize); // header size
        objW.Write((ushort)1);               // header version
        objW.Write(CanMsgObjectSize);        // object size (header + data)
        objW.Write(CanMsgObjectType);        // object type = CAN_MESSAGE

        // Timestamp (nanoseconds since measurement start)
        var timestampNs = (long)((frame.Timestamp - startTime).TotalSeconds * 1_000_000_000);
        objW.Write(timestampNs);

        // CAN_MESSAGE specific data (24 bytes)
        var channel = BusToChannel(frame.SourceBus);
        objW.Write((ushort)channel);     // channel
        objW.Write((byte)frame.Dlc);     // DLC
        objW.Write((byte)0);             // flags
        objW.Write(frame.Id);            // arbitration ID

        // Data (8 bytes, zero-padded)
        var data = new byte[8];
        Array.Copy(frame.Data, data, Math.Min(frame.Dlc, 8));
        objW.Write(data);

        var objectData = objMs.ToArray();

        // Write as uncompressed container
        // Container header: signature(4) + headerSize(2) + headerVersion(2) + objectSize(4) + objectType(4)
        // Container body: compressionMethod(2) + pad(6) + uncompressedSize(4) + pad(4) + data
        w.Write(ObjectSignature);          // "LOBJ"
        w.Write((ushort)16);               // container header size
        w.Write((ushort)1);                // header version
        var containerSize = 16u + 16u + (uint)objectData.Length; // header + container fields + data
        w.Write(containerSize);            // total object size
        w.Write(ContainerObjectType);      // type = container

        // Container-specific fields (16 bytes before data)
        w.Write((ushort)0);                // compression method: 0=uncompressed
        w.Write(new byte[6]);              // padding
        w.Write((uint)objectData.Length);   // uncompressed size
        w.Write(new byte[4]);              // padding

        // Object data
        w.Write(objectData);
    }

    private static void WriteSystemTime(BinaryWriter w, DateTime dt)
    {
        // SYSTEMTIME structure: 8x uint16
        w.Write((ushort)dt.Year);
        w.Write((ushort)dt.Month);
        w.Write((ushort)dt.DayOfWeek);
        w.Write((ushort)dt.Day);
        w.Write((ushort)dt.Hour);
        w.Write((ushort)dt.Minute);
        w.Write((ushort)dt.Second);
        w.Write((ushort)dt.Millisecond);
    }

    private static ushort BusToChannel(BusFrame.Bus bus) => bus switch
    {
        BusFrame.Bus.CAN1 => 1,
        BusFrame.Bus.CAN2 => 2,
        BusFrame.Bus.LIN1 => 3,
        BusFrame.Bus.LIN2 => 4,
        BusFrame.Bus.LIN3 => 5,
        BusFrame.Bus.LIN4 => 6,
        _ => 1
    };
}
```

**Note:** This is a basic BLF writer with uncompressed containers and CAN_MESSAGE objects only. It should open in PCAN-View and CANalyzer. Advanced features (zlib compression, LIN_MESSAGE objects, extended CAN IDs) can be added later. The format should be validated against PCAN-View or CANalyzer when hardware is available.

- [ ] **Step 3: Run tests**

```bash
cd software && dotnet test CanLinConfig.Tests --filter "BlfExporterTests" -v n
```

Expected: 4 tests PASS.

- [ ] **Step 4: Commit**

```
feat: add AscExporter and BlfExporter for Vector format export
```

---

## Task 4: Export UI + Wiring

**Files:**
- Modify: `software/CanLinConfig/ViewModels/BusMonitorViewModel.cs`
- Modify: `software/CanLinConfig/Views/BusMonitorView.xaml`

- [ ] **Step 1: Add export command to BusMonitorViewModel**

Add to `BusMonitorViewModel.cs`:

```csharp
[RelayCommand]
private void ExportFrames()
{
    var frames = _busDataService.FrameHistory;
    if (frames.Count == 0)
    {
        System.Windows.MessageBox.Show("No frames to export.", "Export",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        return;
    }

    var dlg = new Microsoft.Win32.SaveFileDialog
    {
        Filter = "CSV Files (*.csv)|*.csv|ASC Files (*.asc)|*.asc|BLF Files (*.blf)|*.blf|All Files (*.*)|*.*",
        Title = "Export Frames",
        FileName = $"capture_{DateTime.Now:yyyyMMdd_HHmmss}"
    };
    if (dlg.ShowDialog() != true) return;

    IFrameExporter exporter = System.IO.Path.GetExtension(dlg.FileName).ToLowerInvariant() switch
    {
        ".asc" => new AscExporter(),
        ".blf" => new BlfExporter(),
        _ => new CsvExporter()
    };

    try
    {
        using var fs = File.Create(dlg.FileName);
        exporter.Export(fs, frames, _busDataService.DatabaseManager);
    }
    catch (Exception ex)
    {
        System.Windows.MessageBox.Show($"Export failed: {ex.Message}", "Export Error",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
    }
}
```

Add the required using:
```csharp
using CanLinConfig.Services.Export;
```

- [ ] **Step 2: Add Export button to TracePanel toolbar in BusMonitorView.xaml**

Read `software/CanLinConfig/Views/BusMonitorView.xaml`. The TracePanel is a `local:TracePanel` UserControl. The export button should go in the BusMonitorView's own toolbar area, not inside the TracePanel. Add it to the database bar or add a separate toolbar.

Simplest approach — add an Export button at the end of the first row in the database bar:

```xml
<Button Content="Export" Command="{Binding ExportFramesCommand}" Margin="15,0,0,0" Padding="10,2"/>
```

- [ ] **Step 3: Verify build and all tests**

```bash
cd software && dotnet build CanLinConfig.sln && dotnet test CanLinConfig.Tests -v quiet
```

Expected: Build succeeds, all tests pass.

- [ ] **Step 4: Commit**

```
feat: add export button to Bus Monitor with CSV/ASC/BLF format selection
```

---

## Task 5: Integration Test + Test Guide

**Files:**
- Create: `software/CanLinConfig.Tests/ExportIntegrationTests.cs`
- Modify: `software/TEST_GUIDE.md`

- [ ] **Step 1: Write integration test**

```csharp
// software/CanLinConfig.Tests/ExportIntegrationTests.cs
using CanLinConfig.Models;
using CanLinConfig.Services;
using CanLinConfig.Services.Export;

namespace CanLinConfig.Tests;

public class ExportIntegrationTests
{
    private static readonly DateTime T0 = new(2026, 3, 23, 10, 0, 0, 0, DateTimeKind.Utc);

    private static List<BusFrame> MakeTestFrames()
    {
        return
        [
            new(BusFrame.Bus.CAN1, 0x100, 3, [0x01, 0x02, 0x03, 0, 0, 0, 0, 0], T0),
            new(BusFrame.Bus.CAN2, 0x200, 2, [0xAB, 0xCD, 0, 0, 0, 0, 0, 0], T0.AddMilliseconds(100)),
            new(BusFrame.Bus.LIN1, 33, 4, [25, 200, 0, 0, 0, 0, 0, 0], T0.AddMilliseconds(200)),
        ];
    }

    [Theory]
    [InlineData(typeof(CsvExporter))]
    [InlineData(typeof(AscExporter))]
    [InlineData(typeof(BlfExporter))]
    public void AllExporters_handle_mixed_bus_frames(Type exporterType)
    {
        var exporter = (IFrameExporter)Activator.CreateInstance(exporterType)!;
        var frames = MakeTestFrames();

        using var ms = new MemoryStream();
        exporter.Export(ms, frames);

        Assert.True(ms.Length > 0);
    }

    [Fact]
    public void Csv_with_ldf_signals_decodes_lin_frames()
    {
        var dbMgr = new DatabaseManager();
        var testLdf = Path.Combine(AppContext.BaseDirectory, "TestData", "test.ldf");
        dbMgr.AssignDatabase(BusFrame.Bus.LIN1, testLdf);

        var exporter = new CsvExporter();
        var frames = new List<BusFrame>
        {
            new(BusFrame.Bus.LIN1, 33, 4, [25, 200, 0, 0, 0, 0, 0, 0], T0),
        };

        using var ms = new MemoryStream();
        exporter.Export(ms, frames, dbMgr);
        var csv = System.Text.Encoding.UTF8.GetString(ms.ToArray());

        Assert.Contains("MotorSpeed", csv);
        Assert.Contains("2500", csv); // 25 * 100
        Assert.Contains("160", csv);  // 200 - 40
    }
}
```

- [ ] **Step 2: Add export tests section to TEST_GUIDE.md**

Add before "Known Limitations":

```markdown
---

## Export Format Tests (Plan 6 — No Hardware Required)

### Automated Unit Tests

| Test Class | Count | What it covers |
|------------|-------|----------------|
| CsvExporterTests | 4 | Header/rows, signal columns, empty frames, file extension |
| AscExporterTests | 4 | ASC header, frame line format, bus-to-channel mapping, file extension |
| BlfExporterTests | 4 | BLF signature, file size, multi-frame scaling, file extension |
| ExportIntegrationTests | 4 | All exporters with mixed bus frames, CSV with LDF signal decode |

### Export Manual Tests

#### EXP-1: Export CSV from Bus Monitor

1. Capture some frames in the Bus Monitor (live or simulated)
2. Click "Export" button
3. Select CSV format, save
4. Open in Excel/text editor — verify header row, data rows with timestamps, bus, ID, data
5. If DBC loaded: signal columns should be present with physical values

#### EXP-2: Export ASC from Bus Monitor

1. Capture frames, click Export, select ASC format
2. Open in text editor — verify ASC header (date, base hex, timestamps absolute)
3. Verify frame lines have correct format: timestamp, channel, ID, Rx, d, DLC, data
4. If available: open in PCAN-View or CANalyzer to validate

#### EXP-3: Export BLF from Bus Monitor

1. Capture frames, click Export, select BLF format
2. Open in PCAN-View or CANalyzer — verify frames are readable
3. Verify timestamps and channel mapping are correct

#### EXP-4: Export with no frames

1. Click Export with empty trace → info dialog "No frames to export"

### Export Test Checklist

| # | Test | Hardware | Status |
|---|------|----------|--------|
| — | Unit tests (16 export-specific) | None | |
| EXP-1 | CSV export | None | |
| EXP-2 | ASC export | None | |
| EXP-3 | BLF export validation | PCAN-View/CANalyzer | |
| EXP-4 | Export with no frames | None | |
```

- [ ] **Step 3: Run all tests**

```bash
cd software && dotnet test CanLinConfig.Tests -v quiet
```

- [ ] **Step 4: Commit**

```
feat: add export integration tests and update test guide
```

---

## Summary

| Task | Component | Tests | Depends On |
|------|-----------|-------|------------|
| 1 | IFrameExporter + CsvExporter | 4 | — |
| 2 | AscExporter | 4 | 1 |
| 3 | BlfExporter | 4 | 1 |
| 4 | Export UI + wiring | 0 | 1-3 |
| 5 | Integration tests + test guide | 4 | 1-4 |

**Total: 5 tasks, 16 new tests**
