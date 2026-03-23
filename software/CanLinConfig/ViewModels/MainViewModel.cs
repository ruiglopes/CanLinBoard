using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanLinConfig.Adapters;
using CanLinConfig.Models;
using CanLinConfig.Protocol;
using CanLinConfig.Services;
using Microsoft.Win32;

namespace CanLinConfig.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private ICanAdapter? _adapter;
    private ConfigProtocol? _protocol;
    private MonitorFrameDecoder? _monitorDecoder;
    private readonly ProjectService _projectService = new();
    private readonly AppSettings _appSettings;
    private Project? _currentProject;

    [ObservableProperty] private string _windowTitle = "CanLinConfig";
    [ObservableProperty] private string _connectionStatus = "Disconnected";
    [ObservableProperty] private string _firmwareVersion = "";
    [ObservableProperty] private string _statusBarText = "Ready";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _selectedAdapter = "PCAN";
    [ObservableProperty] private string _selectedChannel = "";
    [ObservableProperty] private string _selectedBitrate = "500000";
    [ObservableProperty] private int _selectedTabIndex;

    public ObservableCollection<string> AvailableAdapters { get; } = ["PCAN", "Kvaser", "Vector XL", "SLCAN"];
    public ObservableCollection<string> AvailableChannels { get; } = [];
    public ObservableCollection<string> AvailableBitrates { get; } = ["125000", "250000", "500000", "1000000"];

    // Sub-ViewModels
    public CanConfigViewModel CanConfig { get; }
    public LinConfigViewModel LinConfig { get; }
    public RoutingViewModel Routing { get; }
    public DiagConfigViewModel DiagConfig { get; }
    public DiagnosticsViewModel Diagnostics { get; }
    public ProfilesViewModel Profiles { get; }
    public BusDataService BusDataService { get; }
    public BusMonitorViewModel BusMonitor { get; }

    public ConfigProtocol? Protocol => _protocol;

    public MainViewModel()
    {
        CanConfig = new CanConfigViewModel(this);
        LinConfig = new LinConfigViewModel(this);
        Routing = new RoutingViewModel(this);
        DiagConfig = new DiagConfigViewModel(this);
        Diagnostics = new DiagnosticsViewModel(this);
        Profiles = new ProfilesViewModel(this);

        var dbManager = new DatabaseManager();
        BusDataService = new BusDataService(dbManager);
        BusMonitor = new BusMonitorViewModel(BusDataService);

        _appSettings = AppSettings.Load();

        RefreshChannels();

        // Auto-load last project if configured
        if (_appSettings.LoadLastProject && !string.IsNullOrEmpty(_appSettings.LastProjectPath)
            && System.IO.File.Exists(_appSettings.LastProjectPath))
        {
            try
            {
                OpenProject(_appSettings.LastProjectPath);
            }
            catch
            {
                // Silently ignore — stale path or corrupt file
            }
        }
    }

    [RelayCommand]
    private void RefreshChannels()
    {
        AvailableChannels.Clear();
        try
        {
            var adapter = CreateAdapter(SelectedAdapter);
            if (adapter != null)
            {
                foreach (var ch in adapter.GetAvailableChannels())
                    AvailableChannels.Add(ch);
                adapter.Dispose();
            }
        }
        catch { }

        if (AvailableChannels.Count > 0)
            SelectedChannel = AvailableChannels[0];
    }

    partial void OnSelectedAdapterChanged(string value) => RefreshChannels();

    [RelayCommand]
    private async Task ToggleConnection()
    {
        if (IsConnected)
        {
            Disconnect();
        }
        else
        {
            await ConnectAsync();
        }
    }

    private async Task ConnectAsync()
    {
        if (string.IsNullOrEmpty(SelectedChannel))
        {
            StatusBarText = "No channel selected";
            return;
        }

        _adapter = CreateAdapter(SelectedAdapter);
        if (_adapter == null)
        {
            StatusBarText = "Failed to create adapter";
            return;
        }

        ConnectionStatus = "Connecting...";

        if (!uint.TryParse(SelectedBitrate, out uint bitrate))
            bitrate = 500000;

        var ok = await _adapter.ConnectAsync(SelectedChannel, bitrate);
        if (!ok)
        {
            ConnectionStatus = "Connection failed";
            StatusBarText = "CAN adapter connection failed";
            _adapter.Dispose();
            _adapter = null;
            return;
        }

        _protocol = new ConfigProtocol(_adapter);
        _protocol.RawFrameReceived += (_, e) =>
        {
            BusDataService.OnCanFrame(e.Frame);
            Diagnostics.OnRawFrame(e.Frame);
        };

        _monitorDecoder = new MonitorFrameDecoder();
        _monitorDecoder.FrameDecoded += (_, busFrame) => BusDataService.OnFrame(busFrame);
        _protocol.MonitorFrameReceived += (_, e) => _monitorDecoder.OnCanFrame(e.Frame);
        BusMonitor.MonitorControl.SetProtocol(_protocol, _monitorDecoder);

        // Try firmware handshake
        var result = await _protocol.ConnectAsync();
        if (!result.Success)
        {
            ConnectionStatus = "No device response";
            StatusBarText = "CONNECT command timed out - check device";
            _protocol.Dispose();
            _protocol = null;
            _adapter.Disconnect();
            _adapter.Dispose();
            _adapter = null;
            return;
        }

        FirmwareVersion = $"v{result.Major}.{result.Minor}.{result.Patch}";
        ConnectionStatus = $"Connected - FW {FirmwareVersion}";
        StatusBarText = $"Connected to device (FW {FirmwareVersion}, config size={result.ConfigSize}, rules={result.RuleCount})";
        IsConnected = true;

        // Query firmware struct sizes for validation
        var sizes = await _protocol.QueryDeviceSizesAsync();
        if (sizes != null)
        {
            Routing.FirmwareRuleSize = sizes.RoutingRuleSize;

            var mismatches = new System.Collections.Generic.List<string>();
            if (sizes.RoutingRuleSize != ProtocolConstants.ExpectedRoutingRuleSize)
                mismatches.Add($"routing_rule_t: fw={sizes.RoutingRuleSize} expected={ProtocolConstants.ExpectedRoutingRuleSize}");
            if (sizes.LinEntrySize != ProtocolConstants.ExpectedLinEntrySize)
                mismatches.Add($"lin_schedule_entry_t: fw={sizes.LinEntrySize} expected={ProtocolConstants.ExpectedLinEntrySize}");
            if (sizes.LinTableSize != ProtocolConstants.ExpectedLinTableSize)
                mismatches.Add($"lin_schedule_table_t: fw={sizes.LinTableSize} expected={ProtocolConstants.ExpectedLinTableSize}");

            if (mismatches.Count > 0)
            {
                var msg = "Struct size mismatch — config tool may corrupt data!\n" + string.Join("\n", mismatches);
                MessageBox.Show(msg, "Firmware Mismatch", MessageBoxButton.OK, MessageBoxImage.Warning);
                StatusBarText = "WARNING: struct size mismatch with firmware";
            }
        }
    }

    private void Disconnect()
    {
        Diagnostics.StopMonitoring();
        BusMonitor.MonitorControl.SetProtocol(null, null);
        _monitorDecoder = null;
        _protocol?.Dispose();
        _protocol = null;
        _adapter?.Disconnect();
        _adapter?.Dispose();
        _adapter = null;
        IsConnected = false;
        ConnectionStatus = "Disconnected";
        FirmwareVersion = "";
        StatusBarText = "Disconnected";
    }

    [RelayCommand]
    private async Task ReadAll()
    {
        if (_protocol == null) return;
        StatusBarText = "Reading all parameters...";
        try
        {
            await CanConfig.ReadFromDeviceAsync(_protocol);
            await LinConfig.ReadFromDeviceAsync(_protocol);
            await DiagConfig.ReadFromDeviceAsync(_protocol);
            await Routing.ReadFromDeviceAsync(_protocol);
            await Profiles.ReadFromDeviceAsync(_protocol);
            StatusBarText = "Read All complete";
        }
        catch (Exception ex)
        {
            StatusBarText = $"Read error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task WriteAll()
    {
        if (_protocol == null) return;
        StatusBarText = "Writing all parameters...";
        try
        {
            await CanConfig.WriteToDeviceAsync(_protocol);
            await LinConfig.WriteToDeviceAsync(_protocol);
            await DiagConfig.WriteToDeviceAsync(_protocol);
            await Routing.WriteToDeviceAsync(_protocol);
            await Profiles.WriteToDeviceAsync(_protocol);
            StatusBarText = "Write All complete";
        }
        catch (Exception ex)
        {
            StatusBarText = $"Write error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveToNvm()
    {
        if (_protocol == null) return;
        StatusBarText = "Saving to NVM...";
        var result = await _protocol.SaveAsync();
        StatusBarText = result.Success ? "Saved to NVM" : $"Save failed (status={result.Status})";
    }

    [RelayCommand]
    private async Task LoadDefaults()
    {
        if (_protocol == null) return;
        var mbResult = MessageBox.Show("Reset device config to factory defaults?", "Load Defaults",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (mbResult != MessageBoxResult.Yes) return;

        StatusBarText = "Loading defaults...";
        var result = await _protocol.LoadDefaultsAsync();
        if (result.Success)
        {
            await ReadAll();
            StatusBarText = "Defaults loaded";
        }
        else
        {
            StatusBarText = $"Load defaults failed (status={result.Status})";
        }
    }

    [RelayCommand]
    private async Task EnterBootloader()
    {
        if (_protocol == null) return;
        var mbResult = MessageBox.Show("Reboot device into bootloader mode?", "Enter Bootloader",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (mbResult != MessageBoxResult.Yes) return;

        StatusBarText = "Entering bootloader...";
        var result = await _protocol.EnterBootloaderAsync();
        if (result.Success)
        {
            StatusBarText = "Device rebooting to bootloader";
            Disconnect();
        }
        else
        {
            StatusBarText = $"Enter bootloader failed (status={result.Status})";
        }
    }

    /// <summary>
    /// Enters bootloader mode and fully tears down the config tool adapter.
    /// Called by FirmwareUpdateService before starting flash.
    /// </summary>
    public async Task DisconnectForFirmwareUpdate()
    {
        if (_protocol != null)
        {
            try
            {
                await _protocol.EnterBootloaderAsync();
            }
            catch { }
        }
        Disconnect();
    }

    /// <summary>
    /// Reconnects the config tool adapter after firmware update.
    /// Returns true if reconnect succeeded.
    /// </summary>
    public async Task<bool> ReconnectAfterFirmwareUpdate()
    {
        try
        {
            await ConnectAsync();
            return IsConnected;
        }
        catch
        {
            return false;
        }
    }

    [RelayCommand]
    private void UpdateFirmware()
    {
        var window = new Views.FirmwareUpdateWindow(this);
        window.Owner = Application.Current.MainWindow;
        window.ShowDialog();
    }

    [RelayCommand]
    private void SaveConfigFile()
    {
        if (ConfigFileService.SaveToFile(this))
            StatusBarText = "Config exported to file";
    }

    [RelayCommand]
    private void LoadConfigFile()
    {
        if (ConfigFileService.LoadFromFile(this))
            StatusBarText = "Config loaded from file - click Write All to push to device";
    }

    // -------------------------------------------------------------------------
    // Project commands
    // -------------------------------------------------------------------------

    [RelayCommand]
    private void NewProject()
    {
        if (!ConfirmUnsavedChanges()) return;

        CloseProjectInternal();

        var state = CaptureCurrentState();
        state.ProjectName = "Untitled";
        _currentProject = _projectService.CreateFromState(state);
        UpdateWindowTitle();
        StatusBarText = "New project created";
    }

    [RelayCommand]
    private void OpenProject()
    {
        if (!ConfirmUnsavedChanges()) return;

        var dlg = new OpenFileDialog
        {
            Filter = "CanLinConfig Projects (*.clpkg)|*.clpkg|All Files (*.*)|*.*",
            Title = "Open Project"
        };
        if (dlg.ShowDialog() != true) return;

        OpenProject(dlg.FileName);
    }

    private void OpenProject(string path)
    {
        try
        {
            CloseProjectInternal();

            _currentProject = _projectService.Open(path);
            var state = _projectService.ToState(_currentProject);
            ApplyState(state);
            BusMonitor.Instruments.FromLayouts(_currentProject.Manifest.Instruments);

            _appSettings.LastProjectPath = path;
            _appSettings.AddRecentProject(path);
            _appSettings.Save();

            UpdateWindowTitle();
            StatusBarText = $"Opened project: {_currentProject.Manifest.Name}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to open project:\n{ex.Message}", "Open Project",
                MessageBoxButton.OK, MessageBoxImage.Error);
            StatusBarText = "Failed to open project";
        }
    }

    [RelayCommand]
    private void SaveProject()
    {
        if (_currentProject == null)
        {
            NewProject();
            if (_currentProject == null) return;
        }

        if (string.IsNullOrEmpty(_currentProject.FilePath))
        {
            SaveProjectAs();
            return;
        }

        SaveProjectToFile(_currentProject.FilePath);
    }

    [RelayCommand]
    private void SaveProjectAs()
    {
        if (_currentProject == null)
        {
            NewProject();
            if (_currentProject == null) return;
        }

        var dlg = new SaveFileDialog
        {
            Filter = "CanLinConfig Projects (*.clpkg)|*.clpkg|All Files (*.*)|*.*",
            Title = "Save Project As",
            DefaultExt = ".clpkg",
            FileName = _currentProject.Manifest.Name
        };
        if (dlg.ShowDialog() != true) return;

        SaveProjectToFile(dlg.FileName);
    }

    private void SaveProjectToFile(string filePath)
    {
        try
        {
            // Update project from current tool state
            var state = CaptureCurrentState();
            state.ProjectName = _currentProject!.Manifest.Name;
            _currentProject = _projectService.CreateFromState(state);
            _currentProject.Manifest.Instruments = BusMonitor.Instruments.ToLayouts().ToList();
            _projectService.Save(_currentProject, filePath);

            _appSettings.LastProjectPath = filePath;
            _appSettings.AddRecentProject(filePath);
            _appSettings.Save();

            UpdateWindowTitle();
            StatusBarText = $"Project saved: {System.IO.Path.GetFileName(filePath)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save project:\n{ex.Message}", "Save Project",
                MessageBoxButton.OK, MessageBoxImage.Error);
            StatusBarText = "Failed to save project";
        }
    }

    [RelayCommand]
    private void CloseProject()
    {
        if (!ConfirmUnsavedChanges()) return;
        CloseProjectInternal();
        StatusBarText = "Project closed";
    }

    private void CloseProjectInternal()
    {
        _projectService.CloseProject();
        _currentProject = null;
        BusMonitor.Instruments.ClearAllCommand.Execute(null);
        UpdateWindowTitle();
    }

    // -------------------------------------------------------------------------
    // Project helpers
    // -------------------------------------------------------------------------

    private ProjectState CaptureCurrentState()
    {
        var dbManager = BusDataService.DatabaseManager;
        return new ProjectState
        {
            AdapterType = SelectedAdapter,
            Channel = SelectedChannel,
            Bitrate = uint.TryParse(SelectedBitrate, out var br) ? br : 500000,
            Can1DbPath = dbManager.GetDatabasePath(BusFrame.Bus.CAN1),
            Can2DbPath = dbManager.GetDatabasePath(BusFrame.Bus.CAN2),
            Lin1DbPath = dbManager.GetDatabasePath(BusFrame.Bus.LIN1),
            Lin2DbPath = dbManager.GetDatabasePath(BusFrame.Bus.LIN2),
            Lin3DbPath = dbManager.GetDatabasePath(BusFrame.Bus.LIN3),
            Lin4DbPath = dbManager.GetDatabasePath(BusFrame.Bus.LIN4),
            GraphTimeWindow = BusMonitor.Graph.TimeWindowSeconds
        };
    }

    private void ApplyState(ProjectState state)
    {
        // Connection settings
        if (!string.IsNullOrEmpty(state.AdapterType) && AvailableAdapters.Contains(state.AdapterType))
            SelectedAdapter = state.AdapterType;

        if (!string.IsNullOrEmpty(state.Channel) && AvailableChannels.Contains(state.Channel))
            SelectedChannel = state.Channel;

        SelectedBitrate = state.Bitrate.ToString();

        // Database assignments
        var dbManager = BusDataService.DatabaseManager;
        ApplyDb(dbManager, BusFrame.Bus.CAN1, state.Can1DbPath);
        ApplyDb(dbManager, BusFrame.Bus.CAN2, state.Can2DbPath);
        ApplyDb(dbManager, BusFrame.Bus.LIN1, state.Lin1DbPath);
        ApplyDb(dbManager, BusFrame.Bus.LIN2, state.Lin2DbPath);
        ApplyDb(dbManager, BusFrame.Bus.LIN3, state.Lin3DbPath);
        ApplyDb(dbManager, BusFrame.Bus.LIN4, state.Lin4DbPath);

        // Update BusMonitor display paths
        BusMonitor.Can1DbPath = string.IsNullOrEmpty(state.Can1DbPath) ? "(none)" : System.IO.Path.GetFileName(state.Can1DbPath);
        BusMonitor.Can2DbPath = string.IsNullOrEmpty(state.Can2DbPath) ? "(none)" : System.IO.Path.GetFileName(state.Can2DbPath);
        BusMonitor.Lin1DbPath = string.IsNullOrEmpty(state.Lin1DbPath) ? "(none)" : System.IO.Path.GetFileName(state.Lin1DbPath);
        BusMonitor.Lin2DbPath = string.IsNullOrEmpty(state.Lin2DbPath) ? "(none)" : System.IO.Path.GetFileName(state.Lin2DbPath);
        BusMonitor.Lin3DbPath = string.IsNullOrEmpty(state.Lin3DbPath) ? "(none)" : System.IO.Path.GetFileName(state.Lin3DbPath);
        BusMonitor.Lin4DbPath = string.IsNullOrEmpty(state.Lin4DbPath) ? "(none)" : System.IO.Path.GetFileName(state.Lin4DbPath);

        // Graph time window
        BusMonitor.Graph.TimeWindowSeconds = state.GraphTimeWindow;
    }

    private static void ApplyDb(DatabaseManager dbManager, BusFrame.Bus bus, string? path)
    {
        if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
            dbManager.AssignDatabase(bus, path);
        else
            dbManager.RemoveDatabase(bus);
    }

    private void UpdateWindowTitle()
    {
        if (_currentProject == null)
        {
            WindowTitle = "CanLinConfig";
            return;
        }

        var name = _currentProject.Manifest.Name;
        var dirty = _currentProject.FilePath == null ? " *" : "";
        WindowTitle = $"CanLinConfig \u2014 {name}{dirty}";
    }

    private bool ConfirmUnsavedChanges()
    {
        if (_currentProject == null || _currentProject.FilePath != null)
            return true;

        // Project exists but has never been saved
        var result = MessageBox.Show(
            "Save changes to the current project?",
            "Unsaved Changes",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        switch (result)
        {
            case MessageBoxResult.Yes:
                SaveProject();
                return true;
            case MessageBoxResult.No:
                return true;
            default: // Cancel
                return false;
        }
    }

    /// <summary>
    /// Called from MainWindow.OnClosing to allow cancellation if there are unsaved changes.
    /// </summary>
    public bool CanClose() => ConfirmUnsavedChanges();

    public bool SendRawFrame(CanFrame frame)
    {
        return _adapter?.Send(frame) ?? false;
    }

    private static ICanAdapter? CreateAdapter(string name) => name switch
    {
        "PCAN" => new PcanAdapter(),
        "Kvaser" => new KvaserAdapter(),
        "Vector XL" => new VectorXlAdapter(),
        "SLCAN" => new SlcanAdapter(),
        _ => null,
    };

    public void Dispose()
    {
        Disconnect();
        _projectService.CloseProject();
        GC.SuppressFinalize(this);
    }
}
