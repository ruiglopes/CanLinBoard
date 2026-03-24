# Triggered Mode (Plan 5C) — Implementation Plan

> **STATUS: COMPLETE AND TESTED (2026-03-24)** — Triggered mode tested on-target as part of Phase 8 test suite. Arm, trigger, pre/post capture window, and stop-while-armed all verified.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add triggered recording mode — arm the logger, it records continuously until a trigger condition fires, then captures `post_trigger_kb` more data and stops automatically, preserving `pre_trigger_kb` of pre-trigger context.

**Architecture:** Triggered mode reuses the existing ring buffer. When armed, the logger records continuously (ring wraps). The trigger check runs inline in `flash_logger_task()` for each entry. When the condition matches, the logger records the trigger offset, continues for `post_trigger_kb` more bytes, then auto-stops. The config tool calculates the download window: from `(trigger_offset - pre_trigger_kb)` to `(trigger_offset + post_trigger_kb)`, wrapping around the ring. Trigger parameters are stored in the existing reserved fields of `log_metadata_t`.

**Tech Stack:** C (firmware, FreeRTOS), C# .NET 8 (config tool, WPF, CommunityToolkit.Mvvm, xUnit)

**Spec:** `docs/superpowers/specs/2026-03-23-bus-monitor-logger-design.md` § 3
**Master tracker:** `docs/bus-monitor-logger-master-plan.md` (Plan 5C)
**Branch:** `feature/bus-monitor-foundation`

**Important:** Do NOT commit specs or plans. Only commit actual code changes, and only after the feature is completed and tested.

---

## File Map

### Firmware — Modified Files

| File | Changes |
|------|---------|
| `firmware/src/logger/flash_logger.h` | Add `LOG_MODE_TRIGGERED`, `LOG_STATE_ARMED`/`CAPTURING`, trigger params, arm/trigger API |
| `firmware/src/logger/flash_logger.c` | Trigger check logic, armed→capturing→idle state machine, post-trigger byte tracking |
| `firmware/src/config/config_handler.c` | Add trigger param READ/WRITE handlers, arm state command |

### Config Tool — Modified Files

| File | Changes |
|------|---------|
| `software/CanLinConfig/Protocol/ProtocolConstants.cs` | Add triggered mode constants and trigger params |
| `software/CanLinConfig/ViewModels/LogControlViewModel.cs` | Add trigger config UI properties, arm command |
| `software/CanLinConfig/Views/LogControlPanel.xaml` | Add trigger config panel (visible when Triggered mode selected) |

---

## Task 1: Firmware — Triggered Mode State Machine

**Files:**
- Modify: `firmware/src/logger/flash_logger.h`
- Modify: `firmware/src/logger/flash_logger.c`

### Step-by-step:

- [ ] **Step 1: Update flash_logger.h**

Replace the commented-out triggered mode define (line 59):
```c
/* LOG_MODE_TRIGGERED  = 2  (Plan 5C) */
```
with:
```c
#define LOG_MODE_TRIGGERED      2
```

Add new states after `LOG_STATE_RECORDING` (line 52):
```c
#define LOG_STATE_ARMED         2
#define LOG_STATE_CAPTURING     3
```

Update the reserved trigger field comments in `log_metadata_t` (lines 36-43) — remove the "reserved (Plan 5C)" comments:
```c
    uint32_t trigger_id;      /* CAN/LIN ID to match */
    uint8_t  trigger_bus;     /* bus to watch (0-5) */
    uint8_t  trigger_byte;    /* data byte index to compare (0-7) */
    uint8_t  trigger_op;      /* 0=any, 1=equals, 2=gt, 3=lt, 4=mask */
    uint8_t  trigger_value;   /* comparison value */
    uint32_t pre_trigger_kb;  /* KB of pre-trigger data to retain */
    uint32_t post_trigger_kb; /* KB of post-trigger data to capture */
```

