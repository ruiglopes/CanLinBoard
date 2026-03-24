# Continuous Mode (Plan 5B) — Implementation Plan

> **STATUS: COMPLETE AND TESTED (2026-03-24)** — Continuous mode tested on-target as part of Phase 8 test suite. Auto-resume, gap markers, and wrap count all verified.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add continuous recording mode to the flash logger — ring buffer wraps indefinitely, auto-resumes on boot, and writes gap markers when frames are dropped during queue overflow.

**Architecture:** Continuous mode reuses the existing ring buffer wrap logic (already implemented in Plan 5A). The key additions are: (1) enabling LOG_MODE_CONTINUOUS in the mode validator, (2) auto-resuming recording on boot if mode was continuous, (3) tracking dropped frames and writing gap marker entries (bus=0xFF sentinel) when the queue overflows, and (4) adding a mode selector to the config tool. The ring buffer already wraps correctly — continuous mode is mostly a policy change, not a structural one.

**Tech Stack:** C (firmware, FreeRTOS), C# .NET 8 (config tool, WPF, CommunityToolkit.Mvvm)

**Spec:** `docs/superpowers/specs/2026-03-23-bus-monitor-logger-design.md` § 3
**Master tracker:** `docs/bus-monitor-logger-master-plan.md` (Plan 5B)
**Branch:** `feature/bus-monitor-foundation`

**Important:** Do NOT commit specs or plans. Only commit actual code changes, and only after the feature is completed and tested.

---

## File Map

### Firmware — Modified Files

| File | Changes |
|------|---------|
| `firmware/src/logger/flash_logger.h` | Uncomment `LOG_MODE_CONTINUOUS`, add `LOG_PARAM_DROP_COUNT` |
| `firmware/src/logger/flash_logger.c` | Enable continuous mode, auto-resume on boot, gap marker on queue overflow, drop counter |
| `firmware/src/config/config_handler.c` | Add `LOG_PARAM_DROP_COUNT` to READ_PARAM handler |

### Config Tool — Modified Files

| File | Changes |
|------|---------|
| `software/CanLinConfig/Protocol/ProtocolConstants.cs` | Add `LogModeContinuous`, `LogParamDropCount` |
| `software/CanLinConfig/ViewModels/LogControlViewModel.cs` | Add mode selector (Manual/Continuous), drop count display, read mode on refresh |
| `software/CanLinConfig/Views/LogControlPanel.xaml` | Add mode ComboBox and drop count display |
| `software/CanLinConfig/ViewModels/LogDownloadViewModel.cs` | Update `ParseLogEntries` to count gap markers and report them |
| `software/CanLinConfig.Tests/LogDownloadViewModelTests.cs` | Add test for gap marker counting |

---

## Task 1: Firmware — Enable Continuous Mode and Gap Markers

**Files:**
- Modify: `firmware/src/logger/flash_logger.h:57-58` (uncomment mode define, add param)
- Modify: `firmware/src/logger/flash_logger.c:190-201,241-249,252-263,295-297` (mode logic, auto-resume, gap markers, drop counter)

### Step-by-step:

- [ ] **Step 1: Update flash_logger.h**

Replace the commented-out mode define (line 58):
```c
/* LOG_MODE_CONTINUOUS = 1  (Plan 5B) */
```
with:
```c
#define LOG_MODE_CONTINUOUS     1
```

Add a new param after `LOG_PARAM_FLASH_ERRORS` (line 75):
```c
#define LOG_PARAM_DROP_COUNT    8   /* R, 4 bytes (sub=0/1 split) — frames dropped due to full queue */
```

Add a new accessor after `flash_logger_get_flash_errors` (line 113):
```c
uint32_t flash_logger_get_drop_count(void);
```

- [ ] **Step 2: Add drop counter and gap marker logic to flash_logger.c**

Add a new static variable after `s_page_buf_pos` (line 21):
```c
static volatile uint32_t s_drop_count;  /* frames dropped due to full queue */
```

Update `flash_logger_set_mode()` (line 297) to accept continuous mode:
```c
void     flash_logger_set_mode(uint8_t m)   { if (m <= LOG_MODE_CONTINUOUS) s_meta.mode = m; }
```

Add the drop count accessor after `flash_logger_get_flash_errors` (line 303):
```c
uint32_t flash_logger_get_drop_count(void)  { return s_drop_count; }
```

- [ ] **Step 3: Add gap marker write on queue overflow**

Replace `flash_logger_enqueue_frame()` (lines 241-250) with:

```c
void flash_logger_enqueue_frame(const void *gf_ptr)
{
    if (s_state != LOG_STATE_RECORDING) return;

    const gateway_frame_t *gf = (const gateway_frame_t *)gf_ptr;
    if (!passes_bus_filter((uint8_t)gf->source_bus)) return;

    /* Non-blocking send — track drops for gap marker */
    if (xQueueSend(s_log_queue, gf, 0) != pdTRUE) {
        s_drop_count++;
    }
}
```

