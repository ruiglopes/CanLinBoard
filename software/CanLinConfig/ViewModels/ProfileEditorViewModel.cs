using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanLinConfig.Parsers;
using Microsoft.Win32;

namespace CanLinConfig.ViewModels;

// Observable wrappers for profile sub-items used in the editor DataGrids

public partial class EditableScheduleEntry : ObservableObject
{
    [ObservableProperty] private byte _id;
    [ObservableProperty] private byte _dlc = 8;
    [ObservableProperty] private string _direction = "subscribe"; // "publish" or "subscribe"
    [ObservableProperty] private ushort _intervalMs = 10;
}

public partial class EditableSignal : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private int _startBit;
    [ObservableProperty] private int _bitLength = 8;
    [ObservableProperty] private string _byteOrder = "little_endian";
    [ObservableProperty] private bool _isSigned;
    [ObservableProperty] private double _factor = 1.0;
    [ObservableProperty] private double _offset;
    [ObservableProperty] private double _minValue;
    [ObservableProperty] private double _maxValue = 255;
    [ObservableProperty] private string _unit = "";
    public Dictionary<string, string>? ValueDescriptions { get; set; }

    public string Summary => $"{StartBit}|{BitLength} {(IsSigned ? "-" : "+")} [{MinValue}..{MaxValue}] {Unit}";
}

public partial class EditableByteMap : ObservableObject
{
    [ObservableProperty] private byte _srcByte;
    [ObservableProperty] private byte _dstByte;
    [ObservableProperty] private byte _mask = 0xFF;
}

public partial class EditableCanMapping : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _direction = "control"; // "control" or "status"
    [ObservableProperty] private uint _canId;
    [ObservableProperty] private byte _linFrameId;
    [ObservableProperty] private bool _useSignalMode;

    public ObservableCollection<EditableSignal> Signals { get; } = [];
    public ObservableCollection<EditableByteMap> ByteMaps { get; } = [];

    [ObservableProperty] private EditableSignal? _selectedSignal;
    [ObservableProperty] private EditableByteMap? _selectedByteMap;

    public string DirectionLabel => Direction == "control" ? "CAN → LIN" : "LIN → CAN";
    public string DisplayText => $"{Name} ({DirectionLabel}, CAN 0x{CanId:X3}, LIN 0x{LinFrameId:X2})";

    partial void OnDirectionChanged(string value) => OnPropertyChanged(nameof(DirectionLabel));
    partial void OnCanIdChanged(uint value) => OnPropertyChanged(nameof(DisplayText));
    partial void OnLinFrameIdChanged(byte value) => OnPropertyChanged(nameof(DisplayText));
    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(DisplayText));
}

public partial class EditableParameter : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _type = "numeric"; // "enum" or "numeric"
    [ObservableProperty] private string _optionsText = ""; // comma-separated for enum
    [ObservableProperty] private int _min;
    [ObservableProperty] private int _max = 255;
    [ObservableProperty] private string _unit = "";
    [ObservableProperty] private byte _canControlByte;
    [ObservableProperty] private byte _mask = 0xFF;
    [ObservableProperty] private string _frame = "control"; // "control" or "status"

    // Bit-level
    [ObservableProperty] private int? _startBit;
    [ObservableProperty] private int? _bitLength;
    [ObservableProperty] private string? _byteOrder;
    [ObservableProperty] private bool _isSigned;
    [ObservableProperty] private double _factor = 1.0;
    [ObservableProperty] private double _offset;
    public Dictionary<string, string>? ValueDescriptions { get; set; }

    public bool IsBitLevel => StartBit.HasValue;
}

// --- Main Editor ViewModel ---

public partial class ProfileEditorViewModel : ObservableObject
{
    public bool IsNew { get; }

    // Basic info
    [ObservableProperty] private string _profileName = "";
    [ObservableProperty] private string _profileId = "";
    [ObservableProperty] private string _version = "1.0";
    [ObservableProperty] private string _description = "";

    // LIN config
    [ObservableProperty] private string _linMode = "master";
    [ObservableProperty] private uint _linBaudrate = 19200;

    // Collections
    public ObservableCollection<EditableScheduleEntry> ScheduleEntries { get; } = [];
    public ObservableCollection<EditableCanMapping> CanMappings { get; } = [];
    public ObservableCollection<EditableParameter> Parameters { get; } = [];

