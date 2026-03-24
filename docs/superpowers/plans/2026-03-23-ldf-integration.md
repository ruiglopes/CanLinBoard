# LDF Parser Integration — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Integrate the existing LDF parser with the DatabaseManager and Bus Monitor so LIN bus traffic can be decoded with signal names and physical values, just like CAN traffic with DBC files.

**Architecture:** The existing `LdfParser` already parses LDF files into `LdfFrame`/`LdfSignal` models. This plan adds a conversion layer that maps LDF frame signals into `DbcSignal`-compatible entries (reusing the same `SignalExtractor`), extends `DatabaseManager.AssignDatabase()` to auto-detect `.ldf` files, and adds LIN1-4 database assignment buttons to the Bus Monitor tab.

**Tech Stack:** .NET 8, WPF, existing LdfParser + DbcParser

**Spec:** `docs/superpowers/specs/2026-03-23-bus-monitor-logger-design.md` (Section 6 — LdfParser)

**Important:** Do NOT commit specs or plans. Only commit actual code changes, and only after the feature is completed and tested.

---

## File Map

### New Files

| File | Responsibility |
|------|---------------|
| `software/CanLinConfig.Tests/LdfIntegrationTests.cs` | LDF → DatabaseManager → signal decode tests |
| `software/CanLinConfig.Tests/TestData/test.ldf` | Minimal test LDF fixture |

### Modified Files

| File | Changes |
|------|---------|
| `software/CanLinConfig/Services/DatabaseManager.cs` | Auto-detect .ldf vs .dbc, convert LDF signals to DbcSignal for cache |
| `software/CanLinConfig/ViewModels/BusMonitorViewModel.cs` | Add LIN1-4 database assignment commands + display paths |
| `software/CanLinConfig/Views/BusMonitorView.xaml` | Add LIN database assignment buttons in database bar |

---

## Task 1: Test LDF Fixture + LDF-to-DbcSignal Conversion (TDD)

**Files:**
- Create: `software/CanLinConfig.Tests/TestData/test.ldf`
- Create: `software/CanLinConfig.Tests/LdfIntegrationTests.cs`
- Modify: `software/CanLinConfig/Services/DatabaseManager.cs`

- [ ] **Step 1: Create test LDF file**

```ldf
// software/CanLinConfig.Tests/TestData/test.ldf
LIN_description_file;
LIN_protocol_version = "2.1";
LIN_language_version = "2.1";
LIN_speed = 19.2 kbps;

Nodes {
    Master: ECU_Master, 5 ms, 0.1 ms;
    Slaves: Motor1;
}

Signals {
    MotorSpeed: 8, 0, Motor1, ECU_Master;
    MotorTemp: 8, 0, Motor1, ECU_Master;
    MotorCommand: 8, 0, ECU_Master, Motor1;
}

Frames {
    MotorStatus: 33, Motor1, 4 {
        MotorSpeed, 0;
        MotorTemp, 8;
    }
    MotorControl: 34, ECU_Master, 2 {
        MotorCommand, 0;
    }
}

Schedule_tables {
    MainSchedule {
        MotorControl delay 10 ms;
        MotorStatus delay 10 ms;
    }
}

Signal_encoding_types {
    SpeedEncoding {
        physical_value, 0, 255, 100, 0, "rpm";
    }
    TempEncoding {
        physical_value, 0, 255, 1, -40, "C";
    }
    CmdEncoding {
        logical_value, 0, "Off";
        logical_value, 1, "On";
        logical_value, 2, "Fast";
    }
}

Signal_representation {
    SpeedEncoding: MotorSpeed;
    TempEncoding: MotorTemp;
    CmdEncoding: MotorCommand;
}
```

Add to test project `.csproj`:
```xml
<None Update="TestData\test.ldf">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</None>
```

- [ ] **Step 2: Write failing tests**

