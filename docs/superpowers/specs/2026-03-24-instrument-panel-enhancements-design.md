# Instrument Panel Enhancements — Design Spec

**Date:** 2026-03-24
**Status:** Approved
**Branch:** feature/bus-monitor-foundation

## Overview

Three enhancements to the instrument panel in the CanLinConfig WPF tool:

1. **Widget type selector** — submenu in context menu replacing the cycle-through approach
2. **BitPanel widget** — configurable multi-bit display with per-bit labels
3. **Free-position drag** — canvas-based layout with snap-to-edge alignment

## 1. Widget Type Selector

Replace the single "Change Type" context menu item with a submenu:

```
Right-click → Change Type → Numeric      ✓
                           → Bar
                           → Gauge
                           → Boolean
                           → BitPanel
                           → Enum
             Edit BitPanel...  (only visible when type is BitPanel)
             Remove
```

### Command binding approach

One command per type, with the widget type hardcoded in each MenuItem's `Tag` and the widget passed as `CommandParameter`. The ViewModel has a single `ChangeWidgetType` command that receives the widget and reads the target type from the sender's tag. Alternatively, use individual relay commands per type — but since the list is small (6 items), a single command with a `Tuple<InstrumentWidget, WidgetType>` parameter via a `MultiBinding` + converter is overkill. **Use one MenuItem per type, each with `CommandParameter="{Binding}"` and `Tag` set to the `WidgetType` enum value.** The command handler receives the widget and reads `Tag` from the command source.

Simplest approach: **6 individual relay commands** (`ChangeToNumericCommand`, `ChangeToBarCommand`, etc.) that each call `ChangeWidgetType(widget, targetType)` internally. This avoids converter complexity entirely.

### Checkmark on current type

Each submenu MenuItem binds `IsChecked` to a value converter: `IsChecked="{Binding Type, Converter={StaticResource EnumMatchConverter}, ConverterParameter=Numeric}"`. The `EnumMatchConverter` returns `true` when the value equals the parameter. One simple `IValueConverter` class.

## 2. BitPanel Widget

### Purpose

Display a signal's integer value as individual bit lamps with configurable labels. Useful for status registers and flag bytes (e.g., bit 0 = "Error", bit 1 = "Active", bit 2 = "Ready").

### Model Changes

**WidgetType enum:** Add `BitPanel` variant.

**InstrumentWidget** — new fields:
- `BitCount` (int, 1–8, default 8)
- `BitLabels` (List\<string\>, length = BitCount, defaults to `["Bit 0", "Bit 1", ...]`)

**WidgetLayout** — new JSON fields:
- `"bit_count"` (int)
- `"bit_labels"` (string[])

### Widget UI