    [ObservableProperty] private EditableScheduleEntry? _selectedScheduleEntry;
    [ObservableProperty] private EditableCanMapping? _selectedCanMapping;
    [ObservableProperty] private EditableParameter? _selectedParameter;

    public ProfileEditorViewModel(ProfileDefinition profile, bool isNew)
    {
        IsNew = isNew;
        LoadFromProfile(profile);
    }

    private void LoadFromProfile(ProfileDefinition p)
    {
        ProfileName = p.Name;
        ProfileId = p.Id;
        Version = p.Version;
        Description = p.Description;

        if (p.LinConfig != null)
        {
            LinMode = p.LinConfig.Mode;
            LinBaudrate = p.LinConfig.Baudrate;
        }

        if (p.ScheduleTable != null)
        {
            foreach (var e in p.ScheduleTable)
                ScheduleEntries.Add(new EditableScheduleEntry
                {
                    Id = e.Id, Dlc = e.Dlc, Direction = e.Direction, IntervalMs = e.IntervalMs
                });
        }

        // Load CAN mappings (handles both new can_mappings[] and legacy can_control/can_status)
        foreach (var m in p.GetAllMappings())
        {
            var em = new EditableCanMapping
            {
                Name = m.Name ?? "",
                Direction = m.Direction ?? "control",
                CanId = m.CanId,
                LinFrameId = m.LinFrameId,
                UseSignalMode = m.IsSignalMode,
            };

            if (m.Signals != null)
                foreach (var s in m.Signals)
                    em.Signals.Add(SignalToEditable(s));

            if (m.Mappings != null)
                foreach (var b in m.Mappings)
                    em.ByteMaps.Add(new EditableByteMap { SrcByte = b.SrcByte, DstByte = b.DstByte, Mask = b.Mask });

            CanMappings.Add(em);
        }
        if (CanMappings.Count > 0) SelectedCanMapping = CanMappings[0];

        if (p.Parameters != null)
        {
            foreach (var par in p.Parameters)
            {
                Parameters.Add(new EditableParameter
                {
                    Name = par.Name,
                    Type = par.Type,
                    OptionsText = par.Options != null ? string.Join(", ", par.Options) : "",
                    Min = par.Min,
                    Max = par.Max,
                    Unit = par.Unit,
                    CanControlByte = par.CanControlByte,
                    Mask = par.Mask,
                    Frame = par.Frame ?? "control",
                    StartBit = par.StartBit,
                    BitLength = par.BitLength,
                    ByteOrder = par.ByteOrder,
                    IsSigned = par.IsSigned ?? false,
                    Factor = par.Factor ?? 1.0,
                    Offset = par.Offset ?? 0.0,
                    ValueDescriptions = par.ValueDescriptions,
                });
            }
        }
    }

    public ProfileDefinition BuildProfile()
    {
        var p = new ProfileDefinition
        {
            Name = ProfileName,
            Id = ProfileId,
            Version = Version,
            Description = Description,
            LinConfig = new ProfileLinConfig { Mode = LinMode, Baudrate = LinBaudrate },
            ScheduleTable = ScheduleEntries.Select(e => new ProfileScheduleEntry
            {
                Id = e.Id, Dlc = e.Dlc, Direction = e.Direction, IntervalMs = e.IntervalMs
            }).ToArray(),
            CanMappings = CanMappings.Select(em =>
            {
                var m = new ProfileCanMapping
                {
                    Name = em.Name,
                    Direction = em.Direction,
                    CanId = em.CanId,
                    LinFrameId = em.LinFrameId,
                    MappingMode = em.UseSignalMode ? "signal" : null,
                    Signals = em.UseSignalMode && em.Signals.Count > 0
                        ? em.Signals.Select(EditableToSignal).ToArray() : null,
                    Mappings = em.ByteMaps.Count > 0
                        ? em.ByteMaps.Select(b => new ProfileByteMap { SrcByte = b.SrcByte, DstByte = b.DstByte, Mask = b.Mask }).ToArray()
                        : (em.UseSignalMode ? null : []),
                };

                // Auto-generate byte maps from signals for firmware compat
                if (em.UseSignalMode && m.Signals?.Length > 0)
                    m.Mappings = [.. m.GenerateByteMapsFromSignals()];

                return m;
            }).ToArray(),
            // Don't write legacy fields
            CanControl = null,
            CanStatus = null,
            Parameters = Parameters.Select(par => new ProfileParameter
            {
                Name = par.Name,
                Type = par.Type,
                Options = par.Type == "enum" && !string.IsNullOrWhiteSpace(par.OptionsText)
                    ? par.OptionsText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : null,
                Min = par.Min,
                Max = par.Max,
                Unit = par.Unit,
                CanControlByte = par.CanControlByte,
                Mask = par.Mask,
                Frame = par.Frame != "control" ? par.Frame : null,
                StartBit = par.StartBit,
                BitLength = par.BitLength,
                ByteOrder = par.ByteOrder,
                IsSigned = par.IsSigned ? true : null,
                Factor = par.Factor != 1.0 ? par.Factor : null,
                Offset = par.Offset != 0.0 ? par.Offset : null,
                ValueDescriptions = par.ValueDescriptions,
            }).ToArray(),
        };

        return p;
    }