```csharp
// software/CanLinConfig.Tests/LdfIntegrationTests.cs
using CanLinConfig.Models;
using CanLinConfig.Services;

namespace CanLinConfig.Tests;

public class LdfIntegrationTests
{
    private static string TestLdfPath =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "test.ldf");

    [Fact]
    public void AssignDatabase_ldf_loads_frames_as_messages()
    {
        var mgr = new DatabaseManager();
        mgr.AssignDatabase(BusFrame.Bus.LIN1, TestLdfPath);

        // LDF has 2 frames: MotorStatus (ID 33) and MotorControl (ID 34)
        Assert.Equal("MotorStatus", mgr.GetMessageName(BusFrame.Bus.LIN1, 33));
        Assert.Equal("MotorControl", mgr.GetMessageName(BusFrame.Bus.LIN1, 34));
        Assert.Null(mgr.GetMessageName(BusFrame.Bus.LIN1, 99));
    }

    [Fact]
    public void AssignDatabase_ldf_converts_signals()
    {
        var mgr = new DatabaseManager();
        mgr.AssignDatabase(BusFrame.Bus.LIN1, TestLdfPath);

        var signals = mgr.GetSignals(BusFrame.Bus.LIN1, 33);
        Assert.Equal(2, signals.Count);
        Assert.Contains(signals, s => s.Name == "MotorSpeed");
        Assert.Contains(signals, s => s.Name == "MotorTemp");

        // Verify signal properties from encoding
        var speed = signals.First(s => s.Name == "MotorSpeed");
        Assert.Equal(0, speed.StartBit);
        Assert.Equal(8, speed.BitLength);
        Assert.Equal(100.0, speed.Factor);
        Assert.Equal(0.0, speed.Offset);
        Assert.Equal("rpm", speed.Unit);
    }

    [Fact]
    public void DecodeFrame_ldf_extracts_physical_values()
    {
        var mgr = new DatabaseManager();
        mgr.AssignDatabase(BusFrame.Bus.LIN1, TestLdfPath);

        // MotorStatus frame (ID 33): MotorSpeed at byte 0, MotorTemp at byte 1
        // Speed raw=25 → physical = 25 * 100 + 0 = 2500 rpm
        // Temp raw=200 → physical = 200 * 1 + (-40) = 160 C
        byte[] data = [25, 200, 0, 0, 0, 0, 0, 0];
        var frame = new BusFrame(BusFrame.Bus.LIN1, 33, 4, data, DateTime.Now);
        var values = mgr.DecodeFrame(frame);

        Assert.Equal(2, values.Count);
        var speed = values.First(v => v.Name == "MotorSpeed");
        Assert.Equal(2500.0, speed.PhysicalValue);
        Assert.Equal("rpm", speed.Unit);

        var temp = values.First(v => v.Name == "MotorTemp");
        Assert.Equal(160.0, temp.PhysicalValue);
    }

    [Fact]
    public void AssignDatabase_autodetects_dbc_vs_ldf()
    {
        var mgr = new DatabaseManager();
        var testDbc = Path.Combine(AppContext.BaseDirectory, "TestData", "test.dbc");

        // DBC for CAN bus
        mgr.AssignDatabase(BusFrame.Bus.CAN1, testDbc);
        Assert.Equal("EngineData", mgr.GetMessageName(BusFrame.Bus.CAN1, 256));

        // LDF for LIN bus
        mgr.AssignDatabase(BusFrame.Bus.LIN1, TestLdfPath);
        Assert.Equal("MotorStatus", mgr.GetMessageName(BusFrame.Bus.LIN1, 33));
    }

    [Fact]
    public void RemoveDatabase_ldf_clears_cache()
    {
        var mgr = new DatabaseManager();
        mgr.AssignDatabase(BusFrame.Bus.LIN1, TestLdfPath);
        mgr.RemoveDatabase(BusFrame.Bus.LIN1);
        Assert.Empty(mgr.GetSignals(BusFrame.Bus.LIN1, 33));
        Assert.Null(mgr.GetMessageName(BusFrame.Bus.LIN1, 33));
    }

    [Fact]
    public void Signal_without_encoding_gets_default_factor_offset()
    {
        // If an LDF signal has no encoding, factor=1 offset=0 unit=""
        // MotorControl frame has MotorCommand with CmdEncoding (logical only, no physical)
        var mgr = new DatabaseManager();
        mgr.AssignDatabase(BusFrame.Bus.LIN1, TestLdfPath);

        var signals = mgr.GetSignals(BusFrame.Bus.LIN1, 34);
        Assert.Single(signals);
        var cmd = signals[0];
        Assert.Equal("MotorCommand", cmd.Name);
        // CmdEncoding is logical-only (no physical_value), so defaults apply
        Assert.Equal(1.0, cmd.Factor);
        Assert.Equal(0.0, cmd.Offset);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

```bash
cd software && dotnet test CanLinConfig.Tests --filter "LdfIntegrationTests" -v n
```

- [ ] **Step 4: Extend DatabaseManager to handle LDF files**

Modify `software/CanLinConfig/Services/DatabaseManager.cs`:

Change `AssignDatabase` to auto-detect file type by extension:

```csharp
public void AssignDatabase(BusFrame.Bus bus, string filePath)
{
    var ext = Path.GetExtension(filePath).ToLowerInvariant();
    if (ext == ".ldf")
        AssignLdfDatabase(bus, filePath);
    else
        AssignDbcDatabase(bus, filePath);
}