- Container width: ~200px, height scales with BitCount (~24px per row + header)
- Header: signal name
- Each bit rendered as a horizontal row, **LSB-first** (bit 0 at top):
  - Colored circle: green (#4CD964) when bit=1, dark gray (#444) when bit=0
  - Label text to the right of the lamp
- Bit extraction: `((uint)(long)Value >> bitIndex) & 1` — cast through `long` first to handle the full uint32 range, then to `uint` to avoid sign-extension issues

### Configuration

- **On type change to BitPanel:** The View's code-behind catches a `RequestBitPanelConfig` event from the ViewModel and shows a modal dialog. This matches the existing codebase pattern where dialogs are opened from code-behind, not ViewModels.
- **Dialog contents:**
  - Spinner: number of bits (1–8)
  - Text field per bit for the label (defaults to "Bit 0", "Bit 1", etc.)
  - OK / Cancel buttons
- **"Edit BitPanel..." context menu item:** Opens the same dialog for reconfiguration
  - Only visible when widget type is BitPanel

### Template Selection

- Add `BitPanelTpl` DataTemplate and `BitPanelWidget` UserControl
- Add `BitPanel` case to `WidgetTemplateSelector` in `Views/InstrumentPanel.xaml.cs`

## 3. Free-Position Drag with Snap

### Layout Change

- Replace `WrapPanel` inside `ItemsControl` with a `Canvas`
- Each widget positioned via `Canvas.Left` and `Canvas.Top` attached properties on the `ContentPresenter`
- Binding in `ItemContainerStyle`:
  ```xml
  <Setter Property="Canvas.Left" Value="{Binding X, Mode=TwoWay}"/>
  <Setter Property="Canvas.Top" Value="{Binding Y, Mode=TwoWay}"/>
  ```

### Model Changes

**InstrumentWidget** — new fields (must be `[ObservableProperty]` for binding):
- `[ObservableProperty] private double _x;`
- `[ObservableProperty] private double _y;`

These are observable so that TwoWay bindings on `Canvas.Left`/`Canvas.Top` update the UI when the model changes and vice versa.

**WidgetLayout** — new JSON fields:
- `"x"` (double)
- `"y"` (double)

### Drag Behavior

Implementation: drag logic in `InstrumentPanel.xaml.cs` code-behind (not a separate class).

- **Mouse down** on widget container (`ContentPresenter`): start drag, capture mouse, record offset from widget origin
- **Mouse move**: update `widget.X` and `widget.Y` (flows through TwoWay binding to `Canvas.Left`/`Canvas.Top`), compute snap targets, show guidelines
- **Mouse up**: clear guidelines, release mouse capture. Position is already committed to model via binding.
- **Escape key**: cancel drag, restore original position, release capture
- **Right-click during drag**: ignored (mouse capture prevents context menu)

### Snap Logic

- **Snap threshold:** 8 pixels
- **Snap targets:** canvas edges (left, top, right, bottom) and edges of all other widgets (left, right, top, bottom)
- **Widget bounds for snap:** use `ActualWidth`/`ActualHeight` from the rendered `ContentPresenter`, not declared sizes (handles variable-height BitPanel widgets)
- Horizontal and vertical snap are independent (can snap X to one widget, Y to another)
- When within threshold: snap position to nearest target edge, draw a thin blue guideline (1px, #0078D7)
- Guidelines are temporary `Line` elements added/removed on the canvas during drag
- **Z-order:** last-dragged widget comes to front via `Canvas.SetZIndex`

### New Widget Placement

When a signal is added to the instrument panel via "Add to Instrument Panel":
- Simple stacking: place at `X = 0`, `Y = bottom edge of lowest widget + 8px margin`
- If no widgets exist, place at (8, 8)
- No bin-packing; user drags to arrange

### Backward Compatibility

- Existing `WidgetLayout` entries without `x`/`y` fields default to 0.0
- On load, if all widgets have X=Y=0 (old project), auto-layout in a vertical stack: each widget at `X=8`, `Y = index * 120` (spaced evenly)
- `ProjectManifest.Version` remains 1 — new fields are additive and silently ignored by older tools. No data loss risk since older tools don't modify `WidgetLayout` fields they don't understand.

## Files Changed

| File | Change |
|------|--------|
| `Models/InstrumentWidget.cs` | Add `BitPanel` to enum, `BitCount`, `BitLabels`, `_x`, `_y` fields |
| `Models/Project.cs` (`WidgetLayout`) | Add `bit_count`, `bit_labels`, `x`, `y` JSON fields |
| `Helpers/EnumMatchConverter.cs` | New — `IValueConverter` returning `true` when value equals parameter |
| `ViewModels/InstrumentPanelViewModel.cs` | Replace `CycleWidgetType` with per-type commands, add `RequestBitPanelConfig` event, auto-placement logic |
| `Views/InstrumentPanel.xaml` | Replace WrapPanel with Canvas, update context menu to submenu with checkmarks, add BitPanel template, Canvas.Left/Top bindings |
| `Views/InstrumentPanel.xaml.cs` | Add drag+snap logic, handle `RequestBitPanelConfig` event to show dialog, add `BitPanel` case to `WidgetTemplateSelector` |
| `Views/Widgets/BitPanelWidget.xaml` | New — bit lamp rows |
| `Views/Widgets/BitPanelWidget.xaml.cs` | New — code-behind for dynamic bit rows |
| `Views/Widgets/BitPanelConfigDialog.xaml` | New — modal config dialog |
| `Views/Widgets/BitPanelConfigDialog.xaml.cs` | New — dialog code-behind |

## Testing

- Existing INS-1 to INS-5 tests updated for new behavior
- New tests:
  - INS-6: Widget type submenu — select each type from submenu, verify widget changes and checkmark moves
  - INS-7: BitPanel — add signal, change to BitPanel, configure 4 bits with labels, verify lamps reflect value
  - INS-8: BitPanel edit — right-click "Edit BitPanel...", change labels, verify update
  - INS-9: Drag widget — drag to new position, verify snap guidelines appear near edges
  - INS-10: Snap alignment — drag near another widget edge, verify snap to edge
  - INS-11: Position persistence — save project with positioned widgets, reopen, verify positions restored
  - INS-12: Backward compat — open old project without x/y, verify auto-layout (vertical stack, no stacking)
  - INS-13: Escape cancels drag — start drag, press Escape, verify widget returns to original position
  - INS-14: Z-order — drag a widget, verify it comes to front over overlapping widgets