    // --- Schedule commands ---

    [RelayCommand]
    private void AddScheduleEntry() =>
        ScheduleEntries.Add(new EditableScheduleEntry { Id = 0, Dlc = 8, Direction = "subscribe", IntervalMs = 10 });

    [RelayCommand]
    private void RemoveScheduleEntry()
    {
        if (SelectedScheduleEntry != null)
            ScheduleEntries.Remove(SelectedScheduleEntry);
    }

    // --- CAN Mapping commands ---

    [RelayCommand]
    private void AddCanMapping()
    {
        var m = new EditableCanMapping { Name = $"Mapping_{CanMappings.Count}", Direction = "control" };
        CanMappings.Add(m);
        SelectedCanMapping = m;
    }

    [RelayCommand]
    private void RemoveCanMapping()
    {
        if (SelectedCanMapping != null)
        {
            CanMappings.Remove(SelectedCanMapping);
            SelectedCanMapping = CanMappings.Count > 0 ? CanMappings[0] : null;
        }
    }

    // --- Signal commands (for selected mapping) ---

    [RelayCommand]
    private void AddSignal()
    {
        if (SelectedCanMapping == null) return;
        SelectedCanMapping.Signals.Add(new EditableSignal { Name = $"Signal_{SelectedCanMapping.Signals.Count}" });
    }

    [RelayCommand]
    private void RemoveSignal()
    {
        if (SelectedCanMapping?.SelectedSignal != null)
            SelectedCanMapping.Signals.Remove(SelectedCanMapping.SelectedSignal);
    }

    // --- Byte map commands (for selected mapping) ---

    [RelayCommand]
    private void AddByteMap()
    {
        if (SelectedCanMapping == null) return;
        SelectedCanMapping.ByteMaps.Add(new EditableByteMap());
    }

    [RelayCommand]
    private void RemoveByteMap()
    {
        if (SelectedCanMapping?.SelectedByteMap != null)
            SelectedCanMapping.ByteMaps.Remove(SelectedCanMapping.SelectedByteMap);
    }

    // --- Parameter commands ---

    [RelayCommand]
    private void AddParameter() =>
        Parameters.Add(new EditableParameter { Name = $"Param_{Parameters.Count}" });

    [RelayCommand]
    private void RemoveParameter()
    {
        if (SelectedParameter != null)
            Parameters.Remove(SelectedParameter);
    }