In `flash_logger_task()`, after `write_entry(&entry);` (line 226), add gap marker logic. Replace the queue receive block (lines 215-227) with:

```c
        if (xQueueReceive(s_log_queue, &gf, pdMS_TO_TICKS(100)) == pdTRUE) {
            if (s_state != LOG_STATE_RECORDING) continue;

            /* Check if frames were dropped since last write — insert gap marker */
            if (s_drop_count > 0) {
                log_entry_t gap;
                memset(&gap, 0, sizeof(gap));
                gap.timestamp_ms = gf.timestamp - s_meta.start_timestamp;
                gap.bus = 0xFF;  /* Gap marker sentinel */
                gap.frame_id = s_drop_count;  /* Store drop count in frame_id */
                s_drop_count = 0;
                write_entry(&gap);
            }

            log_entry_t entry;
            entry.timestamp_ms = gf.timestamp - s_meta.start_timestamp;
            entry.frame_id = gf.frame.id;
            entry.bus = (uint8_t)gf.source_bus;
            entry.dlc = gf.frame.dlc;
            memcpy(entry.data, gf.frame.data, 8);
            entry.reserved = 0;

            write_entry(&entry);
        }
```

- [ ] **Step 4: Auto-resume continuous recording on boot**

In `flash_logger_init()` (lines 190-202), replace the unclean shutdown handling:

```c
void flash_logger_init(QueueHandle_t log_queue)
{
    s_log_queue = log_queue;
    s_state = LOG_STATE_IDLE;
    s_page_buf_pos = 0;
    s_drop_count = 0;
    load_metadata();

    if (s_meta.state == LOG_STATE_RECORDING) {
        if (s_meta.mode == LOG_MODE_CONTINUOUS) {
            /* Continuous mode — auto-resume recording after reboot */
            s_state = LOG_STATE_RECORDING;
            /* Don't reset start_timestamp — keep relative timing continuous */
        } else {
            /* Manual mode — unclean shutdown, reset to idle */
            s_meta.state = LOG_STATE_IDLE;
            save_metadata();
        }
    }
}
```

Also reset drop counter in `flash_logger_start()` — add `s_drop_count = 0;` after `s_page_buf_pos = 0;` (line 260).

And in `flash_logger_erase_all()` — add `s_drop_count = 0;` after `s_state = LOG_STATE_IDLE;` (line 290).

- [ ] **Step 5: Verify firmware compiles**

Run:
```bash
cd firmware && cmake --build build
```

- [ ] **Step 6: Commit**

```bash
git add firmware/src/logger/flash_logger.h firmware/src/logger/flash_logger.c
git commit -m "feat(firmware): add continuous recording mode with gap markers and auto-resume"
```

---

## Task 2: Firmware — Add DROP_COUNT to Config Protocol

**Files:**
- Modify: `firmware/src/config/config_handler.c` (add LOG_PARAM_DROP_COUNT case)

### Step-by-step:

- [ ] **Step 1: Add DROP_COUNT to READ_PARAM handler**

In `handle_read_param()`, in the `CFG_SECTION_LOG` case, after the `LOG_PARAM_FLASH_ERRORS` case, add:

```c
        case LOG_PARAM_DROP_COUNT: {
            uint32_t drops = flash_logger_get_drop_count();
            if (sub == 0) {
                payload[3] = (uint8_t)(drops);
                payload[4] = (uint8_t)(drops >> 8);
            } else {
                payload[3] = (uint8_t)(drops >> 16);
                payload[4] = (uint8_t)(drops >> 24);
            }
            plen = 5;
            break;
        }
```

- [ ] **Step 2: Verify firmware compiles**

Run:
```bash
cd firmware && cmake --build build
```

- [ ] **Step 3: Commit**

```bash
git add firmware/src/config/config_handler.c
git commit -m "feat(firmware): add LOG_PARAM_DROP_COUNT to config protocol"
```

---

## Task 3: Config Tool — Add Mode Selector and Drop Count

**Files:**
- Modify: `software/CanLinConfig/Protocol/ProtocolConstants.cs` (add constants)
- Modify: `software/CanLinConfig/ViewModels/LogControlViewModel.cs` (add mode, drop count)
- Modify: `software/CanLinConfig/Views/LogControlPanel.xaml` (add mode ComboBox, drop count)

### Step-by-step:

- [ ] **Step 1: Add constants to ProtocolConstants.cs**

After `LogModeManual` (around line 81), add:
```csharp
    public const byte LogModeContinuous = 1;
```

