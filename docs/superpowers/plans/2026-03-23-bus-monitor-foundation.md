# Bus Monitor Foundation — Implementation Plan

> **STATUS: COMPLETE AND TESTED (2026-03-24)** — All 12 tasks implemented. 121/121 .NET unit tests pass. All manual UI tests pass. On-target tests: Phase 7 (16/16), Phase 8 (15/15). 11 bugs found and fixed during testing. Branch `feature/bus-monitor-foundation` ready for merge to master.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the config tool's live bus monitor with signal decoding, trace view, signal table, and time-series graphing — working immediately against CAN1 traffic via the existing adapter.

**Architecture:** A central `BusDataService` receives raw CAN frames (from adapter or future sources), tags them with source bus, decodes signals via `DatabaseManager` + `SignalExtractor`, and broadcasts to self-contained UI panels (TracePanel, SignalPanel, GraphPanel). Each panel is a UserControl with its own ViewModel, ready for future AvalonDock docking.

**Tech Stack:** .NET 8, WPF, MahApps.Metro, CommunityToolkit.Mvvm, ScottPlot 5.x, xUnit

**Spec:** `docs/superpowers/specs/2026-03-23-bus-monitor-logger-design.md`

**Important:** Do NOT commit specs or plans. Only commit actual code changes, and only after the feature is completed and tested.

---

## File Map

### New Files

| File | Responsibility |
|------|---------------|
| `software/CanLinConfig/Models/BusFrame.cs` | Frame model extended with SourceBus property |
| `software/CanLinConfig/Services/SignalExtractor.cs` | Bit-level signal value extraction from CAN data bytes |
| `software/CanLinConfig/Services/DatabaseManager.cs` | Per-bus DBC/LDF assignment, signal lookup cache |
| `software/CanLinConfig/Services/BusDataService.cs` | Central pipeline: tag, decode, dispatch to UI thread |
| `software/CanLinConfig/ViewModels/BusMonitorViewModel.cs` | Bus Monitor tab orchestrator |
| `software/CanLinConfig/ViewModels/TracePanelViewModel.cs` | High-performance frame trace list |
| `software/CanLinConfig/ViewModels/SignalPanelViewModel.cs` | Live signal value table |
| `software/CanLinConfig/ViewModels/GraphPanelViewModel.cs` | ScottPlot time-series signal plotting |
| `software/CanLinConfig/Views/BusMonitorView.xaml` | Bus Monitor tab layout (3 panels + database bar) |
| `software/CanLinConfig/Views/BusMonitorView.xaml.cs` | Code-behind |
| `software/CanLinConfig/Views/TracePanel.xaml` | Trace panel UserControl |
| `software/CanLinConfig/Views/TracePanel.xaml.cs` | Code-behind |
| `software/CanLinConfig/Views/SignalPanel.xaml` | Signal panel UserControl |
| `software/CanLinConfig/Views/SignalPanel.xaml.cs` | Code-behind |
| `software/CanLinConfig/Views/GraphPanel.xaml` | Graph panel UserControl |
| `software/CanLinConfig/Views/GraphPanel.xaml.cs` | Code-behind |
| `software/CanLinConfig.Tests/CanLinConfig.Tests.csproj` | xUnit test project |
| `software/CanLinConfig.Tests/SignalExtractorTests.cs` | Signal extraction unit tests |
| `software/CanLinConfig.Tests/DatabaseManagerTests.cs` | Database manager tests |
| `software/CanLinConfig.Tests/BusDataServiceTests.cs` | Pipeline integration tests |
| `software/CanLinConfig.Tests/TracePanelViewModelTests.cs` | Trace panel logic tests |

### Modified Files

| File | Changes |
|------|---------|
| `software/CanLinConfig/CanLinConfig.csproj` | Add ScottPlot.WPF NuGet |
| `software/CanLinConfig.sln` | Add test project reference |
| `software/CanLinConfig/ViewModels/MainViewModel.cs` | Add BusDataService + BusMonitorViewModel, rewire frame routing |
| `software/CanLinConfig/ViewModels/DiagnosticsViewModel.cs` | Receive frames via BusDataService instead of direct wiring |
| `software/CanLinConfig/Views/MainWindow.xaml` | Add "Bus Monitor" tab |
| `software/CanLinConfig/Protocol/ProtocolConstants.cs` | Add monitor CAN IDs (0x604, 0x605) |

---

## Task 1: Create Test Project

**Files:**
- Create: `software/CanLinConfig.Tests/CanLinConfig.Tests.csproj`
- Modify: `software/CanLinConfig.sln`

- [ ] **Step 1: Create xUnit test project**

```bash
cd software
dotnet new xunit -n CanLinConfig.Tests
dotnet sln CanLinConfig.sln add CanLinConfig.Tests/CanLinConfig.Tests.csproj
```

- [ ] **Step 2: Add project reference to main project**

```bash
cd software/CanLinConfig.Tests
dotnet add reference ../CanLinConfig/CanLinConfig.csproj
```

- [ ] **Step 3: Verify test project builds**

```bash
cd software
dotnet build CanLinConfig.sln
```

Expected: Build succeeds with 0 errors.

- [ ] **Step 4: Run placeholder test**

```bash
cd software
dotnet test CanLinConfig.Tests/CanLinConfig.Tests.csproj
```

Expected: 0 tests discovered (or 1 default test if template creates one).

---

## Task 2: BusFrame Model

**Files:**
- Create: `software/CanLinConfig/Models/BusFrame.cs`

- [ ] **Step 1: Create BusFrame model**

```csharp
// software/CanLinConfig/Models/BusFrame.cs
namespace CanLinConfig.Models;

/// <summary>
/// A CAN/LIN frame tagged with its source bus.
/// Wraps CanFrame for the BusDataService pipeline.
/// </summary>
public class BusFrame
{
    public enum Bus : byte
    {
        CAN1 = 0,
        CAN2 = 1,
        LIN1 = 2,
        LIN2 = 3,
        LIN3 = 4,
        LIN4 = 5,
        Unknown = 0xFF
    }

    public Bus SourceBus { get; }
    public uint Id { get; }
    public byte Dlc { get; }
    public byte[] Data { get; }
    public DateTime Timestamp { get; }
    public bool IsExtended { get; }

    public BusFrame(Adapters.CanFrame frame, Bus sourceBus = Bus.CAN1)
    {
        SourceBus = sourceBus;
        Id = frame.Id;
        Dlc = frame.Dlc;
        Data = frame.Data;
        Timestamp = frame.Timestamp;
        IsExtended = frame.IsExtended;
    }

    public BusFrame(Bus sourceBus, uint id, byte dlc, byte[] data, DateTime timestamp, bool isExtended = false)
    {
        SourceBus = sourceBus;
        Id = id;
        Dlc = dlc;
        Data = data;
        Timestamp = timestamp;
        IsExtended = isExtended;
    }

    public string BusName => SourceBus switch
    {
        Bus.CAN1 => "CAN1",
        Bus.CAN2 => "CAN2",
        Bus.LIN1 => "LIN1",
        Bus.LIN2 => "LIN2",
        Bus.LIN3 => "LIN3",
        Bus.LIN4 => "LIN4",
        _ => "???"
    };
}
```

- [ ] **Step 2: Verify build**

```bash
cd software && dotnet build CanLinConfig/CanLinConfig.csproj
```

Expected: Build succeeds.

---

## Task 3: SignalExtractor (TDD)

The core algorithm that extracts signal values from CAN data bytes using DBC signal definitions.

**Files:**
- Create: `software/CanLinConfig/Services/SignalExtractor.cs`
- Create: `software/CanLinConfig.Tests/SignalExtractorTests.cs`
- Create: `software/CanLinConfig/Models/SignalValue.cs`

- [ ] **Step 1: Create SignalValue record**

```csharp
// software/CanLinConfig/Models/SignalValue.cs
namespace CanLinConfig.Models;

public record SignalValue(
    string Name,
    double RawValue,
    double PhysicalValue,
    string Unit,
    byte Bus,
    uint FrameId,
    DateTime Timestamp);
```

- [ ] **Step 2: Write failing tests for Intel (little-endian) byte order extraction**