private void AssignDbcDatabase(BusFrame.Bus bus, string filePath)
{
    var dbc = DbcParser.Parse(filePath);
    lock (_lock)
    {
        _dbPaths[bus] = filePath;
        RebuildCacheFromDbc(bus, dbc);
    }
}

private void AssignLdfDatabase(BusFrame.Bus bus, string filePath)
{
    var ldf = LdfParser.Parse(filePath);
    lock (_lock)
    {
        _dbPaths[bus] = filePath;
        RebuildCacheFromLdf(bus, ldf);
    }
}
```

Add the LDF-to-cache conversion method:

```csharp
private void RebuildCacheFromLdf(BusFrame.Bus bus, LdfFile ldf)
{
    ClearCache(bus);
    foreach (var frame in ldf.Frames)
    {
        var signals = new List<DbcSignal>();
        foreach (var frameSig in frame.Signals)
        {
            var ldfSig = LdfParser.GetSignal(ldf, frameSig.Name);
            if (ldfSig == null) continue;

            var encoding = LdfParser.GetEncodingForSignal(ldf, frameSig.Name);
            var physEncoding = encoding?.Values.FirstOrDefault(v => v.IsPhysical);

            signals.Add(new DbcSignal
            {
                Name = frameSig.Name,
                StartBit = frameSig.BitOffset,
                BitLength = ldfSig.BitSize,
                IsLittleEndian = true, // LIN is always LSB-first
                IsSigned = false,
                Factor = physEncoding?.Factor ?? 1.0,
                Offset = physEncoding?.Offset ?? 0.0,
                MinValue = physEncoding?.RawValue ?? 0,
                MaxValue = physEncoding?.RawMax ?? ((1 << ldfSig.BitSize) - 1),
                Unit = physEncoding?.Description ?? ""
            });
        }

        _signalCache[(bus, frame.Id)] = signals;
        _nameCache[(bus, frame.Id)] = frame.Name;
    }
}
```

Rename the existing `RebuildCache` to `RebuildCacheFromDbc` for clarity. Remove the old `_databases` dictionary (it stored `DbcFile` but was never read after cache build).

- [ ] **Step 5: Run tests to verify they pass**

```bash
cd software && dotnet test CanLinConfig.Tests --filter "LdfIntegrationTests" -v n
```

Expected: 6 tests PASS.

- [ ] **Step 6: Run all tests**

```bash
cd software && dotnet test CanLinConfig.Tests -v quiet
```

Expected: All 39 existing + 6 new = 45 tests PASS.

- [ ] **Step 7: Commit**

```
feat: extend DatabaseManager with LDF support for LIN signal decoding
```

---

## Task 2: LIN Database Assignment UI

Add LIN1-4 database file pickers to the Bus Monitor tab.

**Files:**
- Modify: `software/CanLinConfig/ViewModels/BusMonitorViewModel.cs`
- Modify: `software/CanLinConfig/Views/BusMonitorView.xaml`

- [ ] **Step 1: Add LIN database properties and commands to BusMonitorViewModel**

Add to `BusMonitorViewModel.cs`:

```csharp
[ObservableProperty] private string _lin1DbPath = "(none)";
[ObservableProperty] private string _lin2DbPath = "(none)";
[ObservableProperty] private string _lin3DbPath = "(none)";
[ObservableProperty] private string _lin4DbPath = "(none)";
```

Add assign/clear commands for each LIN bus (follow the existing CAN pattern):

```csharp
[RelayCommand]
private void AssignLin1Db() => AssignLinDb(BusFrame.Bus.LIN1, v => Lin1DbPath = v);
[RelayCommand]
private void ClearLin1Db() => ClearLinDb(BusFrame.Bus.LIN1, () => Lin1DbPath = "(none)");
// ... repeat for LIN2, LIN3, LIN4

