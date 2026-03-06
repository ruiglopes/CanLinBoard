# Test Plan: Profile Library Completion + Vector XL Adapter

## Prerequisites

- Build: `cd software && dotnet build CanLinConfig.sln` — must succeed with 0 errors, 0 warnings
- PCAN adapter + CAN/LIN board connected (for on-device tests)
- Vector XL driver installed (for Vector adapter tests; graceful fallback tested without it)

---

## Part 1: Profile Library

### T1 — Profile Loading on Startup

| # | Test | Steps | Expected |
|---|------|-------|----------|
| 1.1 | Built-in profiles load | Launch app | Profile list shows "Bosch WDA LIN Wiper Motor" and "Pierburg CWA400 LIN Coolant Pump" |
| 1.2 | Profile details display | Select WDA profile | Name, description, version "1.0", LIN Mode "master", Baud Rate "19200" all visible |
| 1.3 | Parameters field parsed | Select WDA profile, note it in detail pane | No crash; parameters section is hidden (ProfileApplied = false) |

### T2 — Routing Rule Generation (M8.13)

| # | Test | Steps | Expected |
|---|------|-------|----------|
| 2.1 | WDA generates 2 rules | Connect to device. Select WDA, channel LIN1, click "Apply Profile" | Status bar shows "...with 2 routing rules". Switch to Routing tab: 2 new rules present |
| 2.2 | Control rule correct | Inspect first rule in Routing tab | SrcBus=CAN1, SrcId=0x280 (640), SrcMask=0x7FF, DstBus=LIN1, DstId=33, DstDlc=0, Enabled=true, 2 byte mappings (0->0, 1->1), ProfileTag="wda-wiper:0" |
| 2.3 | Status rule correct | Inspect second rule in Routing tab | SrcBus=LIN1, SrcId=34, SrcMask=0x03F, DstBus=CAN1, DstId=0x281 (641), 4 byte mappings (0->0..3->3), ProfileTag="wda-wiper:0" |
| 2.4 | Channel assignment | Select WDA, channel LIN3, Apply | Rules show DstBus/SrcBus=LIN3 (bus index 4). ProfileTag="wda-wiper:2" |
| 2.5 | Re-apply replaces rules | Apply WDA to LIN1, then re-apply WDA to LIN2 | Old LIN1 rules (tag "wda-wiper:0") removed. New LIN2 rules (tag "wda-wiper:1") added. Total WDA rules = 2 |
| 2.6 | Re-apply same channel | Apply WDA to LIN1 twice | Still exactly 2 rules with tag "wda-wiper:0" (no duplicates) |
| 2.7 | Two profiles coexist | Apply WDA to LIN1, then CWA400 to LIN2 | 4 rules total: 2 with tag "wda-wiper:0", 2 with tag "cwa400-pump:1" |
| 2.8 | Capacity limit (32 max) | Manually add rules to reach 31, then Apply WDA profile | MessageBox warns "would exceed maximum of 32". Rules not added; existing rules unchanged |
| 2.9 | Rules written to device | Apply WDA, then do Read All | Rules round-trip: Routing tab still shows 2 rules with correct IDs/mappings. (ProfileTag is software-only so will be empty after read-back — that's expected) |

### T3 — Import/Export Profiles (M8.15)

| # | Test | Steps | Expected |
|---|------|-------|----------|
| 3.1 | Export profile | Select WDA, click Export. Save as `test-export.json` | File created. Open in text editor: valid JSON with name, id, parameters, etc. |
| 3.2 | Import new profile | Create a custom profile JSON with `"id": "test-custom"`. Click Import, select file | Profile appears in list. File copied to `Profiles/Devices/test-custom.json` |
| 3.3 | Import selects profile | Import a profile | Imported profile is auto-selected in list |
| 3.4 | Import duplicate — replace | Import a profile with `"id": "wda-wiper"` | Prompt asks "already exists. Replace it?" — click Yes: old entry replaced, new one shown |
| 3.5 | Import duplicate — cancel | Same as 3.4 but click No | Profile list unchanged |
| 3.6 | Import invalid JSON | Import a file with no `name` field | MessageBox: "Invalid profile: missing 'name' or 'id' field." |
| 3.7 | Import non-JSON file | Import a `.txt` file | MessageBox: "Import failed: ..." |
| 3.8 | Status bar feedback | Import or export | Status bar shows "Imported profile: ..." or "Exported profile: ..." |

### T4 — Parameter Controls (M8.14)

| # | Test | Steps | Expected |
|---|------|-------|----------|
| 4.1 | Hidden before apply | Select WDA profile (don't click Apply) | "Device Parameters" section not visible |
| 4.2 | Visible after apply | Connect + Apply WDA | "Device Parameters" header appears. Two controls: "Wiper Mode" dropdown, "Interval Time" slider |
| 4.3 | Enum ComboBox | WDA applied. Click "Wiper Mode" dropdown | Options: Off, Slow, Fast, Interval. Default selection index 0 (Off) |
| 4.4 | Numeric Slider | WDA applied. Drag "Interval Time" slider | Value updates (0-255). Unit "x100ms" shown in gray |
| 4.5 | CWA400 parameters | Apply CWA400 | Two controls: "Pump Speed" slider (0-255, unit "%"), "Enable" dropdown (Off, On) |
| 4.6 | Send Control Frame — basic | WDA applied. Set Wiper Mode=Fast (index 2), Interval=50. Click "Send Control Frame" | Status bar: "Sent control frame 0x280: 0x280 [8] 02 32 00 00 00 00 00 00". Frame visible in CAN bus trace if monitoring |
| 4.7 | Byte masking | WDA: Wiper Mode mask=0xFF at byte 0, Interval mask=0xFF at byte 1 | Sent frame data[0] = enum index, data[1] = slider value. No cross-contamination |
| 4.8 | Send without apply | Before applying any profile, "Send Control Frame" button | Button not visible (entire parameter section hidden by `ProfileApplied=false`) |
| 4.9 | Profile with no parameters | Create/import a profile JSON with no `parameters` field. Apply it | No parameter controls shown. "Send Control Frame" button visible but `SendControlFrame` returns immediately (CanControl null guard or empty values = all-zero frame) |

### T5 — Config File Round-trip (ProfileTag persistence)

| # | Test | Steps | Expected |
|---|------|-------|----------|
| 5.1 | Export preserves ProfileTag | Apply WDA profile. File > Save Config. Open JSON | `routing` array entries include `"profile_tag": "wda-wiper:0"` |
| 5.2 | Import restores ProfileTag | Load the config file from 5.1 via File > Load Config | Routing rules in UI have `ProfileTag = "wda-wiper:0"` (verify by re-applying same profile — old rules correctly removed) |
| 5.3 | Empty tag omitted | Manual rules (no profile). File > Save Config | Routing entries have no `profile_tag` field in JSON (omitted when empty, not `""`) |

---

## Part 2: Vector XL Adapter

### T6 — Graceful Fallback (no Vector driver)

| # | Test | Steps | Expected |
|---|------|-------|----------|
| 6.1 | No crash on select | Select "Vector XL" adapter in dropdown (without vxlapi64.dll installed) | Channel list is empty. No crash or unhandled exception |
| 6.2 | Connect returns false | Select Vector XL, try to connect | "CAN adapter connection failed" in status bar |

### T7 — Vector XL with Hardware

*Requires: Vector CANcase/CANboard with XL Driver Library installed*

| # | Test | Steps | Expected |
|---|------|-------|----------|
| 7.1 | Channel enumeration | Select "Vector XL" adapter | Channel list shows Vector_CH0 through Vector_CH7 |
| 7.2 | Connect at 500 kbps | Select channel, bitrate 500000, click Connect | Status: "Connected - FW vX.Y.Z" (or "No device response" if no board — that's adapter-level pass) |
| 7.3 | TX frame | Connected. Go to Routing tab or use Send Control Frame | Frame appears on bus (verify with second tool: CANalyzer, PCAN-View, etc.) |
| 7.4 | RX frame | Send a frame from external tool (e.g. 0x123 [3] AA BB CC) | Frame appears in Diagnostics monitor with correct ID, DLC, data |
| 7.5 | Extended ID TX | Send frame with IsExtended=true | Bit 31 set in XL event ID on the wire; received as extended at remote |
| 7.6 | Extended ID RX | Remote sends 0x1ABCDEF3 extended frame | CanFrame.IsExtended=true, Id=0x1ABCDEF3 in monitor |
| 7.7 | Disconnect clean | Click Disconnect | RX thread stops within 1s. No exceptions. Can reconnect |
| 7.8 | Config protocol | Connect via Vector, Read All | Same behavior as PCAN: reads CAN/LIN/Routing/Diag params |

### T8 — Vector Adapter Cross-check vs PCAN

*Requires: both PCAN and Vector adapters on same bus*

| # | Test | Steps | Expected |
|---|------|-------|----------|
| 8.1 | PCAN TX -> Vector RX | Instance A (PCAN) sends frame. Instance B (Vector) monitors | Frame received correctly on Vector side |
| 8.2 | Vector TX -> PCAN RX | Vice versa | Frame received correctly on PCAN side |

---

## Summary

| Area | Tests | Needs HW | Key risk |
|------|-------|----------|----------|
| Profile loading | T1 (3) | No | JSON schema backward compat |
| Routing rule gen | T2 (9) | Yes (device) | Bus index mapping, tag cleanup |
| Import/export | T3 (8) | No | File validation, duplicate handling |
| Parameter controls | T4 (9) | Yes (Send) | Byte packing, UI visibility |
| Config file round-trip | T5 (3) | No | ProfileTag serialization |
| Vector fallback | T6 (2) | No | DllNotFoundException path |
| Vector with HW | T7 (8) | Yes (Vector) | Struct marshaling, RX loop |
| Cross-adapter | T8 (2) | Yes (both) | Interop correctness |

**Total: 44 test cases** (13 can run without hardware, 31 need device/adapter)