    [RelayCommand]
    private void AutoParametersFromSignals()
    {
        // Gather control signals from all control mappings that use signal mode
        var controlSignals = CanMappings
            .Where(m => m.Direction == "control" && m.UseSignalMode)
            .SelectMany(m => m.Signals)
            .ToList();

        if (controlSignals.Count == 0)
        {
            MessageBox.Show("No control signals defined. Import a DBC/LDF or add signals first.",
                "No Signals", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        foreach (var sig in controlSignals)
        {
            if (Parameters.Any(p => p.Name == sig.Name)) continue;

            bool hasValDescs = sig.ValueDescriptions?.Count > 0;
            var param = new EditableParameter
            {
                Name = sig.Name,
                Type = hasValDescs ? "enum" : "numeric",
                Min = (int)sig.MinValue,
                Max = (int)sig.MaxValue,
                Unit = sig.Unit,
                Frame = "control",
                StartBit = sig.StartBit,
                BitLength = sig.BitLength,
                ByteOrder = sig.ByteOrder,
                IsSigned = sig.IsSigned,
                Factor = sig.Factor,
                Offset = sig.Offset,
                ValueDescriptions = sig.ValueDescriptions,
                CanControlByte = (byte)(sig.StartBit / 8),
                Mask = CalculateMask(sig.StartBit, sig.BitLength),
            };

            if (hasValDescs)
                param.OptionsText = string.Join(", ", sig.ValueDescriptions!.Values);

            Parameters.Add(param);
        }
    }

    private static byte CalculateMask(int startBit, int bitLength)
    {
        int bitInByte = startBit % 8;
        int bitsInFirstByte = Math.Min(bitLength, 8 - bitInByte);
        return (byte)(((1 << bitsInFirstByte) - 1) << bitInByte);
    }

    // --- DBC Import ---

    [RelayCommand]
    private void ImportDbc()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "DBC Files|*.dbc|All Files|*.*",
            Title = "Import CAN Database (DBC)",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var dbc = DbcParser.Parse(dlg.FileName);
            if (dbc.Messages.Count == 0)
            {
                MessageBox.Show("No messages found in DBC file.", "Import", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var importDlg = new Views.DbcImportDialog(dbc);
            importDlg.Owner = FindProfileEditorWindow();
            if (importDlg.ShowDialog() != true) return;

            // Apply control message as a new mapping
            if (importDlg.SelectedControlMessage != null)
            {
                var msg = importDlg.SelectedControlMessage;
                var em = new EditableCanMapping
                {
                    Name = msg.Name,
                    Direction = "control",
                    CanId = msg.Id,
                    UseSignalMode = true,
                };
                foreach (var sig in msg.Signals)
                    em.Signals.Add(DbcSignalToEditable(sig));
                CanMappings.Add(em);
                SelectedCanMapping = em;
            }

            // Apply status message as a new mapping
            if (importDlg.SelectedStatusMessage != null)
            {
                var msg = importDlg.SelectedStatusMessage;
                var em = new EditableCanMapping
                {
                    Name = msg.Name,
                    Direction = "status",
                    CanId = msg.Id,
                    UseSignalMode = true,
                };
                foreach (var sig in msg.Signals)
                    em.Signals.Add(DbcSignalToEditable(sig));
                CanMappings.Add(em);
                SelectedCanMapping = em;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"DBC import failed: {ex.Message}", "Import Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // --- LDF Import ---

    [RelayCommand]
    private void ImportLdf()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "LDF Files|*.ldf|All Files|*.*",
            Title = "Import LIN Description File (LDF)",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var ldf = LdfParser.Parse(dlg.FileName);

            var importDlg = new Views.LdfImportDialog(ldf);
            importDlg.Owner = FindProfileEditorWindow();
            if (importDlg.ShowDialog() != true) return;

            // Apply LIN config
            LinMode = "master";
            LinBaudrate = ldf.BaudRate;

            // Apply schedule table
            if (importDlg.SelectedScheduleTable != null)
            {
                ScheduleEntries.Clear();
                foreach (var entry in importDlg.SelectedScheduleTable.Entries)
                {
                    var frame = ldf.Frames.FirstOrDefault(f => f.Name == entry.FrameName);
                    if (frame == null) continue;

                    ScheduleEntries.Add(new EditableScheduleEntry
                    {
                        Id = frame.Id,
                        Dlc = frame.Size,
                        Direction = frame.DirectionForMaster(ldf.MasterNode),
                        IntervalMs = (ushort)Math.Max(1, entry.DelayMs),
                    });
                }
            }

            // Create one CAN mapping per LIN frame in the selected schedule table
            var scheduledFrameNames = importDlg.SelectedScheduleTable?.Entries
                .Select(e => e.FrameName).Distinct().ToHashSet() ?? [];

            uint nextCanId = 0x200; // Starting CAN ID (user can change later)
            // Find max existing CAN ID to avoid conflicts
            if (CanMappings.Count > 0)
                nextCanId = CanMappings.Max(m => m.CanId) + 1;

            foreach (var frameName in scheduledFrameNames)
            {
                var frame = ldf.Frames.FirstOrDefault(f => f.Name == frameName);
                if (frame == null) continue;

                string dir = frame.DirectionForMaster(ldf.MasterNode) == "publish" ? "control" : "status";

                var em = new EditableCanMapping
                {
                    Name = frame.Name,
                    Direction = dir,
                    CanId = nextCanId++,
                    LinFrameId = frame.Id,
                    UseSignalMode = importDlg.ImportSignals,
                };

                // Import signals if requested
                if (importDlg.ImportSignals)
                {
                    foreach (var fSig in frame.Signals)
                    {
                        var sigDef = LdfParser.GetSignal(ldf, fSig.Name);
                        if (sigDef == null) continue;

                        var enc = LdfParser.GetEncodingForSignal(ldf, fSig.Name);

                        var editable = new EditableSignal
                        {
                            Name = fSig.Name,
                            StartBit = fSig.BitOffset,
                            BitLength = sigDef.BitSize,
                            ByteOrder = "little_endian",
                            IsSigned = false,
                            Factor = 1.0,
                            Offset = 0,
                            MinValue = 0,
                            MaxValue = (1 << Math.Min(sigDef.BitSize, 30)) - 1,
                            Unit = "",
                        };

                        if (enc != null)
                        {
                            var phys = enc.Values.FirstOrDefault(v => v.IsPhysical);
                            if (phys != null)
                            {
                                editable.Factor = phys.Factor;
                                editable.Offset = phys.Offset;
                                editable.MinValue = phys.RawValue;
                                editable.MaxValue = phys.RawMax;
                                if (!string.IsNullOrEmpty(phys.Description))
                                    editable.Unit = phys.Description;
                            }

                            var logicals = enc.Values.Where(v => !v.IsPhysical).ToList();
                            if (logicals.Count > 0)
                            {
                                editable.ValueDescriptions = [];
                                foreach (var lv in logicals)
                                    editable.ValueDescriptions[lv.RawValue.ToString()] = lv.Description;
                            }
                        }

                        em.Signals.Add(editable);
                    }
                }

                CanMappings.Add(em);
            }

            if (CanMappings.Count > 0 && SelectedCanMapping == null)
                SelectedCanMapping = CanMappings[0];

            // Update profile name from LDF if blank
            if (string.IsNullOrWhiteSpace(ProfileName) || ProfileName == "New Profile")
                ProfileName = ldf.SlaveNodes.Count > 0 ? ldf.SlaveNodes[0] : "LDF Import";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"LDF import failed: {ex.Message}", "Import Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // --- Helpers ---

    private static Window? FindProfileEditorWindow()
    {
        foreach (Window w in Application.Current.Windows)
            if (w is Views.ProfileEditorWindow && w.IsActive)
                return w;
        return Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
            ?? Application.Current.MainWindow;
    }

    private static EditableSignal SignalToEditable(ProfileSignal s) => new()
    {
        Name = s.Name,
        StartBit = s.StartBit,
        BitLength = s.BitLength,
        ByteOrder = s.ByteOrder,
        IsSigned = s.IsSigned,
        Factor = s.Factor,
        Offset = s.Offset,
        MinValue = s.MinValue,
        MaxValue = s.MaxValue,
        Unit = s.Unit,
        ValueDescriptions = s.ValueDescriptions,
    };

    private static ProfileSignal EditableToSignal(EditableSignal s) => new()
    {
        Name = s.Name,
        StartBit = s.StartBit,
        BitLength = s.BitLength,
        ByteOrder = s.ByteOrder,
        IsSigned = s.IsSigned,
        Factor = s.Factor,
        Offset = s.Offset,
        MinValue = s.MinValue,
        MaxValue = s.MaxValue,
        Unit = s.Unit,
        ValueDescriptions = s.ValueDescriptions,
    };

    private static EditableSignal DbcSignalToEditable(DbcSignal s) => new()
    {
        Name = s.Name,
        StartBit = s.StartBit,
        BitLength = s.BitLength,
        ByteOrder = s.IsLittleEndian ? "little_endian" : "big_endian",
        IsSigned = s.IsSigned,
        Factor = s.Factor,
        Offset = s.Offset,
        MinValue = s.MinValue,
        MaxValue = s.MaxValue,
        Unit = s.Unit,
        ValueDescriptions = s.ValueDescriptions.Count > 0
            ? s.ValueDescriptions.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value)
            : null,
    };
}