private void AssignLinDb(BusFrame.Bus bus, Action<string> setPath)
{
    var path = BrowseDbFile();
    if (path == null) return;
    _busDataService.DatabaseManager.AssignDatabase(bus, path);
    setPath(System.IO.Path.GetFileName(path));
}

private void ClearLinDb(BusFrame.Bus bus, Action resetPath)
{
    _busDataService.DatabaseManager.RemoveDatabase(bus);
    resetPath();
}
```

Also update the existing `BrowseDbcFile()` to accept both DBC and LDF:

```csharp
private static string? BrowseDbFile()
{
    var dlg = new OpenFileDialog
    {
        Filter = "Database Files (*.dbc;*.ldf)|*.dbc;*.ldf|DBC Files (*.dbc)|*.dbc|LDF Files (*.ldf)|*.ldf|All Files (*.*)|*.*",
        Title = "Select Bus Database"
    };
    return dlg.ShowDialog() == true ? dlg.FileName : null;
}
```

Rename existing `BrowseDbcFile()` references to use `BrowseDbFile()`.

- [ ] **Step 2: Add LIN database buttons to BusMonitorView.xaml**

Read `software/CanLinConfig/Views/BusMonitorView.xaml`. The database bar is a `StackPanel` with CAN1/CAN2 buttons. Add a second row or extend the same row with LIN1-4. To avoid overcrowding, use a WrapPanel or two-row layout:

```xml
<!-- Replace the single StackPanel database bar with a two-row layout -->
<StackPanel Grid.Row="0" Margin="5">
    <StackPanel Orientation="Horizontal" Margin="0,2">
        <TextBlock Text="CAN1:" VerticalAlignment="Center" Width="40"/>
        <TextBlock Text="{Binding Can1DbPath}" VerticalAlignment="Center" Width="120" TextTrimming="CharacterEllipsis"/>
        <Button Content="..." Command="{Binding AssignCan1DbCommand}" Width="25" Margin="2,0"/>
        <Button Content="X" Command="{Binding ClearCan1DbCommand}" Width="25" Margin="0,0,15,0"/>
        <TextBlock Text="CAN2:" VerticalAlignment="Center" Width="40"/>
        <TextBlock Text="{Binding Can2DbPath}" VerticalAlignment="Center" Width="120" TextTrimming="CharacterEllipsis"/>
        <Button Content="..." Command="{Binding AssignCan2DbCommand}" Width="25" Margin="2,0"/>
        <Button Content="X" Command="{Binding ClearCan2DbCommand}" Width="25"/>
    </StackPanel>
    <StackPanel Orientation="Horizontal" Margin="0,2">
        <TextBlock Text="LIN1:" VerticalAlignment="Center" Width="40"/>
        <TextBlock Text="{Binding Lin1DbPath}" VerticalAlignment="Center" Width="120" TextTrimming="CharacterEllipsis"/>
        <Button Content="..." Command="{Binding AssignLin1DbCommand}" Width="25" Margin="2,0"/>
        <Button Content="X" Command="{Binding ClearLin1DbCommand}" Width="25" Margin="0,0,15,0"/>
        <TextBlock Text="LIN2:" VerticalAlignment="Center" Width="40"/>
        <TextBlock Text="{Binding Lin2DbPath}" VerticalAlignment="Center" Width="120" TextTrimming="CharacterEllipsis"/>
        <Button Content="..." Command="{Binding AssignLin2DbCommand}" Width="25" Margin="2,0"/>
        <Button Content="X" Command="{Binding ClearLin2DbCommand}" Width="25" Margin="0,0,15,0"/>
        <TextBlock Text="LIN3:" VerticalAlignment="Center" Width="40"/>
        <TextBlock Text="{Binding Lin3DbPath}" VerticalAlignment="Center" Width="120" TextTrimming="CharacterEllipsis"/>
        <Button Content="..." Command="{Binding AssignLin3DbCommand}" Width="25" Margin="2,0"/>
        <Button Content="X" Command="{Binding ClearLin3DbCommand}" Width="25" Margin="0,0,15,0"/>
        <TextBlock Text="LIN4:" VerticalAlignment="Center" Width="40"/>
        <TextBlock Text="{Binding Lin4DbPath}" VerticalAlignment="Center" Width="120" TextTrimming="CharacterEllipsis"/>
        <Button Content="..." Command="{Binding AssignLin4DbCommand}" Width="25" Margin="2,0"/>
        <Button Content="X" Command="{Binding ClearLin4DbCommand}" Width="25"/>
    </StackPanel>