Add trigger operator defines after the mode defines:
```c
/* ---- Trigger Operators ---- */
#define LOG_TRIGGER_OP_ANY      0   /* any frame on trigger_bus with trigger_id */
#define LOG_TRIGGER_OP_EQ       1   /* data[trigger_byte] == trigger_value */
#define LOG_TRIGGER_OP_GT       2   /* data[trigger_byte] > trigger_value */
#define LOG_TRIGGER_OP_LT       3   /* data[trigger_byte] < trigger_value */
#define LOG_TRIGGER_OP_MASK     4   /* (data[trigger_byte] & trigger_value) != 0 */
```

Add new config protocol params after `LOG_PARAM_DROP_COUNT` (line 76):
```c
#define LOG_PARAM_TRIGGER_BUS   9   /* R/W, 1 byte */
#define LOG_PARAM_TRIGGER_ID    10  /* R/W, 4 bytes (sub=0/1 split) */
#define LOG_PARAM_TRIGGER_BYTE  11  /* R/W, 1 byte */
#define LOG_PARAM_TRIGGER_OP    12  /* R/W, 1 byte */
#define LOG_PARAM_TRIGGER_VALUE 13  /* R/W, 1 byte */
#define LOG_PARAM_PRE_TRIG_KB   14  /* R/W, 2 bytes */
#define LOG_PARAM_POST_TRIG_KB  15  /* R/W, 2 bytes */
```

Add new control API:
```c
void flash_logger_arm(void);
```

Add trigger config accessors:
```c
void     flash_logger_set_trigger_bus(uint8_t bus);
void     flash_logger_set_trigger_id(uint32_t id);
void     flash_logger_set_trigger_byte(uint8_t idx);
void     flash_logger_set_trigger_op(uint8_t op);
void     flash_logger_set_trigger_value(uint8_t val);
void     flash_logger_set_pre_trigger_kb(uint16_t kb);
void     flash_logger_set_post_trigger_kb(uint16_t kb);
uint8_t  flash_logger_get_trigger_bus(void);
uint32_t flash_logger_get_trigger_id(void);
uint8_t  flash_logger_get_trigger_byte(void);
uint8_t  flash_logger_get_trigger_op(void);
uint8_t  flash_logger_get_trigger_value(void);
uint16_t flash_logger_get_pre_trigger_kb(void);
uint16_t flash_logger_get_post_trigger_kb(void);
```

- [ ] **Step 2: Add trigger state machine to flash_logger.c**

Add new static variables after `s_drop_count` (line 22):
```c
static uint32_t s_trigger_offset;   /* write_offset when trigger fired */
static uint32_t s_post_trigger_remaining; /* bytes left to capture after trigger */
```

Add a trigger check function before `flash_logger_init`:
```c
static bool check_trigger(const log_entry_t *entry)
{
    /* Only check on the configured bus */
    if (entry->bus != s_meta.trigger_bus) return false;
    if (entry->frame_id != s_meta.trigger_id) return false;

    switch (s_meta.trigger_op) {
    case LOG_TRIGGER_OP_ANY:
        return true;
    case LOG_TRIGGER_OP_EQ:
        if (s_meta.trigger_byte >= entry->dlc) return false;
        return entry->data[s_meta.trigger_byte] == s_meta.trigger_value;
    case LOG_TRIGGER_OP_GT:
        if (s_meta.trigger_byte >= entry->dlc) return false;
        return entry->data[s_meta.trigger_byte] > s_meta.trigger_value;
    case LOG_TRIGGER_OP_LT:
        if (s_meta.trigger_byte >= entry->dlc) return false;
        return entry->data[s_meta.trigger_byte] < s_meta.trigger_value;
    case LOG_TRIGGER_OP_MASK:
        if (s_meta.trigger_byte >= entry->dlc) return false;
        return (entry->data[s_meta.trigger_byte] & s_meta.trigger_value) != 0;
    default:
        return false;
    }
}
```