After `LogParamFlashErrors` (around line 79), add:
```csharp
    public const byte LogParamDropCount = 8;
```

- [ ] **Step 2: Update LogControlViewModel**

Add new properties after the existing fields:
```csharp
    [ObservableProperty] private int _selectedModeIndex; // 0=Manual, 1=Continuous
    [ObservableProperty] private uint _dropCount;
```

Add a property for mode names:
```csharp
    public string[] ModeNames { get; } = ["Manual", "Continuous"];
```

Add a partial method to send mode changes:
```csharp
    partial void OnSelectedModeIndexChanged(int value) => _ = SendModeAsync();

    private async Task SendModeAsync()
    {
        if (_protocol == null) return;
        await _protocol.WriteParamAsync(
            ProtocolConstants.SectionLog,
            ProtocolConstants.LogParamMode, 0,
            [(byte)SelectedModeIndex]);
    }
```

In `RefreshStatusAsync()`, after reading flash errors, add:
```csharp
        // Read drop count (32-bit, split)
        DropCount = await ReadUint32ParamAsync(ProtocolConstants.LogParamDropCount);

        // Read current mode
        var mode = await _protocol.ReadParamAsync(
            ProtocolConstants.SectionLog, ProtocolConstants.LogParamMode, 0);
        if (mode.Success && mode.Value.Length > 0)
        {
            _selectedModeIndex = mode.Value[0]; // Set backing field to avoid re-sending
            OnPropertyChanged(nameof(SelectedModeIndex));
        }
```

In `SetProtocol()`, reset mode index when disconnecting:
```csharp
        if (!IsConnected)
        {
            IsRecording = false;
            StatusText = "Disconnected";
            _selectedModeIndex = 0;
            OnPropertyChanged(nameof(SelectedModeIndex));
        }
```

Update the `StatusText` switch to show mode info for continuous:
```csharp
            StatusText = LoggerStatus switch
            {
                ProtocolConstants.LogStateIdle => "Idle",
                ProtocolConstants.LogStateRecording => SelectedModeIndex == 1 ? "Recording (Continuous)" : "Recording",
                ProtocolConstants.LogStateError => "Error — flash failures",
                _ => $"Unknown ({LoggerStatus})"
            };
```

- [ ] **Step 3: Update LogControlPanel.xaml**

Add a mode selector and drop count to the status row. After the `FlashErrors` TextBlock, add:
```xml
                <TextBlock Text="  Drops:" VerticalAlignment="Center" Margin="0,0,5,0"/>
                <TextBlock Text="{Binding DropCount}" VerticalAlignment="Center"/>
```

Add a mode selector row between the status row and the bus filter row:
```xml
            <StackPanel Orientation="Horizontal" Margin="0,0,0,8">
                <TextBlock Text="Mode:" VerticalAlignment="Center" Margin="0,0,5,0"/>
                <ComboBox ItemsSource="{Binding ModeNames}"
                          SelectedIndex="{Binding SelectedModeIndex}"
                          IsEnabled="{Binding IsConnected}"
                          Width="120" VerticalAlignment="Center"/>
            </StackPanel>
```

- [ ] **Step 4: Build the solution**

Run:
```bash
cd software && dotnet build CanLinConfig.sln
```

- [ ] **Step 5: Run all tests**

Run:
```bash
cd software && dotnet test CanLinConfig.Tests -v n
```

- [ ] **Step 6: Commit**

```bash
git add software/CanLinConfig/Protocol/ProtocolConstants.cs software/CanLinConfig/ViewModels/LogControlViewModel.cs software/CanLinConfig/Views/LogControlPanel.xaml
git commit -m "feat(config-tool): add continuous mode selector and drop count display"
```

---

## Task 4: Config Tool — Gap Marker Reporting in Download

**Files:**
- Modify: `software/CanLinConfig/ViewModels/LogDownloadViewModel.cs` (report gap count)
- Modify: `software/CanLinConfig.Tests/LogDownloadViewModelTests.cs` (add gap counting test)

### Step-by-step:

- [ ] **Step 1: Add test for gap marker counting**

In `LogDownloadViewModelTests.cs`, add:

```csharp
    [Fact]
    public void ParseLogEntries_reports_gap_marker_drop_count()
    {
        var data = new byte[40]; // 2 entries
        // Entry 0: normal frame, CAN1, ID=0x100
        data[4] = 0x00; data[5] = 0x01; data[8] = 0x00; data[9] = 0x03;

        // Entry 1: gap marker (bus = 0xFF, frame_id = drop count = 5)
        data[24] = 0x05; data[25] = 0x00; data[26] = 0x00; data[27] = 0x00; // frame_id = 5
        data[28] = 0xFF; // bus = gap marker

        var (entries, gapCount) = LogDownloadViewModel.ParseLogEntriesWithGaps(data);

        Assert.Single(entries); // gap marker not included as entry
        Assert.Equal(5u, gapCount); // 5 frames were dropped
    }
```