</StackPanel>
```

- [ ] **Step 3: Verify build and all tests pass**

```bash
cd software && dotnet build CanLinConfig.sln && dotnet test CanLinConfig.Tests -v quiet
```

Expected: Build succeeds, 45 tests pass.

- [ ] **Step 4: Commit**

```
feat: add LIN database assignment to Bus Monitor tab
```

---

## Task 3: Update Project System for LIN databases

The ProjectState and MainViewModel already have Lin1-4 database path fields. Verify they work with the new LDF assignment by updating `ApplyState` in MainViewModel.

**Files:**
- Modify: `software/CanLinConfig/ViewModels/MainViewModel.cs` (if needed — check ApplyState handles LIN paths)
- Modify: `software/CanLinConfig/ViewModels/BusMonitorViewModel.cs` (if needed — verify LIN paths are captured)

- [ ] **Step 1: Read MainViewModel.cs CaptureCurrentState() and ApplyState()**

Verify that `CaptureCurrentState()` reads `GetDatabasePath(BusFrame.Bus.LIN1)` through `LIN4`, and `ApplyState()` assigns LIN databases and sets BusMonitor LIN display paths. If not, add the missing lines following the CAN1/CAN2 pattern.

- [ ] **Step 2: Verify build and tests**

```bash
cd software && dotnet build CanLinConfig.sln && dotnet test CanLinConfig.Tests -v quiet
```

- [ ] **Step 3: Commit (if changes were needed)**

```
feat: wire LIN database paths through project system
```

---

## Task 4: Test Guide Update

- [ ] **Step 1: Add LDF tests to `software/TEST_GUIDE.md`**

Add a section "LDF Integration Tests (Plan 3)" with:
- Automated test table (6 LdfIntegrationTests)
- Manual test LDF-1: Assign LDF to LIN1 bus in Bus Monitor tab
- Manual test LDF-2: Verify LIN signals decode after LDF assignment (requires firmware monitor — note as future test)
- Manual test LDF-3: LDF file persisted in project save/load

---

## Summary

| Task | Component | Tests | Depends On |
|------|-----------|-------|------------|
| 1 | DatabaseManager LDF support + tests | 6 | — |
| 2 | LIN database assignment UI | 0 | 1 |
| 3 | Project system LIN paths | 0 | 1, 2 |
| 4 | Test guide update | 0 | 1-3 |

**Total: 4 tasks, 6 new tests**

Note: The existing `LdfParser` was already implemented (381 lines, full LDF parsing with signal encodings). This plan only adds the integration layer to make it work with the bus monitor pipeline.