Update `flash_logger_task()` — replace the state check and entry write block (lines 223-246) with:
```c
        if (xQueueReceive(s_log_queue, &gf, pdMS_TO_TICKS(100)) == pdTRUE) {
            uint8_t state = s_state;
            if (state != LOG_STATE_RECORDING && state != LOG_STATE_ARMED &&
                state != LOG_STATE_CAPTURING)
                continue;

            /* Check if frames were dropped since last write — insert gap marker */
            if (s_drop_count > 0) {
                log_entry_t gap;
                memset(&gap, 0, sizeof(gap));
                gap.timestamp_ms = gf.timestamp - s_meta.start_timestamp;
                gap.bus = 0xFF;
                gap.frame_id = s_drop_count;
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

            /* Triggered mode state machine */
            if (state == LOG_STATE_ARMED) {
                if (check_trigger(&entry)) {
                    s_trigger_offset = s_meta.write_offset;
                    s_post_trigger_remaining = s_meta.post_trigger_kb * 1024;
                    s_state = LOG_STATE_CAPTURING;
                    s_meta.state = LOG_STATE_CAPTURING;
                }
            } else if (state == LOG_STATE_CAPTURING) {
                if (s_post_trigger_remaining <= LOG_ENTRY_SIZE) {
                    /* Post-trigger capture complete — auto-stop */
                    s_state = LOG_STATE_IDLE;
                    flush_page_buffer();
                    s_meta.state = LOG_STATE_IDLE;
                    save_metadata();
                } else {
                    s_post_trigger_remaining -= LOG_ENTRY_SIZE;
                }
            }
        }
```

Update `flash_logger_enqueue_frame()` — accept ARMED and CAPTURING states too:
```c
void flash_logger_enqueue_frame(const void *gf_ptr)
{
    uint8_t state = s_state;
    if (state != LOG_STATE_RECORDING && state != LOG_STATE_ARMED &&
        state != LOG_STATE_CAPTURING)
        return;

    const gateway_frame_t *gf = (const gateway_frame_t *)gf_ptr;
    if (!passes_bus_filter((uint8_t)gf->source_bus)) return;

    if (xQueueSend(s_log_queue, gf, 0) != pdTRUE) {
        s_drop_count++;
    }
}
```

Add `flash_logger_arm()`:
```c
void flash_logger_arm(void)
{
    if (s_state != LOG_STATE_IDLE) return;
    if (s_meta.mode != LOG_MODE_TRIGGERED) return;

    s_meta.start_timestamp = time_us_32() / 1000;
    s_meta.state = LOG_STATE_ARMED;
    s_state = LOG_STATE_ARMED;
    s_page_buf_pos = 0;
    s_drop_count = 0;
    s_trigger_offset = 0;
    s_post_trigger_remaining = 0;

    save_metadata();
}
```

Update `flash_logger_set_mode()` to accept triggered:
```c
void     flash_logger_set_mode(uint8_t m)   { if (m <= LOG_MODE_TRIGGERED) s_meta.mode = m; }
```

Update `flash_logger_init()` to handle armed/capturing states on boot reset:
```c
    if (s_meta.state == LOG_STATE_RECORDING) {
        if (s_meta.mode == LOG_MODE_CONTINUOUS) {
            s_state = LOG_STATE_RECORDING;
        } else {
            s_meta.state = LOG_STATE_IDLE;
            save_metadata();
        }
    } else if (s_meta.state == LOG_STATE_ARMED || s_meta.state == LOG_STATE_CAPTURING) {
        /* Triggered mode — don't auto-resume, reset to idle */
        s_meta.state = LOG_STATE_IDLE;
        save_metadata();
    }
```

Update `flash_logger_stop()` to accept armed/capturing:
```c
void flash_logger_stop(void)
{
    uint8_t state = s_state;
    if (state != LOG_STATE_RECORDING && state != LOG_STATE_ARMED &&
        state != LOG_STATE_CAPTURING)
        return;

    s_state = LOG_STATE_IDLE;
    flush_page_buffer();
    s_meta.state = LOG_STATE_IDLE;
    save_metadata();
}
```