```csharp
// software/CanLinConfig.Tests/SignalExtractorTests.cs
using CanLinConfig.Parsers;
using CanLinConfig.Services;

namespace CanLinConfig.Tests;

public class SignalExtractorTests
{
    // Helper to create a DbcSignal with minimal params
    private static DbcSignal Sig(string name, int startBit, int bitLength,
        bool littleEndian = true, bool signed_ = false,
        double factor = 1.0, double offset = 0.0, string unit = "")
        => new()
        {
            Name = name, StartBit = startBit, BitLength = bitLength,
            IsLittleEndian = littleEndian, IsSigned = signed_,
            Factor = factor, Offset = offset, Unit = unit,
            MinValue = 0, MaxValue = 0
        };

    [Fact]
    public void Extract_Intel_8bit_byte0()
    {
        // Signal: 8 bits starting at bit 0, Intel byte order
        var signal = Sig("TestSig", startBit: 0, bitLength: 8);
        byte[] data = [0xAB, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

        double raw = SignalExtractor.ExtractRaw(data, signal);

        Assert.Equal(0xAB, raw);
    }

    [Fact]
    public void Extract_Intel_16bit_spanning_bytes()
    {
        // Signal: 16 bits starting at bit 8 (byte1), Intel byte order
        // Intel: LSB at bit 8 (byte1), MSB at bit 23 (byte2)
        var signal = Sig("RPM", startBit: 8, bitLength: 16);
        byte[] data = [0x00, 0xD2, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00];
        // 0x04D2 = 1234

        double raw = SignalExtractor.ExtractRaw(data, signal);

        Assert.Equal(1234.0, raw);
    }

    [Fact]
    public void Extract_Intel_with_factor_offset()
    {
        // Signal: 8 bits at bit 0, factor=0.5, offset=40
        // Physical = raw * 0.5 + 40
        var signal = Sig("Temp", startBit: 0, bitLength: 8, factor: 0.5, offset: -40, unit: "C");
        byte[] data = [200, 0, 0, 0, 0, 0, 0, 0];
        // Physical = 200 * 0.5 + (-40) = 100 - 40 = 60

        double physical = SignalExtractor.ExtractPhysical(data, signal);

        Assert.Equal(60.0, physical);
    }

    [Fact]
    public void Extract_Intel_signed_negative()
    {
        // Signal: 8 bits at bit 0, signed
        var signal = Sig("Temp", startBit: 0, bitLength: 8, signed_: true);
        byte[] data = [0xFE, 0, 0, 0, 0, 0, 0, 0]; // -2 as signed int8

        double raw = SignalExtractor.ExtractRaw(data, signal);

        Assert.Equal(-2.0, raw);
    }

    [Fact]
    public void Extract_Intel_4bit_nibble()
    {
        // Signal: 4 bits starting at bit 4 (upper nibble of byte 0)
        var signal = Sig("Gear", startBit: 4, bitLength: 4);
        byte[] data = [0xA7, 0, 0, 0, 0, 0, 0, 0];
        // Bits 4-7 of byte 0 = 0xA = 10

        double raw = SignalExtractor.ExtractRaw(data, signal);

        Assert.Equal(10.0, raw);
    }

    [Fact]
    public void Extract_Intel_1bit_boolean()
    {
        var signal = Sig("EngineOn", startBit: 2, bitLength: 1);
        byte[] data = [0x04, 0, 0, 0, 0, 0, 0, 0]; // bit 2 set

        double raw = SignalExtractor.ExtractRaw(data, signal);

        Assert.Equal(1.0, raw);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

```bash
cd software && dotnet test CanLinConfig.Tests --filter "SignalExtractorTests" -v n
```

Expected: All tests FAIL (SignalExtractor class doesn't exist).

- [ ] **Step 4: Implement SignalExtractor**

```csharp
// software/CanLinConfig/Services/SignalExtractor.cs
using CanLinConfig.Parsers;

namespace CanLinConfig.Services;

/// <summary>
/// Extracts signal values from CAN frame data bytes using DBC signal definitions.
/// Handles both Intel (little-endian) and Motorola (big-endian) byte ordering.
/// </summary>
public static class SignalExtractor
{
    /// <summary>
    /// Extract the raw integer value of a signal from CAN data bytes.
    /// </summary>
    public static double ExtractRaw(byte[] data, DbcSignal signal)
    {
        ulong rawBits;

        if (signal.IsLittleEndian)
            rawBits = ExtractIntel(data, signal.StartBit, signal.BitLength);
        else
            rawBits = ExtractMotorola(data, signal.StartBit, signal.BitLength);

        if (signal.IsSigned)
            return SignExtend(rawBits, signal.BitLength);

        return rawBits;
    }

    /// <summary>
    /// Extract the physical (scaled) value: physical = raw * factor + offset.
    /// </summary>
    public static double ExtractPhysical(byte[] data, DbcSignal signal)
    {
        double raw = ExtractRaw(data, signal);
        return raw * signal.Factor + signal.Offset;
    }

    private static ulong ExtractIntel(byte[] data, int startBit, int bitLength)
    {
        // Intel byte order: startBit is the LSB position.
        // Bit numbering: byte0[0..7], byte1[8..15], byte2[16..23], etc.
        ulong result = 0;
        for (int i = 0; i < bitLength; i++)
        {
            int bitPos = startBit + i;
            int byteIdx = bitPos / 8;
            int bitIdx = bitPos % 8;
            if (byteIdx < data.Length && (data[byteIdx] & (1 << bitIdx)) != 0)
                result |= 1UL << i;
        }
        return result;
    }

    private static ulong ExtractMotorola(byte[] data, int startBit, int bitLength)
    {
        // Motorola byte order: startBit is the MSB position.
        // DBC Motorola bit numbering: bit = byte_num * 8 + (7 - bit_in_byte)
        // Bits are numbered MSB-first within each byte.
        ulong result = 0;
        int bitPos = startBit;
        for (int i = bitLength - 1; i >= 0; i--)
        {
            int byteIdx = bitPos / 8;
            int bitIdx = 7 - (bitPos % 8);
            if (byteIdx < data.Length && (data[byteIdx] & (1 << bitIdx)) != 0)
                result |= 1UL << i;

            // Move to next bit in Motorola order
            if (bitPos % 8 == 0)
                bitPos += 15; // wrap to next byte MSB
            else
                bitPos--;
        }
        return result;
    }