- [ ] **Step 2: Run test — verify it fails**

Run:
```bash
cd software && dotnet test CanLinConfig.Tests --filter "gap_marker_drop" -v n
```
Expected: Compile error (ParseLogEntriesWithGaps doesn't exist).

- [ ] **Step 3: Add ParseLogEntriesWithGaps method**

In `LogDownloadViewModel.cs`, add alongside `ParseLogEntries`:

```csharp
    public static (List<LogEntry> Entries, uint GapDropCount) ParseLogEntriesWithGaps(byte[] data)
    {
        var entries = new List<LogEntry>();
        uint gapDrops = 0;
        const int entrySize = 20;

        for (int i = 0; i + entrySize <= data.Length; i += entrySize)
        {
            byte bus = data[i + 8];

            // Gap marker: bus = 0xFF, frame_id contains drop count
            if (bus == 0xFF)
            {
                uint dropCount = (uint)(data[i + 4] | (data[i + 5] << 8)
                                | (data[i + 6] << 16) | (data[i + 7] << 24));
                gapDrops += dropCount;
                continue;
            }

            // Skip erased flash
            if (data[i] == 0xFF && data[i + 1] == 0xFF &&
                data[i + 2] == 0xFF && data[i + 3] == 0xFF)
                continue;

            uint timestampMs = (uint)(data[i] | (data[i + 1] << 8)
                             | (data[i + 2] << 16) | (data[i + 3] << 24));
            uint frameId = (uint)(data[i + 4] | (data[i + 5] << 8)
                          | (data[i + 6] << 16) | (data[i + 7] << 24));
            byte dlc = data[i + 9];
            var payload = new byte[8];
            Array.Copy(data, i + 10, payload, 0, 8);

            entries.Add(new LogEntry(timestampMs, frameId, bus, dlc, payload));
        }

        return (entries, gapDrops);
    }
```

Update the `Download()` method to use `ParseLogEntriesWithGaps` instead of `ParseLogEntries`:

Replace:
```csharp
            _downloadedLog = ParseLogEntries(allData.ToArray());
            DownloadedEntries = _downloadedLog.Count;
            HasDownloadedData = _downloadedLog.Count > 0;
            DownloadStatus = $"Complete — {_downloadedLog.Count} entries";
```

With:
```csharp
            var (entries, gapDrops) = ParseLogEntriesWithGaps(allData.ToArray());
            _downloadedLog = entries;
            DownloadedEntries = _downloadedLog.Count;
            HasDownloadedData = _downloadedLog.Count > 0;
            DownloadStatus = gapDrops > 0
                ? $"Complete — {_downloadedLog.Count} entries ({gapDrops} frames dropped)"
                : $"Complete — {_downloadedLog.Count} entries";
```

- [ ] **Step 4: Run tests — verify they pass**

Run:
```bash
cd software && dotnet test CanLinConfig.Tests --filter "LogDownloadViewModel" -v n
```
Expected: All tests pass (existing + new gap marker test).

- [ ] **Step 5: Commit**

```bash
git add software/CanLinConfig/ViewModels/LogDownloadViewModel.cs software/CanLinConfig.Tests/LogDownloadViewModelTests.cs
git commit -m "feat(config-tool): add gap marker reporting in log download"
```

---

## Task 5: Update Master Plan

**Files:**
- Modify: `docs/bus-monitor-logger-master-plan.md` (update Plan 5B status)

### Step-by-step:

- [ ] **Step 1: Update master plan**

In `docs/bus-monitor-logger-master-plan.md`, update Plan 5B section status from `NOT STARTED` to `COMPLETE` and fill in details.

- [ ] **Step 2: Verify firmware builds**

Run:
```bash
cd firmware && cmake --build build
```

- [ ] **Step 3: Run all config tool tests**

Run:
```bash
cd software && dotnet test CanLinConfig.Tests -v n
```

- [ ] **Step 4: Commit**

```bash
git add docs/bus-monitor-logger-master-plan.md
git commit -m "docs: update master plan — Plan 5B continuous mode complete"
```

---

## Testing Notes

### Config tool (can test now)
- ParseLogEntriesWithGaps: unit test covers gap marker drop count extraction
- Existing ParseLogEntries tests still pass (gap markers already skipped)
- Mode selector UI manually verifiable

### Firmware (deferred — requires hardware)
- Set mode to continuous, start recording, verify ring wraps
- Reboot while continuous recording — verify auto-resume
- Flood queue to trigger drops — verify gap marker entries in flash
- Download log with gap markers — verify config tool reports drop count
- Set mode back to manual — verify no auto-resume on reboot
- Drop counter readable via config protocol