Add trigger config accessors:
```c
void     flash_logger_set_trigger_bus(uint8_t bus)    { s_meta.trigger_bus = bus; }
void     flash_logger_set_trigger_id(uint32_t id)     { s_meta.trigger_id = id; }
void     flash_logger_set_trigger_byte(uint8_t idx)   { if (idx < 8) s_meta.trigger_byte = idx; }
void     flash_logger_set_trigger_op(uint8_t op)      { if (op <= LOG_TRIGGER_OP_MASK) s_meta.trigger_op = op; }
void     flash_logger_set_trigger_value(uint8_t val)  { s_meta.trigger_value = val; }
void     flash_logger_set_pre_trigger_kb(uint16_t kb) { s_meta.pre_trigger_kb = kb; }
void     flash_logger_set_post_trigger_kb(uint16_t kb){ s_meta.post_trigger_kb = kb; }
uint8_t  flash_logger_get_trigger_bus(void)           { return s_meta.trigger_bus; }
uint32_t flash_logger_get_trigger_id(void)            { return s_meta.trigger_id; }
uint8_t  flash_logger_get_trigger_byte(void)          { return s_meta.trigger_byte; }
uint8_t  flash_logger_get_trigger_op(void)            { return s_meta.trigger_op; }
uint8_t  flash_logger_get_trigger_value(void)         { return s_meta.trigger_value; }
uint16_t flash_logger_get_pre_trigger_kb(void)        { return (uint16_t)s_meta.pre_trigger_kb; }
uint16_t flash_logger_get_post_trigger_kb(void)       { return (uint16_t)s_meta.post_trigger_kb; }
```

- [ ] **Step 3: Verify firmware compiles**

Run:
```bash
cd firmware && cmake --build build
```

- [ ] **Step 4: Commit**

```bash
git add firmware/src/logger/flash_logger.h firmware/src/logger/flash_logger.c
git commit -m "feat(firmware): add triggered recording mode with arm/capture state machine"
```

---

## Task 2: Firmware — Trigger Config Protocol Handlers

**Files:**
- Modify: `firmware/src/config/config_handler.c`

### Step-by-step:

- [ ] **Step 1: Add trigger READ_PARAM cases**

In `handle_read_param()`, in the `CFG_SECTION_LOG` case, after `LOG_PARAM_DROP_COUNT`, add:

```c
        case LOG_PARAM_TRIGGER_BUS:
            payload[3] = flash_logger_get_trigger_bus();
            plen = 4;
            break;
        case LOG_PARAM_TRIGGER_ID: {
            uint32_t tid = flash_logger_get_trigger_id();
            if (sub == 0) {
                payload[3] = (uint8_t)(tid);
                payload[4] = (uint8_t)(tid >> 8);
            } else {
                payload[3] = (uint8_t)(tid >> 16);
                payload[4] = (uint8_t)(tid >> 24);
            }
            plen = 5;
            break;
        }
        case LOG_PARAM_TRIGGER_BYTE:
            payload[3] = flash_logger_get_trigger_byte();
            plen = 4;
            break;
        case LOG_PARAM_TRIGGER_OP:
            payload[3] = flash_logger_get_trigger_op();
            plen = 4;
            break;
        case LOG_PARAM_TRIGGER_VALUE:
            payload[3] = flash_logger_get_trigger_value();
            plen = 4;
            break;
        case LOG_PARAM_PRE_TRIG_KB: {
            uint16_t pre = flash_logger_get_pre_trigger_kb();
            payload[3] = (uint8_t)(pre);
            payload[4] = (uint8_t)(pre >> 8);
            plen = 5;
            break;
        }
        case LOG_PARAM_POST_TRIG_KB: {
            uint16_t post = flash_logger_get_post_trigger_kb();
            payload[3] = (uint8_t)(post);
            payload[4] = (uint8_t)(post >> 8);
            plen = 5;
            break;
        }
```

- [ ] **Step 2: Add trigger WRITE_PARAM cases**

In `handle_write_param()`, in the `CFG_SECTION_LOG` case, after `LOG_PARAM_STATE_CMD`, add:

```c
        case LOG_PARAM_TRIGGER_BUS:
            flash_logger_set_trigger_bus(data[4]);
            break;
        case LOG_PARAM_TRIGGER_ID:
            if (dlc >= 8) {
                uint32_t tid = (uint32_t)data[4] | ((uint32_t)data[5] << 8)
                             | ((uint32_t)data[6] << 16) | ((uint32_t)data[7] << 24);
                flash_logger_set_trigger_id(tid);
            }
            break;
        case LOG_PARAM_TRIGGER_BYTE:
            flash_logger_set_trigger_byte(data[4]);
            break;
        case LOG_PARAM_TRIGGER_OP:
            flash_logger_set_trigger_op(data[4]);
            break;
        case LOG_PARAM_TRIGGER_VALUE:
            flash_logger_set_trigger_value(data[4]);
            break;
        case LOG_PARAM_PRE_TRIG_KB:
            if (dlc >= 6) {
                uint16_t kb = (uint16_t)data[4] | ((uint16_t)data[5] << 8);
                flash_logger_set_pre_trigger_kb(kb);
            }
            break;
        case LOG_PARAM_POST_TRIG_KB:
            if (dlc >= 6) {
                uint16_t kb = (uint16_t)data[4] | ((uint16_t)data[5] << 8);
                flash_logger_set_post_trigger_kb(kb);
            }
            break;
```

Also update the `LOG_PARAM_STATE_CMD` handler to support arm command (data[4]==2):
```c
        case LOG_PARAM_STATE_CMD:
            if (data[4] == 1) {
                flash_logger_start();
            } else if (data[4] == 0) {
                flash_logger_stop();
            } else if (data[4] == 2) {
                flash_logger_arm();
            } else if (data[4] == 0xFF) {
                flash_logger_erase_all();
            }
            break;
```

- [ ] **Step 3: Verify firmware compiles**

Run:
```bash
cd firmware && cmake --build build
```

- [ ] **Step 4: Commit**

```bash
git add firmware/src/config/config_handler.c
git commit -m "feat(firmware): add trigger config params and arm command to SectionLog"
```

---

## Task 3: Config Tool — Trigger Configuration UI

**Files:**
- Modify: `software/CanLinConfig/Protocol/ProtocolConstants.cs`
- Modify: `software/CanLinConfig/ViewModels/LogControlViewModel.cs`
- Modify: `software/CanLinConfig/Views/LogControlPanel.xaml`

### Step-by-step:

- [ ] **Step 1: Add trigger constants to ProtocolConstants.cs**

After the existing logger constants, add:
```csharp
    public const byte LogModeTriggered = 2;

    // Logger states
    public const byte LogStateArmed = 2;
    public const byte LogStateCapturing = 3;

    // Logger state commands
    public const byte LogCmdArm = 2;

    // Trigger params
    public const byte LogParamTriggerBus = 9;
    public const byte LogParamTriggerId = 10;
    public const byte LogParamTriggerByte = 11;
    public const byte LogParamTriggerOp = 12;
    public const byte LogParamTriggerValue = 13;
    public const byte LogParamPreTrigKb = 14;
    public const byte LogParamPostTrigKb = 15;
```

- [ ] **Step 2: Update LogControlViewModel**

Update `ModeNames` to include Triggered:
```csharp
    public string[] ModeNames { get; } = ["Manual", "Continuous", "Triggered"];
```

Add trigger config properties:
```csharp
    [ObservableProperty] private byte _triggerBus;
    [ObservableProperty] private string _triggerId = "0x100";
    [ObservableProperty] private byte _triggerByteIndex;
    [ObservableProperty] private int _selectedTriggerOpIndex; // 0=Any,1=Eq,2=GT,3=LT,4=Mask
    [ObservableProperty] private byte _triggerValue;
    [ObservableProperty] private ushort _preTriggerKb = 64;
    [ObservableProperty] private ushort _postTriggerKb = 64;
    [ObservableProperty] private bool _isTriggeredMode;

    public string[] TriggerOpNames { get; } = ["Any Match", "Equals", "Greater Than", "Less Than", "Bit Mask"];
    public string[] BusNames { get; } = ["CAN1", "CAN2", "LIN1", "LIN2", "LIN3", "LIN4"];
```

