using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanLinConfig.Adapters;
using CanLinConfig.Protocol;
using CanLinConfig.Services;

namespace CanLinConfig.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private ICanAdapter? _adapter;
    private ConfigProtocol? _protocol;

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

    public ConfigProtocol? Protocol => _protocol;

    public MainViewModel()
    {
        CanConfig = new CanConfigViewModel(this);
        LinConfig = new LinConfigViewModel(this);
        Routing = new RoutingViewModel(this);
        DiagConfig = new DiagConfigViewModel(this);
        Diagnostics = new DiagnosticsViewModel(this);
        Profiles = new ProfilesViewModel(this);

        RefreshChannels();
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
        _protocol.RawFrameReceived += (_, e) => Diagnostics.OnRawFrame(e.Frame);

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
    }

    private void Disconnect()
    {
        Diagnostics.StopMonitoring();
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
            StatusBarText = "Defaults loaded - click Read All to refresh UI";
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
        GC.SuppressFinalize(this);
    }
}