    private static double SignExtend(ulong value, int bitLength)
    {
        // Check if MSB is set
        if ((value & (1UL << (bitLength - 1))) != 0)
        {
            // Sign extend: fill upper bits with 1s, cast to signed
            ulong mask = ulong.MaxValue << bitLength;
            return (double)(long)(value | mask);
        }
        return value;
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

```bash
cd software && dotnet test CanLinConfig.Tests --filter "SignalExtractorTests" -v n
```

Expected: All 6 tests PASS.

- [ ] **Step 6: Add Motorola byte order tests**

Add these to `SignalExtractorTests.cs`:

```csharp
[Fact]
public void Extract_Motorola_16bit()
{
    // Motorola: startBit is MSB position
    // 16-bit signal, MSB at bit 7 (byte0 bit7)
    // Byte 0 = MSB, Byte 1 = LSB
    var signal = Sig("Speed", startBit: 7, bitLength: 16, littleEndian: false);
    byte[] data = [0x04, 0xD2, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
    // 0x04D2 = 1234

    double raw = SignalExtractor.ExtractRaw(data, signal);

    Assert.Equal(1234.0, raw);
}

[Fact]
public void Extract_Motorola_8bit()
{
    var signal = Sig("Status", startBit: 7, bitLength: 8, littleEndian: false);
    byte[] data = [0xAB, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    double raw = SignalExtractor.ExtractRaw(data, signal);

    Assert.Equal(0xAB, raw);
}

[Fact]
public void Extract_Motorola_12bit()
{
    // 12-bit signal, MSB at byte0 bit7, spans into byte1
    var signal = Sig("Value", startBit: 7, bitLength: 12, littleEndian: false);
    byte[] data = [0xAB, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
    // Motorola: byte0=0xAB (MSB 8 bits), byte1 upper 4 bits = 0xC = 12
    // Full value = 0xABC = 2748

    double raw = SignalExtractor.ExtractRaw(data, signal);

    Assert.Equal(2748.0, raw);
}
```

- [ ] **Step 7: Run all SignalExtractor tests**

```bash
cd software && dotnet test CanLinConfig.Tests --filter "SignalExtractorTests" -v n
```

Expected: All 9 tests PASS.

---

## Task 4: DatabaseManager (TDD)

Manages per-bus DBC file assignments and provides signal lookup.

**Files:**
- Create: `software/CanLinConfig/Services/DatabaseManager.cs`
- Create: `software/CanLinConfig.Tests/DatabaseManagerTests.cs`
- Create: `software/CanLinConfig.Tests/TestData/test.dbc` (test fixture)

- [ ] **Step 1: Create a minimal test DBC file**

```dbc
// software/CanLinConfig.Tests/TestData/test.dbc
VERSION ""

NS_ :

BS_:

BU_: ECU1

BO_ 256 EngineData: 8 ECU1
 SG_ EngineRPM : 0|16@1+ (0.25,0) [0|16383.75] "rpm" Vector__XXX
 SG_ CoolantTemp : 16|8@1+ (1,-40) [-40|215] "C" Vector__XXX
 SG_ EngineOn : 24|1@1+ (1,0) [0|1] "" Vector__XXX

BO_ 512 VehicleSpeed: 4 ECU1
 SG_ Speed : 0|16@1+ (0.01,0) [0|655.35] "km/h" Vector__XXX
 SG_ GearPos : 16|4@1+ (1,0) [0|15] "" Vector__XXX
```

Mark it as embedded resource or copy-to-output in the test project. Add to `.csproj`:

```xml
<ItemGroup>
  <None Update="TestData\test.dbc">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
</ItemGroup>
```

- [ ] **Step 2: Write failing tests**

```csharp
// software/CanLinConfig.Tests/DatabaseManagerTests.cs
using CanLinConfig.Models;
using CanLinConfig.Services;

namespace CanLinConfig.Tests;

public class DatabaseManagerTests
{
    private static string TestDbcPath =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "test.dbc");

    [Fact]
    public void AssignDatabase_loads_and_caches_signals()
    {
        var mgr = new DatabaseManager();
        mgr.AssignDatabase(BusFrame.Bus.CAN1, TestDbcPath);

        var signals = mgr.GetSignals(BusFrame.Bus.CAN1, 256);

        Assert.Equal(3, signals.Count);
        Assert.Contains(signals, s => s.Name == "EngineRPM");
        Assert.Contains(signals, s => s.Name == "CoolantTemp");
        Assert.Contains(signals, s => s.Name == "EngineOn");
    }

    [Fact]
    public void GetSignals_returns_empty_for_unknown_bus()
    {
        var mgr = new DatabaseManager();

        var signals = mgr.GetSignals(BusFrame.Bus.CAN2, 256);

        Assert.Empty(signals);
    }

    [Fact]
    public void GetSignals_returns_empty_for_unknown_id()
    {
        var mgr = new DatabaseManager();
        mgr.AssignDatabase(BusFrame.Bus.CAN1, TestDbcPath);

        var signals = mgr.GetSignals(BusFrame.Bus.CAN1, 999);

        Assert.Empty(signals);
    }

    [Fact]
    public void GetMessageName_returns_name_when_loaded()
    {
        var mgr = new DatabaseManager();
        mgr.AssignDatabase(BusFrame.Bus.CAN1, TestDbcPath);

        Assert.Equal("EngineData", mgr.GetMessageName(BusFrame.Bus.CAN1, 256));
        Assert.Null(mgr.GetMessageName(BusFrame.Bus.CAN1, 999));
        Assert.Null(mgr.GetMessageName(BusFrame.Bus.CAN2, 256));
    }

    [Fact]
    public void RemoveDatabase_clears_assignment()
    {
        var mgr = new DatabaseManager();
        mgr.AssignDatabase(BusFrame.Bus.CAN1, TestDbcPath);
        mgr.RemoveDatabase(BusFrame.Bus.CAN1);

        Assert.Empty(mgr.GetSignals(BusFrame.Bus.CAN1, 256));
    }

    [Fact]
    public void DecodeFrame_extracts_physical_values()
    {
        var mgr = new DatabaseManager();
        mgr.AssignDatabase(BusFrame.Bus.CAN1, TestDbcPath);

        // EngineRPM: 16bit@0 Intel, factor=0.25, offset=0
        // CoolantTemp: 8bit@16 Intel, factor=1, offset=-40
        // EngineOn: 1bit@24 Intel
        byte[] data = [0xE8, 0x03, 200, 0x01, 0, 0, 0, 0];
        // RPM raw = 0x03E8 = 1000, physical = 1000 * 0.25 = 250 rpm
        // Temp raw = 200, physical = 200 - 40 = 160 C
        // EngineOn raw = 1

        var frame = new BusFrame(BusFrame.Bus.CAN1, 256, 8, data, DateTime.Now);
        var values = mgr.DecodeFrame(frame);

        Assert.Equal(3, values.Count);

        var rpm = values.First(v => v.Name == "EngineRPM");
        Assert.Equal(250.0, rpm.PhysicalValue);
        Assert.Equal("rpm", rpm.Unit);

        var temp = values.First(v => v.Name == "CoolantTemp");
        Assert.Equal(160.0, temp.PhysicalValue);

        var eng = values.First(v => v.Name == "EngineOn");
        Assert.Equal(1.0, eng.RawValue);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

```bash
cd software && dotnet test CanLinConfig.Tests --filter "DatabaseManagerTests" -v n
```

Expected: All tests FAIL (DatabaseManager doesn't exist).

- [ ] **Step 4: Implement DatabaseManager**

```csharp
// software/CanLinConfig/Services/DatabaseManager.cs
using CanLinConfig.Models;
using CanLinConfig.Parsers;

namespace CanLinConfig.Services;

/// <summary>
/// Manages per-bus DBC/LDF database assignments and provides signal lookup.
/// Thread-safe: all public methods use a lock around the internal dictionaries.
/// </summary>
public class DatabaseManager
{
    private readonly object _lock = new();
    private readonly Dictionary<BusFrame.Bus, DbcFile> _databases = new();
    private readonly Dictionary<BusFrame.Bus, string> _dbPaths = new();

    // Cached lookup: (bus, messageId) → list of signals
    private readonly Dictionary<(BusFrame.Bus, uint), IReadOnlyList<DbcSignal>> _signalCache = new();
    // Cached lookup: (bus, messageId) → message name
    private readonly Dictionary<(BusFrame.Bus, uint), string> _nameCache = new();

    public void AssignDatabase(BusFrame.Bus bus, string filePath)
    {
        var dbc = DbcParser.Parse(filePath);
        lock (_lock)
        {
            _databases[bus] = dbc;
            _dbPaths[bus] = filePath;
            RebuildCache(bus, dbc);
        }
    }

    public void RemoveDatabase(BusFrame.Bus bus)
    {
        lock (_lock)
        {
            _databases.Remove(bus);
            _dbPaths.Remove(bus);
            ClearCache(bus);
        }
    }

    public string? GetDatabasePath(BusFrame.Bus bus)
    {
        lock (_lock)
            return _dbPaths.GetValueOrDefault(bus);
    }

    public IReadOnlyList<DbcSignal> GetSignals(BusFrame.Bus bus, uint messageId)
    {
        lock (_lock)
            return _signalCache.GetValueOrDefault((bus, messageId), []);
    }

    public string? GetMessageName(BusFrame.Bus bus, uint messageId)
    {
        lock (_lock)
            return _nameCache.GetValueOrDefault((bus, messageId));
    }

    /// <summary>
    /// Decode all signals from a frame using the assigned database.
    /// Returns empty list if no database assigned or message not found.
    /// </summary>
    public IReadOnlyList<SignalValue> DecodeFrame(BusFrame frame)
    {
        var signals = GetSignals(frame.SourceBus, frame.Id);
        if (signals.Count == 0)
            return [];

        var values = new List<SignalValue>(signals.Count);
        foreach (var sig in signals)
        {
            double raw = SignalExtractor.ExtractRaw(frame.Data, sig);
            double physical = raw * sig.Factor + sig.Offset;
            values.Add(new SignalValue(
                sig.Name, raw, physical, sig.Unit,
                (byte)frame.SourceBus, frame.Id, frame.Timestamp));
        }
        return values;
    }

    /// <summary>
    /// Get all known message IDs for a bus.
    /// </summary>
    public IReadOnlyList<(uint Id, string Name)> GetMessages(BusFrame.Bus bus)
    {
        lock (_lock)
        {
            if (!_databases.TryGetValue(bus, out var dbc))
                return [];
            return dbc.Messages.Select(m => (m.Id, m.Name)).ToList();
        }
    }

    /// <summary>
    /// Get all database assignments as (bus, path) pairs.
    /// Used for project save/restore.
    /// </summary>
    public IReadOnlyDictionary<BusFrame.Bus, string> GetAssignments()
    {
        lock (_lock)
            return new Dictionary<BusFrame.Bus, string>(_dbPaths);
    }

    private void RebuildCache(BusFrame.Bus bus, DbcFile dbc)
    {
        ClearCache(bus);
        foreach (var msg in dbc.Messages)
        {
            _signalCache[(bus, msg.Id)] = msg.Signals;
            _nameCache[(bus, msg.Id)] = msg.Name;
        }
    }

    private void ClearCache(BusFrame.Bus bus)
    {
        var keysToRemove = _signalCache.Keys.Where(k => k.Item1 == bus).ToList();
        foreach (var key in keysToRemove)
        {
            _signalCache.Remove(key);
            _nameCache.Remove(key);
        }
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

```bash
cd software && dotnet test CanLinConfig.Tests --filter "DatabaseManagerTests" -v n
```

Expected: All 6 tests PASS.

---

## Task 5: BusDataService (TDD)

Central pipeline that receives frames, decodes signals, dispatches to consumers.

**Files:**
- Create: `software/CanLinConfig/Services/BusDataService.cs`
- Create: `software/CanLinConfig.Tests/BusDataServiceTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// software/CanLinConfig.Tests/BusDataServiceTests.cs
using CanLinConfig.Models;
using CanLinConfig.Services;

namespace CanLinConfig.Tests;

public class BusDataServiceTests
{
    [Fact]
    public void OnFrame_raises_FrameReceived_event()
    {
        var dbMgr = new DatabaseManager();
        var svc = new BusDataService(dbMgr);
        BusFrame? received = null;
        svc.FrameReceived += (_, f) => received = f;

        var frame = new BusFrame(BusFrame.Bus.CAN1, 0x100, 8,
            new byte[8], DateTime.Now);
        svc.OnFrame(frame);

        Assert.NotNull(received);
        Assert.Equal(0x100u, received!.Id);
        Assert.Equal(BusFrame.Bus.CAN1, received.SourceBus);
    }

    [Fact]
    public void OnFrame_raises_SignalsDecoded_when_database_loaded()
    {
        var dbMgr = new DatabaseManager();
        var testDbc = Path.Combine(AppContext.BaseDirectory, "TestData", "test.dbc");
        dbMgr.AssignDatabase(BusFrame.Bus.CAN1, testDbc);

        var svc = new BusDataService(dbMgr);
        IReadOnlyList<SignalValue>? decoded = null;
        svc.SignalsDecoded += (_, signals) => decoded = signals;

        byte[] data = [0xE8, 0x03, 200, 0x01, 0, 0, 0, 0];
        var frame = new BusFrame(BusFrame.Bus.CAN1, 256, 8, data, DateTime.Now);
        svc.OnFrame(frame);

        Assert.NotNull(decoded);
        Assert.Equal(3, decoded!.Count);
    }

    [Fact]
    public void OnFrame_no_signals_for_unknown_message()
    {
        var dbMgr = new DatabaseManager();
        var svc = new BusDataService(dbMgr);
        bool signalFired = false;
        svc.SignalsDecoded += (_, _) => signalFired = true;

        var frame = new BusFrame(BusFrame.Bus.CAN1, 0x999, 8,
            new byte[8], DateTime.Now);
        svc.OnFrame(frame);

        Assert.False(signalFired);
    }

    [Fact]
    public void OnCanFrame_wraps_with_CAN1_bus()
    {
        var dbMgr = new DatabaseManager();
        var svc = new BusDataService(dbMgr);
        BusFrame? received = null;
        svc.FrameReceived += (_, f) => received = f;

        var canFrame = new Adapters.CanFrame(0x100, new byte[8]);
        svc.OnCanFrame(canFrame);

        Assert.NotNull(received);
        Assert.Equal(BusFrame.Bus.CAN1, received!.SourceBus);
    }

    [Fact]
    public void History_buffer_stores_frames()
    {
        var dbMgr = new DatabaseManager();
        var svc = new BusDataService(dbMgr, maxHistory: 100);

        for (int i = 0; i < 10; i++)
            svc.OnFrame(new BusFrame(BusFrame.Bus.CAN1, (uint)i, 0, new byte[8], DateTime.Now));

        Assert.Equal(10, svc.FrameHistory.Count);
    }

    [Fact]
    public void History_buffer_caps_at_max()
    {
        var dbMgr = new DatabaseManager();
        var svc = new BusDataService(dbMgr, maxHistory: 5);

        for (int i = 0; i < 10; i++)
            svc.OnFrame(new BusFrame(BusFrame.Bus.CAN1, (uint)i, 0, new byte[8], DateTime.Now));

        Assert.Equal(5, svc.FrameHistory.Count);
        // Oldest should be gone, newest retained
        Assert.Equal(5u, svc.FrameHistory[0].Id);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
cd software && dotnet test CanLinConfig.Tests --filter "BusDataServiceTests" -v n
```

Expected: All tests FAIL.

- [ ] **Step 3: Implement BusDataService**

```csharp
// software/CanLinConfig/Services/BusDataService.cs
using CanLinConfig.Adapters;
using CanLinConfig.Models;

namespace CanLinConfig.Services;

/// <summary>
/// Central frame pipeline. Receives raw frames from any source,
/// tags with source bus, decodes signals, broadcasts to consumers.
/// </summary>
public class BusDataService
{
    private readonly DatabaseManager _dbManager;
    private readonly int _maxHistory;
    private readonly List<BusFrame> _history;
    private readonly object _historyLock = new();

    /// <summary>Fired for every frame received, regardless of decoding.</summary>
    public event EventHandler<BusFrame>? FrameReceived;

    /// <summary>Fired when a frame matches a database and signals are decoded.
    /// Only fired if at least one signal is decoded.</summary>
    public event EventHandler<IReadOnlyList<SignalValue>>? SignalsDecoded;

    public BusDataService(DatabaseManager dbManager, int maxHistory = 50_000)
    {
        _dbManager = dbManager;
        _maxHistory = maxHistory;
        _history = new List<BusFrame>(Math.Min(maxHistory, 1024));
    }

    public IReadOnlyList<BusFrame> FrameHistory
    {
        get { lock (_historyLock) return _history.ToList(); }
    }

    /// <summary>Process a frame tagged with its source bus.</summary>
    public void OnFrame(BusFrame frame)
    {
        // Store in history
        lock (_historyLock)
        {
            _history.Add(frame);
            while (_history.Count > _maxHistory)
                _history.RemoveAt(0);
        }

        // Broadcast raw frame
        FrameReceived?.Invoke(this, frame);

        // Decode signals
        var signals = _dbManager.DecodeFrame(frame);
        if (signals.Count > 0)
            SignalsDecoded?.Invoke(this, signals);
    }

    /// <summary>
    /// Convenience: wrap a raw CanFrame from the adapter as CAN1.
    /// Used for direct adapter hookup (before monitor protocol is implemented).
    /// </summary>
    public void OnCanFrame(CanFrame frame)
    {
        OnFrame(new BusFrame(frame, BusFrame.Bus.CAN1));
    }

    public void ClearHistory()
    {
        lock (_historyLock)
            _history.Clear();
    }

    public DatabaseManager DatabaseManager => _dbManager;
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
cd software && dotnet test CanLinConfig.Tests --filter "BusDataServiceTests" -v n
```

Expected: All 6 tests PASS.

---

## Task 6: TracePanelViewModel

High-performance frame trace with circular buffer and bus/ID filtering.

**Files:**
- Create: `software/CanLinConfig/ViewModels/TracePanelViewModel.cs`
- Create: `software/CanLinConfig.Tests/TracePanelViewModelTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// software/CanLinConfig.Tests/TracePanelViewModelTests.cs
using CanLinConfig.Models;
using CanLinConfig.ViewModels;

namespace CanLinConfig.Tests;

public class TracePanelViewModelTests
{
    [Fact]
    public void AddFrame_adds_entry_to_list()
    {
        var vm = new TracePanelViewModel(maxEntries: 100);
        var frame = new BusFrame(BusFrame.Bus.CAN1, 0x100, 8, new byte[8], DateTime.Now);

        vm.AddFrame(frame, messageName: "EngineData");

        Assert.Single(vm.Entries);
        Assert.Equal("0x100", vm.Entries[0].Id);
        Assert.Equal("CAN1", vm.Entries[0].Bus);
        Assert.Equal("EngineData", vm.Entries[0].MessageName);
    }

    [Fact]
    public void AddFrame_caps_at_max_entries()
    {
        var vm = new TracePanelViewModel(maxEntries: 5);

        for (int i = 0; i < 10; i++)
            vm.AddFrame(new BusFrame(BusFrame.Bus.CAN1, (uint)i, 0, new byte[8], DateTime.Now), null);

        Assert.Equal(5, vm.Entries.Count);
    }

    [Fact]
    public void Paused_state_prevents_adds()
    {
        var vm = new TracePanelViewModel(maxEntries: 100);
        vm.IsPaused = true;

        vm.AddFrame(new BusFrame(BusFrame.Bus.CAN1, 0x100, 0, new byte[8], DateTime.Now), null);

        Assert.Empty(vm.Entries);
    }

    [Fact]
    public void Clear_removes_all_entries()
    {
        var vm = new TracePanelViewModel(maxEntries: 100);
        vm.AddFrame(new BusFrame(BusFrame.Bus.CAN1, 0x100, 0, new byte[8], DateTime.Now), null);
        vm.AddFrame(new BusFrame(BusFrame.Bus.CAN1, 0x101, 0, new byte[8], DateTime.Now), null);

        vm.ClearCommand.Execute(null);

        Assert.Empty(vm.Entries);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
cd software && dotnet test CanLinConfig.Tests --filter "TracePanelViewModelTests" -v n
```

Expected: All tests FAIL.

- [ ] **Step 3: Implement TracePanelViewModel**

```csharp
// software/CanLinConfig/ViewModels/TracePanelViewModel.cs
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanLinConfig.Models;

namespace CanLinConfig.ViewModels;

public partial class TracePanelViewModel : ObservableObject
{
    private readonly int _maxEntries;

    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string _idFilter = "";
    [ObservableProperty] private string _busFilter = "All";

    public ObservableCollection<TraceEntry> Entries { get; } = [];

    public TracePanelViewModel(int maxEntries = 10_000)
    {
        _maxEntries = maxEntries;
    }

    public void AddFrame(BusFrame frame, string? messageName)
    {
        if (IsPaused) return;

        var entry = new TraceEntry(frame, messageName);

        // Apply filters
        if (!PassesFilter(entry)) return;

        Entries.Add(entry);
        while (Entries.Count > _maxEntries)
            Entries.RemoveAt(0);
    }

    private bool PassesFilter(TraceEntry entry)
    {
        if (!string.IsNullOrEmpty(IdFilter))
        {
            var filterText = IdFilter.Replace("0x", "").Replace("0X", "");
            if (uint.TryParse(filterText, System.Globalization.NumberStyles.HexNumber, null, out uint filterId))
            {
                if (entry.RawId != filterId) return false;
            }
        }

        if (BusFilter != "All" && entry.Bus != BusFilter)
            return false;

        return true;
    }

    [RelayCommand]
    private void Clear() => Entries.Clear();

    [RelayCommand]
    private void TogglePause() => IsPaused = !IsPaused;
}

public class TraceEntry
{
    public string Time { get; }
    public string Bus { get; }
    public string Id { get; }
    public uint RawId { get; }
    public byte Dlc { get; }
    public string DataHex { get; }
    public string? MessageName { get; }

    public TraceEntry(BusFrame frame, string? messageName)
    {
        Time = frame.Timestamp.ToString("HH:mm:ss.fff");
        Bus = frame.BusName;
        Id = $"0x{frame.Id:X3}";
        RawId = frame.Id;
        Dlc = frame.Dlc;
        DataHex = string.Join(" ", frame.Data.Take(frame.Dlc).Select(b => b.ToString("X2")));
        MessageName = messageName;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
cd software && dotnet test CanLinConfig.Tests --filter "TracePanelViewModelTests" -v n
```

Expected: All 4 tests PASS.

---

## Task 7: SignalPanelViewModel

Live signal value table — tracks latest value, min/max, and last update time per signal.

**Files:**
- Create: `software/CanLinConfig/ViewModels/SignalPanelViewModel.cs`

- [ ] **Step 1: Implement SignalPanelViewModel**

```csharp
// software/CanLinConfig/ViewModels/SignalPanelViewModel.cs
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanLinConfig.Models;

namespace CanLinConfig.ViewModels;

public partial class SignalPanelViewModel : ObservableObject
{
    // Key: "Bus:MessageId:SignalName"
    private readonly Dictionary<string, SignalEntry> _entryMap = new();

    public ObservableCollection<SignalEntry> Signals { get; } = [];

    /// <summary>Raised when user requests a signal to be graphed.</summary>
    public event EventHandler<SignalEntry>? AddToGraphRequested;

    public void UpdateSignals(IReadOnlyList<SignalValue> values)
    {
        foreach (var sv in values)
        {
            string key = $"{sv.Bus}:{sv.FrameId}:{sv.Name}";
            if (_entryMap.TryGetValue(key, out var entry))
            {
                entry.Update(sv);
            }
            else
            {
                entry = new SignalEntry(sv);
                _entryMap[key] = entry;
                Signals.Add(entry);
            }
        }
    }

    [RelayCommand]
    private void AddToGraph(SignalEntry? entry)
    {
        if (entry != null)
            AddToGraphRequested?.Invoke(this, entry);
    }

    [RelayCommand]
    private void Clear()
    {
        Signals.Clear();
        _entryMap.Clear();
    }
}

public partial class SignalEntry : ObservableObject
{
    public string Name { get; }
    public string Unit { get; }
    public string Bus { get; }
    public uint FrameId { get; }
    public string MessageKey { get; }

    [ObservableProperty] private double _value;
    [ObservableProperty] private double _rawValue;
    [ObservableProperty] private double _minValue = double.MaxValue;
    [ObservableProperty] private double _maxValue = double.MinValue;
    [ObservableProperty] private string _lastUpdate = "";

    public SignalEntry(SignalValue sv)
    {
        Name = sv.Name;
        Unit = sv.Unit;
        Bus = ((BusFrame.Bus)sv.Bus).ToString();
        FrameId = sv.FrameId;
        MessageKey = $"{sv.Bus}:{sv.FrameId}:{sv.Name}";
        Update(sv);
    }

    public void Update(SignalValue sv)
    {
        Value = sv.PhysicalValue;
        RawValue = sv.RawValue;
        if (sv.PhysicalValue < MinValue) MinValue = sv.PhysicalValue;
        if (sv.PhysicalValue > MaxValue) MaxValue = sv.PhysicalValue;
        LastUpdate = sv.Timestamp.ToString("HH:mm:ss.fff");
    }
}
```

- [ ] **Step 2: Verify build**

```bash
cd software && dotnet build CanLinConfig/CanLinConfig.csproj
```

Expected: Build succeeds.

---

## Task 8: GraphPanelViewModel + ScottPlot

**Files:**
- Modify: `software/CanLinConfig/CanLinConfig.csproj` (add ScottPlot NuGet)
- Create: `software/CanLinConfig/ViewModels/GraphPanelViewModel.cs`

- [ ] **Step 1: Add ScottPlot NuGet package**

```bash
cd software/CanLinConfig && dotnet add package ScottPlot.WPF
```

**Note:** Install the latest stable 5.x release. After install, run `dotnet list software/CanLinConfig package --include-transitive | grep -i scottplot` to confirm the exact version. ScottPlot 5.x has API differences from 4.x — the code below targets the 5.0.x API. If the installed version differs, consult the [ScottPlot 5 Cookbook](https://scottplot.net/cookbook/5.0/) and adjust method names accordingly.

- [ ] **Step 2: Verify build still works**

```bash
cd software && dotnet build CanLinConfig/CanLinConfig.csproj
```

Expected: Build succeeds with ScottPlot restored.

- [ ] **Step 3: Implement GraphPanelViewModel**

```csharp
// software/CanLinConfig/ViewModels/GraphPanelViewModel.cs
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanLinConfig.Models;

namespace CanLinConfig.ViewModels;

public partial class GraphPanelViewModel : ObservableObject
{
    private readonly int _maxPoints;

    // Key: signal key ("Bus:MsgId:Name"), Value: list of (timestamp, value) points
    private readonly Dictionary<string, SignalTrace> _traces = new();

    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private double _timeWindowSeconds = 30.0;

    public ObservableCollection<SignalTrace> Traces { get; } = [];

    /// <summary>Raised when trace data changes and the plot needs refresh.</summary>
    public event EventHandler? PlotNeedsRefresh;

    public GraphPanelViewModel(int maxPointsPerSignal = 10_000)
    {
        _maxPoints = maxPointsPerSignal;
    }

    public void AddSignal(string key, string displayName, string unit)
    {
        if (_traces.ContainsKey(key)) return;
        var trace = new SignalTrace(key, displayName, unit, _maxPoints);
        _traces[key] = trace;
        Traces.Add(trace);
    }

    public void RemoveSignal(string key)
    {
        if (_traces.Remove(key, out var trace))
            Traces.Remove(trace);
    }

    public void OnSignalValues(IReadOnlyList<SignalValue> values)
    {
        if (IsPaused) return;

        bool anyUpdated = false;
        foreach (var sv in values)
        {
            string key = $"{sv.Bus}:{sv.FrameId}:{sv.Name}";
            if (_traces.TryGetValue(key, out var trace))
            {
                trace.AddPoint(sv.Timestamp, sv.PhysicalValue);
                anyUpdated = true;
            }
        }

        if (anyUpdated)
            PlotNeedsRefresh?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void TogglePause() => IsPaused = !IsPaused;

    [RelayCommand]
    private void ClearAll()
    {
        foreach (var trace in _traces.Values)
            trace.Clear();
        PlotNeedsRefresh?.Invoke(this, EventArgs.Empty);
    }
}

public class SignalTrace
{
    public string Key { get; }
    public string DisplayName { get; }
    public string Unit { get; }
    private readonly int _maxPoints;

    // Thread-safe data storage — read from UI thread, written from pipeline
    private readonly object _lock = new();
    private readonly List<double> _timestamps = new();
    private readonly List<double> _values = new();

    public SignalTrace(string key, string displayName, string unit, int maxPoints)
    {
        Key = key;
        DisplayName = displayName;
        Unit = unit;
        _maxPoints = maxPoints;
    }

    public void AddPoint(DateTime timestamp, double value)
    {
        lock (_lock)
        {
            _timestamps.Add(timestamp.ToOADate());
            _values.Add(value);
            while (_timestamps.Count > _maxPoints)
            {
                _timestamps.RemoveAt(0);
                _values.RemoveAt(0);
            }
        }
    }

    public (double[] timestamps, double[] values) GetData()
    {
        lock (_lock)
            return (_timestamps.ToArray(), _values.ToArray());
    }

    public void Clear()
    {
        lock (_lock)
        {
            _timestamps.Clear();
            _values.Clear();
        }
    }
}
```

- [ ] **Step 4: Verify build**

```bash
cd software && dotnet build CanLinConfig/CanLinConfig.csproj
```

Expected: Build succeeds.

---

## Task 9: XAML Views — TracePanel, SignalPanel, GraphPanel

**Files:**
- Create: `software/CanLinConfig/Views/TracePanel.xaml` + `.xaml.cs`
- Create: `software/CanLinConfig/Views/SignalPanel.xaml` + `.xaml.cs`
- Create: `software/CanLinConfig/Views/GraphPanel.xaml` + `.xaml.cs`

- [ ] **Step 1: Create TracePanel view**

```xml
<!-- software/CanLinConfig/Views/TracePanel.xaml -->
<UserControl x:Class="CanLinConfig.Views.TracePanel"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:CanLinConfig.ViewModels">
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <!-- Toolbar -->
        <ToolBar Grid.Row="0">
            <Button Command="{Binding TogglePauseCommand}">
                <Button.Style>
                    <Style TargetType="Button">
                        <Setter Property="Content" Value="Pause"/>
                        <Style.Triggers>
                            <DataTrigger Binding="{Binding IsPaused}" Value="True">
                                <Setter Property="Content" Value="Resume"/>
                            </DataTrigger>
                        </Style.Triggers>
                    </Style>
                </Button.Style>
            </Button>
            <Button Content="Clear" Command="{Binding ClearCommand}"/>
            <Separator/>
            <TextBlock Text="ID Filter:" VerticalAlignment="Center" Margin="5,0"/>
            <TextBox Text="{Binding IdFilter, UpdateSourceTrigger=PropertyChanged}"
                     Width="80" VerticalAlignment="Center"/>
            <Separator/>
            <TextBlock Text="Bus:" VerticalAlignment="Center" Margin="5,0"/>
            <ComboBox SelectedItem="{Binding BusFilter}" Width="80">
                <ComboBoxItem Content="All"/>
                <ComboBoxItem Content="CAN1"/>
                <ComboBoxItem Content="CAN2"/>
                <ComboBoxItem Content="LIN1"/>
                <ComboBoxItem Content="LIN2"/>
                <ComboBoxItem Content="LIN3"/>
                <ComboBoxItem Content="LIN4"/>
            </ComboBox>
        </ToolBar>

        <!-- Frame list -->
        <DataGrid Grid.Row="1" ItemsSource="{Binding Entries}"
                  AutoGenerateColumns="False" IsReadOnly="True"
                  VirtualizingPanel.IsVirtualizing="True"
                  VirtualizingPanel.VirtualizationMode="Recycling"
                  EnableRowVirtualization="True"
                  CanUserSortColumns="False">
            <DataGrid.Columns>
                <DataGridTextColumn Header="Time" Binding="{Binding Time}" Width="100"/>
                <DataGridTextColumn Header="Bus" Binding="{Binding Bus}" Width="55"/>
                <DataGridTextColumn Header="ID" Binding="{Binding Id}" Width="70"/>
                <DataGridTextColumn Header="DLC" Binding="{Binding Dlc}" Width="40"/>
                <DataGridTextColumn Header="Data" Binding="{Binding DataHex}" Width="*"/>
                <DataGridTextColumn Header="Message" Binding="{Binding MessageName}" Width="140"/>
            </DataGrid.Columns>
        </DataGrid>
    </Grid>
</UserControl>
```

```csharp
// software/CanLinConfig/Views/TracePanel.xaml.cs
using System.Windows.Controls;

namespace CanLinConfig.Views;

public partial class TracePanel : UserControl
{
    public TracePanel()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 2: Create SignalPanel view**

```xml
<!-- software/CanLinConfig/Views/SignalPanel.xaml -->
<UserControl x:Class="CanLinConfig.Views.SignalPanel"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:CanLinConfig.ViewModels">
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <ToolBar Grid.Row="0">
            <Button Content="Clear" Command="{Binding ClearCommand}"/>
        </ToolBar>

        <DataGrid x:Name="SignalGrid" Grid.Row="1" ItemsSource="{Binding Signals}"
                  AutoGenerateColumns="False" IsReadOnly="True"
                  VirtualizingPanel.IsVirtualizing="True"
                  EnableRowVirtualization="True">
            <DataGrid.Columns>
                <DataGridTextColumn Header="Signal" Binding="{Binding Name}" Width="140"/>
                <DataGridTextColumn Header="Value" Binding="{Binding Value, StringFormat=F3}" Width="100"/>
                <DataGridTextColumn Header="Unit" Binding="{Binding Unit}" Width="60"/>
                <DataGridTextColumn Header="Raw" Binding="{Binding RawValue}" Width="80"/>
                <DataGridTextColumn Header="Min" Binding="{Binding MinValue, StringFormat=F3}" Width="80"/>
                <DataGridTextColumn Header="Max" Binding="{Binding MaxValue, StringFormat=F3}" Width="80"/>
                <DataGridTextColumn Header="Bus" Binding="{Binding Bus}" Width="55"/>
                <DataGridTextColumn Header="Last Update" Binding="{Binding LastUpdate}" Width="100"/>
            </DataGrid.Columns>
        </DataGrid>
    </Grid>
</UserControl>
```

The ContextMenu is wired in code-behind to avoid WPF visual tree issues (ContextMenus exist in a separate visual tree and cannot use RelativeSource to find the DataGrid):

```csharp
// software/CanLinConfig/Views/SignalPanel.xaml.cs
using System.Windows.Controls;
using CanLinConfig.ViewModels;

namespace CanLinConfig.Views;

public partial class SignalPanel : UserControl
{
    public SignalPanel()
    {
        InitializeComponent();

        var menu = new ContextMenu();
        var addToGraph = new MenuItem { Header = "Add to Graph" };
        addToGraph.Click += (_, _) =>
        {
            if (DataContext is SignalPanelViewModel vm &&
                SignalGrid.SelectedItem is SignalEntry entry)
            {
                vm.AddToGraphCommand.Execute(entry);
            }
        };
        menu.Items.Add(addToGraph);
        SignalGrid.ContextMenu = menu;
    }
}
```

- [ ] **Step 3: Create GraphPanel view**

```xml
<!-- software/CanLinConfig/Views/GraphPanel.xaml -->
<UserControl x:Class="CanLinConfig.Views.GraphPanel"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:sys="clr-namespace:System;assembly=mscorlib"
             xmlns:scottplot="clr-namespace:ScottPlot.WPF;assembly=ScottPlot.WPF">
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <ToolBar Grid.Row="0">
            <Button Command="{Binding TogglePauseCommand}">
                <Button.Style>
                    <Style TargetType="Button">
                        <Setter Property="Content" Value="Pause"/>
                        <Style.Triggers>
                            <DataTrigger Binding="{Binding IsPaused}" Value="True">
                                <Setter Property="Content" Value="Resume"/>
                            </DataTrigger>
                        </Style.Triggers>
                    </Style>
                </Button.Style>
            </Button>
            <Button Content="Clear" Command="{Binding ClearAllCommand}"/>
            <Separator/>
            <TextBlock Text="Window:" VerticalAlignment="Center" Margin="5,0"/>
            <ComboBox SelectedValue="{Binding TimeWindowSeconds}"
                      SelectedValuePath="Tag" Width="70">
                <ComboBoxItem Content="10s">
                    <ComboBoxItem.Tag><sys:Double>10</sys:Double></ComboBoxItem.Tag>
                </ComboBoxItem>
                <ComboBoxItem Content="30s">
                    <ComboBoxItem.Tag><sys:Double>30</sys:Double></ComboBoxItem.Tag>
                </ComboBoxItem>
                <ComboBoxItem Content="60s">
                    <ComboBoxItem.Tag><sys:Double>60</sys:Double></ComboBoxItem.Tag>
                </ComboBoxItem>
                <ComboBoxItem Content="5m">
                    <ComboBoxItem.Tag><sys:Double>300</sys:Double></ComboBoxItem.Tag>
                </ComboBoxItem>
            </ComboBox>
        </ToolBar>

        <scottplot:WpfPlot x:Name="WpfPlot" Grid.Row="1"/>
    </Grid>
</UserControl>
```

**Note on ScottPlot 5.x:** The xmlns namespace and API below target ScottPlot 5.0.x. After installing the package, verify the WPF control namespace by checking the installed assembly. If the xmlns doesn't resolve, check `ScottPlot.WPF.WpfPlot` in the Object Browser. The ScottPlot 5.x API differs significantly from 4.x — consult the [ScottPlot 5 Cookbook](https://scottplot.net/cookbook/5.0/) if method signatures don't match.

```csharp
// software/CanLinConfig/Views/GraphPanel.xaml.cs
using System.Windows.Controls;
using System.Windows.Threading;
using CanLinConfig.ViewModels;

namespace CanLinConfig.Views;

public partial class GraphPanel : UserControl
{
    private DispatcherTimer? _refreshTimer;

    public GraphPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is GraphPanelViewModel oldVm)
            oldVm.PlotNeedsRefresh -= OnPlotNeedsRefresh;

        _refreshTimer?.Stop();

        if (e.NewValue is GraphPanelViewModel newVm)
        {
            newVm.PlotNeedsRefresh += OnPlotNeedsRefresh;
            SetupPlot();
        }
    }

    private void SetupPlot()
    {
        WpfPlot.Plot.Title("Signal Graph");
        WpfPlot.Plot.XLabel("Time");
        WpfPlot.Plot.YLabel("Value");
        WpfPlot.Refresh();

        // Throttled refresh at 20 Hz max to avoid overwhelming the UI
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _refreshTimer.Tick += (_, _) => RefreshPlot();
        _refreshTimer.Start();
    }

    private bool _needsRefresh;

    private void OnPlotNeedsRefresh(object? sender, EventArgs e)
    {
        _needsRefresh = true;
    }

    private void RefreshPlot()
    {
        if (!_needsRefresh || DataContext is not GraphPanelViewModel vm) return;
        _needsRefresh = false;

        // ScottPlot 5.x: clear all plottables and re-add
        WpfPlot.Plot.PlottableList.Clear();

        foreach (var trace in vm.Traces)
        {
            var (timestamps, values) = trace.GetData();
            if (timestamps.Length < 2) continue;

            var scatter = WpfPlot.Plot.Add.Scatter(timestamps, values);
            scatter.LegendText = $"{trace.DisplayName} [{trace.Unit}]";
        }

        // ScottPlot 5.x: DateTime tick formatting on bottom axis
        // If this method doesn't exist in your installed version, use:
        //   WpfPlot.Plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.DateTimeAutomatic();
        WpfPlot.Plot.Axes.DateTimeTicksBottom();
        WpfPlot.Plot.ShowLegend();
        WpfPlot.Refresh();
    }
}
```

- [ ] **Step 4: Verify build**

```bash
cd software && dotnet build CanLinConfig/CanLinConfig.csproj
```

Expected: Build succeeds. If ScottPlot API methods don't resolve, check the installed version and consult the ScottPlot 5 Cookbook for the equivalent methods. Common adjustments:
- `Plot.Clear()` → `Plot.PlottableList.Clear()`
- `Plot.Axes.DateTimeTicksBottom()` → `Plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.DateTimeAutomatic()`
- `Plot.ShowLegend()` → `Plot.Legend.IsVisible = true`

---

## Task 10: BusMonitorViewModel + BusMonitorView

Orchestrates the three panels and database assignment bar.

**Files:**
- Create: `software/CanLinConfig/ViewModels/BusMonitorViewModel.cs`
- Create: `software/CanLinConfig/Views/BusMonitorView.xaml` + `.xaml.cs`

- [ ] **Step 1: Implement BusMonitorViewModel**

```csharp
// software/CanLinConfig/ViewModels/BusMonitorViewModel.cs
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanLinConfig.Models;
using CanLinConfig.Services;
using Microsoft.Win32;

namespace CanLinConfig.ViewModels;

public partial class BusMonitorViewModel : ObservableObject
{
    private readonly BusDataService _busDataService;
    private readonly Dispatcher _dispatcher;

    public TracePanelViewModel Trace { get; }
    public SignalPanelViewModel Signals { get; }
    public GraphPanelViewModel Graph { get; }

    // Database assignments (display paths)
    [ObservableProperty] private string _can1DbPath = "(none)";
    [ObservableProperty] private string _can2DbPath = "(none)";

    public BusMonitorViewModel(BusDataService busDataService)
    {
        _busDataService = busDataService;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        Trace = new TracePanelViewModel();
        Signals = new SignalPanelViewModel();
        Graph = new GraphPanelViewModel();

        // Wire events
        _busDataService.FrameReceived += OnFrameReceived;
        _busDataService.SignalsDecoded += OnSignalsDecoded;

        // Wire signal panel → graph panel
        Signals.AddToGraphRequested += OnAddToGraph;
    }

    private void OnFrameReceived(object? sender, BusFrame frame)
    {
        // BusDataService fires on the adapter RX thread.
        // Marshal to UI thread since panel VMs modify ObservableCollections.
        var msgName = _busDataService.DatabaseManager.GetMessageName(frame.SourceBus, frame.Id);
        _dispatcher.BeginInvoke(() => Trace.AddFrame(frame, msgName));
    }

    private void OnSignalsDecoded(object? sender, IReadOnlyList<SignalValue> signals)
    {
        _dispatcher.BeginInvoke(() =>
        {
            Signals.UpdateSignals(signals);
            Graph.OnSignalValues(signals);
        });
    }

    private void OnAddToGraph(object? sender, SignalEntry entry)
    {
        Graph.AddSignal(entry.MessageKey, entry.Name, entry.Unit);
    }

    [RelayCommand]
    private void AssignCan1Db()
    {
        var path = BrowseDbcFile();
        if (path == null) return;
        _busDataService.DatabaseManager.AssignDatabase(BusFrame.Bus.CAN1, path);
        Can1DbPath = System.IO.Path.GetFileName(path);
    }

    [RelayCommand]
    private void AssignCan2Db()
    {
        var path = BrowseDbcFile();
        if (path == null) return;
        _busDataService.DatabaseManager.AssignDatabase(BusFrame.Bus.CAN2, path);
        Can2DbPath = System.IO.Path.GetFileName(path);
    }

    [RelayCommand]
    private void ClearCan1Db()
    {
        _busDataService.DatabaseManager.RemoveDatabase(BusFrame.Bus.CAN1);
        Can1DbPath = "(none)";
    }

    [RelayCommand]
    private void ClearCan2Db()
    {
        _busDataService.DatabaseManager.RemoveDatabase(BusFrame.Bus.CAN2);
        Can2DbPath = "(none)";
    }

    private static string? BrowseDbcFile()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "DBC Files (*.dbc)|*.dbc|All Files (*.*)|*.*",
            Title = "Select CAN Database"
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public void Cleanup()
    {
        _busDataService.FrameReceived -= OnFrameReceived;
        _busDataService.SignalsDecoded -= OnSignalsDecoded;
    }
}
```

- [ ] **Step 2: Create BusMonitorView**

```xml
<!-- software/CanLinConfig/Views/BusMonitorView.xaml -->
<UserControl x:Class="CanLinConfig.Views.BusMonitorView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:local="clr-namespace:CanLinConfig.Views">
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <!-- Database assignment bar -->
        <StackPanel Grid.Row="0" Orientation="Horizontal" Margin="5">
            <TextBlock Text="CAN1 DB:" VerticalAlignment="Center" Margin="0,0,5,0"/>
            <TextBlock Text="{Binding Can1DbPath}" VerticalAlignment="Center"
                       Width="120" TextTrimming="CharacterEllipsis"/>
            <Button Content="..." Command="{Binding AssignCan1DbCommand}" Width="25" Margin="2,0"/>
            <Button Content="X" Command="{Binding ClearCan1DbCommand}" Width="25" Margin="0,0,15,0"/>

            <TextBlock Text="CAN2 DB:" VerticalAlignment="Center" Margin="0,0,5,0"/>
            <TextBlock Text="{Binding Can2DbPath}" VerticalAlignment="Center"
                       Width="120" TextTrimming="CharacterEllipsis"/>
            <Button Content="..." Command="{Binding AssignCan2DbCommand}" Width="25" Margin="2,0"/>
            <Button Content="X" Command="{Binding ClearCan2DbCommand}" Width="25"/>
        </StackPanel>

        <!-- Three panels in a grid with splitters -->
        <Grid Grid.Row="1">
            <Grid.RowDefinitions>
                <RowDefinition Height="*" MinHeight="100"/>
                <RowDefinition Height="5"/>
                <RowDefinition Height="200" MinHeight="80"/>
            </Grid.RowDefinitions>
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*" MinWidth="200"/>
                <ColumnDefinition Width="5"/>
                <ColumnDefinition Width="300" MinWidth="150"/>
            </Grid.ColumnDefinitions>

            <!-- Trace panel (top-left) -->
            <local:TracePanel Grid.Row="0" Grid.Column="0"
                              DataContext="{Binding Trace}"/>

            <GridSplitter Grid.Row="0" Grid.Column="1"
                          HorizontalAlignment="Stretch" VerticalAlignment="Stretch"/>

            <!-- Signal panel (top-right) -->
            <local:SignalPanel Grid.Row="0" Grid.Column="2"
                               DataContext="{Binding Signals}"/>

            <GridSplitter Grid.Row="1" Grid.Column="0" Grid.ColumnSpan="3"
                          HorizontalAlignment="Stretch" VerticalAlignment="Stretch"/>

            <!-- Graph panel (bottom, full width) -->
            <local:GraphPanel Grid.Row="2" Grid.Column="0" Grid.ColumnSpan="3"
                              DataContext="{Binding Graph}"/>
        </Grid>
    </Grid>
</UserControl>
```

```csharp
// software/CanLinConfig/Views/BusMonitorView.xaml.cs
using System.Windows.Controls;

namespace CanLinConfig.Views;

public partial class BusMonitorView : UserControl
{
    public BusMonitorView()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 3: Verify build**

```bash
cd software && dotnet build CanLinConfig/CanLinConfig.csproj
```

Expected: Build succeeds.

---

## Task 11: Wire Into MainViewModel + MainWindow

Connect BusDataService into the existing frame flow and add the Bus Monitor tab.

**Files:**
- Modify: `software/CanLinConfig/ViewModels/MainViewModel.cs`
- Modify: `software/CanLinConfig/ViewModels/DiagnosticsViewModel.cs`
- Modify: `software/CanLinConfig/Views/MainWindow.xaml`
- Modify: `software/CanLinConfig/Protocol/ProtocolConstants.cs`

- [ ] **Step 1: Add monitor CAN IDs to ProtocolConstants**

Add to `software/CanLinConfig/Protocol/ProtocolConstants.cs` after the `DiagSysHealthId` line:

```csharp
    // Bus Monitor CAN IDs
    public const uint MonitorHeaderId = 0x604;
    public const uint MonitorDataId = 0x605;
```

- [ ] **Step 2: Update MainViewModel — add BusDataService and BusMonitorViewModel**

In `software/CanLinConfig/ViewModels/MainViewModel.cs`:

Add fields:

```csharp
public BusDataService BusDataService { get; }
public BusMonitorViewModel BusMonitor { get; }
```

In constructor, create services:

```csharp
var dbManager = new DatabaseManager();
BusDataService = new BusDataService(dbManager);
BusMonitor = new BusMonitorViewModel(BusDataService);
```

In `ConnectAsync()`, change the frame routing from:

```csharp
_protocol.RawFrameReceived += (_, e) => Diagnostics.OnRawFrame(e.Frame);
```

to:

```csharp
_protocol.RawFrameReceived += (_, e) =>
{
    BusDataService.OnCanFrame(e.Frame);
    Diagnostics.OnRawFrame(e.Frame);
};
```

This routes frames through BusDataService (for the bus monitor) while keeping the existing diagnostics decode path intact.

- [ ] **Step 3: Add Bus Monitor tab to MainWindow.xaml**

In `software/CanLinConfig/Views/MainWindow.xaml`, add the new tab **before** the existing "Live Diagnostics" tab in the TabControl:

```xml
<TabItem Header="Bus Monitor">
    <views:BusMonitorView DataContext="{Binding BusMonitor}"/>
</TabItem>
```

The existing `MainWindow.xaml` already declares `xmlns:views="clr-namespace:CanLinConfig.Views"` — use that prefix to stay consistent with existing tabs.

- [ ] **Step 4: Verify build**

```bash
cd software && dotnet build CanLinConfig/CanLinConfig.csproj
```

Expected: Build succeeds.

- [ ] **Step 5: Run all tests**

```bash
cd software && dotnet test CanLinConfig.Tests -v n
```

Expected: All tests pass (SignalExtractor: 9, DatabaseManager: 6, BusDataService: 6, TracePanel: 4 = 25 tests).

---

## Task 12: Integration Smoke Test

Verify end-to-end that the bus monitor works with a simulated frame flow.

**Files:**
- Create: `software/CanLinConfig.Tests/IntegrationTests.cs`

- [ ] **Step 1: Write integration test**

```csharp
// software/CanLinConfig.Tests/IntegrationTests.cs
using CanLinConfig.Models;
using CanLinConfig.Services;
using CanLinConfig.ViewModels;

namespace CanLinConfig.Tests;

public class IntegrationTests
{
    [Fact]
    public void EndToEnd_frame_flows_through_pipeline()
    {
        // Setup
        var dbManager = new DatabaseManager();
        var testDbc = Path.Combine(AppContext.BaseDirectory, "TestData", "test.dbc");
        dbManager.AssignDatabase(BusFrame.Bus.CAN1, testDbc);

        var busData = new BusDataService(dbManager);
        var trace = new TracePanelViewModel();
        var signals = new SignalPanelViewModel();

        // Wire manually (BusMonitorViewModel does this in production)
        busData.FrameReceived += (_, f) =>
            trace.AddFrame(f, dbManager.GetMessageName(f.SourceBus, f.Id));
        busData.SignalsDecoded += (_, sv) =>
            signals.UpdateSignals(sv);

        // Simulate a CAN frame: EngineData (ID=256)
        // RPM raw=1000 (0x03E8 LE), Temp raw=200, EngineOn=1
        byte[] data = [0xE8, 0x03, 200, 0x01, 0, 0, 0, 0];
        var frame = new Adapters.CanFrame(256, data);

        // Act
        busData.OnCanFrame(frame);

        // Assert trace
        Assert.Single(trace.Entries);
        Assert.Equal("EngineData", trace.Entries[0].MessageName);
        Assert.Equal("CAN1", trace.Entries[0].Bus);

        // Assert signals
        Assert.Equal(3, signals.Signals.Count);
        var rpm = signals.Signals.First(s => s.Name == "EngineRPM");
        Assert.Equal(250.0, rpm.Value); // 1000 * 0.25
        Assert.Equal("rpm", rpm.Unit);
    }

    [Fact]
    public void EndToEnd_unknown_message_still_shows_in_trace()
    {
        var dbManager = new DatabaseManager();
        var busData = new BusDataService(dbManager);
        var trace = new TracePanelViewModel();

        busData.FrameReceived += (_, f) =>
            trace.AddFrame(f, dbManager.GetMessageName(f.SourceBus, f.Id));

        var frame = new Adapters.CanFrame(0x7FF, new byte[] { 0x01, 0x02 }, dlc: 2);
        busData.OnCanFrame(frame);

        Assert.Single(trace.Entries);
        Assert.Null(trace.Entries[0].MessageName);
    }
}
```

- [ ] **Step 2: Run all tests**

```bash
cd software && dotnet test CanLinConfig.Tests -v n
```

Expected: All tests pass (25 + 2 = 27 tests total).

---

## Summary

| Task | Component | Tests | Depends On |
|------|-----------|-------|------------|
| 1 | Test project setup | 0 | — |
| 2 | BusFrame model | 0 | — |
| 3 | SignalExtractor | 9 | 2 |
| 4 | DatabaseManager | 6 | 3 |
| 5 | BusDataService | 6 | 2, 4 |
| 6 | TracePanelViewModel | 4 | 2 |
| 7 | SignalPanelViewModel | 0 | 2 |
| 8 | GraphPanelViewModel + ScottPlot | 0 | 2 |
| 9 | XAML views (3 panels) | 0 | 6, 7, 8 |
| 10 | BusMonitorViewModel + view | 0 | 5, 9 |
| 11 | Wire into MainViewModel + MainWindow | 0 | 10 |
| 12 | Integration smoke test | 2 | 11 |

**Total: 12 tasks, 27 tests (actual: 121 .NET tests + 31 on-target tests after full feature expansion)**

**Testing completed 2026-03-24. All tests pass. 11 bugs found and fixed during testing.**