Add `OnSelectedModeIndexChanged` update for `IsTriggeredMode`:
```csharp
    partial void OnSelectedModeIndexChanged(int value)
    {
        IsTriggeredMode = value == 2;
        _ = SendModeAsync();
    }
```

Add Arm command:
```csharp
    [RelayCommand(CanExecute = nameof(CanArm))]
    private async Task Arm()
    {
        if (_protocol == null) return;

        // Send trigger config first
        await SendTriggerConfigAsync();

        // Send bus mask
        await _protocol.WriteParamAsync(
            ProtocolConstants.SectionLog,
            ProtocolConstants.LogParamBusMask, 0,
            [BuildBusMask()]);

        // Send arm command
        await _protocol.WriteParamAsync(
            ProtocolConstants.SectionLog,
            ProtocolConstants.LogParamStateCmd, 0,
            [ProtocolConstants.LogCmdArm]);

        await RefreshStatusAsync();
    }

    private bool CanArm() => IsConnected && !IsRecording && IsTriggeredMode;
```

Add `SendTriggerConfigAsync`:
```csharp
    private async Task SendTriggerConfigAsync()
    {
        if (_protocol == null) return;

        await _protocol.WriteParamAsync(ProtocolConstants.SectionLog,
            ProtocolConstants.LogParamTriggerBus, 0, [TriggerBus]);

        // Parse trigger ID from hex string
        if (uint.TryParse(TriggerId.Replace("0x", "").Replace("0X", ""),
            System.Globalization.NumberStyles.HexNumber, null, out uint id))
        {
            await _protocol.WriteParamAsync(ProtocolConstants.SectionLog,
                ProtocolConstants.LogParamTriggerId, 0,
                [(byte)id, (byte)(id >> 8), (byte)(id >> 16), (byte)(id >> 24)]);
        }

        await _protocol.WriteParamAsync(ProtocolConstants.SectionLog,
            ProtocolConstants.LogParamTriggerByte, 0, [TriggerByteIndex]);
        await _protocol.WriteParamAsync(ProtocolConstants.SectionLog,
            ProtocolConstants.LogParamTriggerOp, 0, [(byte)SelectedTriggerOpIndex]);
        await _protocol.WriteParamAsync(ProtocolConstants.SectionLog,
            ProtocolConstants.LogParamTriggerValue, 0, [TriggerValue]);
        await _protocol.WriteParamAsync(ProtocolConstants.SectionLog,
            ProtocolConstants.LogParamPreTrigKb, 0,
            [(byte)PreTriggerKb, (byte)(PreTriggerKb >> 8)]);
        await _protocol.WriteParamAsync(ProtocolConstants.SectionLog,
            ProtocolConstants.LogParamPostTrigKb, 0,
            [(byte)PostTriggerKb, (byte)(PostTriggerKb >> 8)]);
    }
```

Update `RefreshStatusAsync` status text to handle armed/capturing:
```csharp
            StatusText = LoggerStatus switch
            {
                ProtocolConstants.LogStateIdle => "Idle",
                ProtocolConstants.LogStateRecording => SelectedModeIndex == 1 ? "Recording (Continuous)" : "Recording",
                ProtocolConstants.LogStateArmed => "Armed — waiting for trigger",
                ProtocolConstants.LogStateCapturing => "Triggered — capturing post-trigger data",
                ProtocolConstants.LogStateError => "Error — flash failures",
                _ => $"Unknown ({LoggerStatus})"
            };
```

Update `IsRecording` to also be true when armed/capturing (for Stop button):
```csharp
            IsRecording = LoggerStatus == ProtocolConstants.LogStateRecording
                       || LoggerStatus == ProtocolConstants.LogStateArmed
                       || LoggerStatus == ProtocolConstants.LogStateCapturing;
```

Update `CanStart` to exclude triggered mode (use Arm instead):
```csharp
    private bool CanStart() => IsConnected && !IsRecording && !IsTriggeredMode;
```

Notify ArmCommand in RefreshStatusAsync:
```csharp
        ArmCommand.NotifyCanExecuteChanged();
```

- [ ] **Step 3: Update LogControlPanel.xaml**

Add an Arm button after the Start button:
```xml
                <Button Content="Arm" Command="{Binding ArmCommand}" Width="70" Margin="0,0,5,0"/>
```

Add a trigger config section (visible only in triggered mode) after the bus filter row and before the controls row:
```xml
            <!-- Trigger Config (visible in Triggered mode) -->
            <StackPanel Margin="0,0,0,8"
                        Visibility="{Binding IsTriggeredMode, Converter={StaticResource BoolToVisConverter}}">
                <StackPanel Orientation="Horizontal" Margin="0,0,0,4">
                    <TextBlock Text="Trigger Bus:" VerticalAlignment="Center" Margin="0,0,5,0"/>
                    <ComboBox ItemsSource="{Binding BusNames}" SelectedIndex="{Binding TriggerBus}"
                              Width="80" VerticalAlignment="Center" Margin="0,0,10,0"/>
                    <TextBlock Text="ID:" VerticalAlignment="Center" Margin="0,0,5,0"/>
                    <TextBox Text="{Binding TriggerId}" Width="80" VerticalAlignment="Center" Margin="0,0,10,0"/>
                    <TextBlock Text="Op:" VerticalAlignment="Center" Margin="0,0,5,0"/>
                    <ComboBox ItemsSource="{Binding TriggerOpNames}" SelectedIndex="{Binding SelectedTriggerOpIndex}"
                              Width="110" VerticalAlignment="Center"/>
                </StackPanel>
                <StackPanel Orientation="Horizontal">
                    <TextBlock Text="Byte:" VerticalAlignment="Center" Margin="0,0,5,0"/>
                    <TextBox Text="{Binding TriggerByteIndex}" Width="40" VerticalAlignment="Center" Margin="0,0,10,0"/>
                    <TextBlock Text="Value:" VerticalAlignment="Center" Margin="0,0,5,0"/>
                    <TextBox Text="{Binding TriggerValue}" Width="50" VerticalAlignment="Center" Margin="0,0,10,0"/>
                    <TextBlock Text="Pre (KB):" VerticalAlignment="Center" Margin="0,0,5,0"/>
                    <TextBox Text="{Binding PreTriggerKb}" Width="50" VerticalAlignment="Center" Margin="0,0,10,0"/>
                    <TextBlock Text="Post (KB):" VerticalAlignment="Center" Margin="0,0,5,0"/>
                    <TextBox Text="{Binding PostTriggerKb}" Width="50" VerticalAlignment="Center"/>
                </StackPanel>
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
git commit -m "feat(config-tool): add triggered mode config UI with arm command"
```

---

## Task 4: Update Master Plan

**Files:**
- Modify: `docs/bus-monitor-logger-master-plan.md`

### Step-by-step:

- [ ] **Step 1: Update Plan 5C status to COMPLETE**

Update the Plan 5C section with what was built.

- [ ] **Step 2: Verify firmware builds and tests pass**

Run:
```bash
cd firmware && cmake --build build
cd software && dotnet test CanLinConfig.Tests -v n
```

- [ ] **Step 3: Commit**

```bash
git add docs/bus-monitor-logger-master-plan.md
git commit -m "docs: update master plan — Plan 5C triggered mode complete"
```

---

## Testing Notes

### Config tool (can test now)
- Existing tests still pass
- Mode selector shows Manual/Continuous/Triggered
- Trigger config panel appears only in Triggered mode
- UI manually verifiable

### Firmware (deferred — requires hardware)
- Set mode to triggered, configure trigger (bus=CAN1, ID=0x100, op=any)
- Arm logger, send matching frame on CAN1 → verify state transitions: armed → capturing → idle
- Verify pre_trigger_kb of data retained before trigger point
- Verify post_trigger_kb of data captured after trigger
- Verify stop command works during armed/capturing states
- Verify reboot during armed state → resets to idle (no auto-resume)
- Verify trigger_byte/trigger_op/trigger_value conditions (equals, gt, lt, mask)
