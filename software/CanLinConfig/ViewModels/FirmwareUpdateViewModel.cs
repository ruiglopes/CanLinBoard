using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanLinConfig.Models;
using CanLinConfig.Services;

namespace CanLinConfig.ViewModels;

public partial class FirmwareUpdateViewModel : ObservableObject
{
    private readonly MainViewModel _mainVm;
    private readonly FirmwareUpdateSettings _settings;
    private CancellationTokenSource? _cts;
    private byte[]? _firmware;
    private DfwContainer? _dfwFile;
    private byte[]? _hmacKey;

    [ObservableProperty] private string _firmwarePath = "";
    [ObservableProperty] private string _firmwareStatus = "";
    [ObservableProperty] private bool _firmwareValid;
    [ObservableProperty] private string _keyStatus = "";
    [ObservableProperty] private bool _keyLoaded;
    [ObservableProperty] private bool _keyRequired;
    [ObservableProperty] private string _keyHexInput = "";
    [ObservableProperty] private bool _useHexInput;
    [ObservableProperty] private bool _rememberKeyPath;
    [ObservableProperty] private string _selectedBitrate = "500000";
    [ObservableProperty] private bool _deltaUpdate = true;
    [ObservableProperty] private string _targetBankText = "";

    // Progress
    [ObservableProperty] private string _stageText = "";
    [ObservableProperty] private string _counterText = "";
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private string _logText = "";

    // State
    [ObservableProperty] private bool _isFlashing;
    [ObservableProperty] private bool _canStart;
    [ObservableProperty] private bool _canClose = true;

    public string[] BitrateOptions { get; } = ["125000", "250000", "500000", "1000000"];

    public FirmwareUpdateViewModel(MainViewModel mainVm)
    {
        _mainVm = mainVm;
        _settings = FirmwareUpdateSettings.Load();

        // Restore settings
        SelectedBitrate = _settings.BootloaderBitrate.ToString();
        if (_settings.LastKeyFilePath != null && File.Exists(_settings.LastKeyFilePath))
            LoadKeyFile(_settings.LastKeyFilePath);

        RememberKeyPath = _settings.LastKeyFilePath != null;
    }

    [RelayCommand]
    private void BrowseFirmware()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Firmware (*.dfw;*.bin)|*.dfw;*.bin|Dual-Bank (*.dfw)|*.dfw|Binary (*.bin)|*.bin|All files (*.*)|*.*",
            Title = "Select Firmware File"
        };
        if (dialog.ShowDialog() != true) return;
        LoadFirmwareFile(dialog.FileName);
    }

    private void LoadFirmwareFile(string path)
    {
        FirmwarePath = path;
        _firmware = null;
        _dfwFile = null;
        FirmwareValid = false;

        try
        {
            if (path.EndsWith(".dfw", StringComparison.OrdinalIgnoreCase))
            {
                var data = File.ReadAllBytes(path);
                var error = DfwContainer.Validate(data);
                if (error != null)
                {
                    FirmwareStatus = $"Error: {error}";
                    UpdateCanStart();
                    return;
                }
                _dfwFile = DfwContainer.TryParse(data);
                FirmwareStatus = $"Dual-bank v{_dfwFile!.VersionString} — " +
                    $"Bank A ({_dfwFile.BankAFirmware.Length:N0} bytes) + " +
                    $"Bank B ({_dfwFile.BankBFirmware.Length:N0} bytes)";
                FirmwareValid = true;
            }
            else
            {
                var header = AppHeader.TryParse(path);
                if (header == null)
                {
                    FirmwareStatus = "Error: Failed to read firmware file";
                    UpdateCanStart();
                    return;
                }
                if (!header.MagicValid) { FirmwareStatus = "Error: Not a valid firmware file (bad magic)"; UpdateCanStart(); return; }
                if (!header.CrcValid) { FirmwareStatus = "Error: CRC mismatch — file may be corrupted"; UpdateCanStart(); return; }
                if (!header.SizeValid) { FirmwareStatus = "Error: Invalid firmware size"; UpdateCanStart(); return; }
                if (!header.EntryPointValid) { FirmwareStatus = "Error: Invalid entry point"; UpdateCanStart(); return; }

                _firmware = header.FullBinary;
                FirmwareStatus = $"v{header.VersionString} — {header.Size:N0} bytes, CRC 0x{header.Crc32:X8}, {(header.HasSignature ? "Signed" : "Unsigned")}";
                FirmwareValid = true;
            }

            // Don't persist firmware path — user should select each time
        }
        catch (Exception ex)
        {
            FirmwareStatus = $"Error: {ex.Message}";
        }
        UpdateCanStart();
    }

    [RelayCommand]
    private void BrowseKey()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Key files (*.key;*.bin)|*.key;*.bin|All files (*.*)|*.*",
            Title = "Select HMAC Key File"
        };
        if (dialog.ShowDialog() != true) return;
        LoadKeyFile(dialog.FileName);
    }

    private void LoadKeyFile(string path)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            if (data.Length != 32)
            {
                KeyStatus = $"Error: Key must be exactly 32 bytes (got {data.Length})";
                _hmacKey = null;
                KeyLoaded = false;
                UpdateCanStart();
                return;
            }
            _hmacKey = data;
            KeyStatus = $"Key loaded: {Path.GetFileName(path)} (32 bytes)";
            KeyLoaded = true;

            if (RememberKeyPath)
            {
                _settings.LastKeyFilePath = path;
                _settings.Save();
            }
        }
        catch (Exception ex)
        {
            KeyStatus = $"Error: {ex.Message}";
            _hmacKey = null;
            KeyLoaded = false;
        }
        UpdateCanStart();
    }

    partial void OnKeyHexInputChanged(string value)
    {
        if (!UseHexInput) return;
        var hex = value.Replace(" ", "").Replace("-", "");
        if (hex.Length == 64 && hex.All(c => Uri.IsHexDigit(c)))
        {
            _hmacKey = Convert.FromHexString(hex);
            KeyStatus = "Key loaded from hex input (32 bytes)";
            KeyLoaded = true;
        }
        else
        {
            _hmacKey = null;
            KeyLoaded = false;
            KeyStatus = hex.Length > 0 ? $"Need 64 hex chars (got {hex.Length})" : "";
        }
        UpdateCanStart();
    }

    partial void OnSelectedBitrateChanged(string value)
    {
        if (uint.TryParse(value, out uint br))
        {
            _settings.BootloaderBitrate = br;
            _settings.Save();
        }
    }

    private void UpdateCanStart()
    {
        CanStart = FirmwareValid && !IsFlashing && (!KeyRequired || KeyLoaded);
    }

    private void AppendLog(string message)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            LogText += $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n";
        });
    }

    [RelayCommand]
    private async Task StartFlash()
    {
        if (!FirmwareValid) return;

        IsFlashing = true;
        CanStart = false;
        CanClose = false;
        LogText = "";
        _cts = new CancellationTokenSource();

        var options = new FlashOptions(
            AdapterType: _mainVm.SelectedAdapter,
            ChannelName: _mainVm.SelectedChannel,
            OriginalBitrate: uint.TryParse(_mainVm.SelectedBitrate, out uint br) ? br : 500000,
            BootloaderBitrate: uint.TryParse(SelectedBitrate, out uint blBr) ? blBr : 500000,
            Firmware: _firmware,
            DfwFile: _dfwFile,
            HmacKey: _hmacKey,
            DeltaUpdate: DeltaUpdate,
            WasConnected: _mainVm.IsConnected);

        // Disconnect config tool if connected
        if (_mainVm.IsConnected)
        {
            AppendLog("Entering bootloader mode...");
            await _mainVm.DisconnectForFirmwareUpdate();
            await Task.Delay(500);
        }

        var service = new FirmwareUpdateService
        {
            Log = AppendLog,
            OnBitrateScanPrompt = (failedBitrate) =>
            {
                var result = MessageBox.Show(
                    $"Connection failed at {failedBitrate / 1000} kbps.\nTry other standard bitrates?",
                    "Bitrate Scan", MessageBoxButton.YesNo, MessageBoxImage.Question);
                return Task.FromResult(result == MessageBoxResult.Yes);
            },
            OnResumePrompt = (bytesReceived) =>
            {
                var result = MessageBox.Show(
                    $"Previous transfer interrupted at {bytesReceived:N0} bytes.\nResume from last checkpoint?",
                    "Resume Transfer", MessageBoxButton.YesNo, MessageBoxImage.Question);
                return Task.FromResult(result == MessageBoxResult.Yes);
            }
        };

        var progressHandler = new Progress<FlashProgress>(p =>
        {
            StageText = p.Message;
            IsIndeterminate = p.IsIndeterminate;
            if (!p.IsIndeterminate && p.Total > 0)
            {
                ProgressValue = (double)p.Current / p.Total * 100;
                CounterText = p.Stage switch
                {
                    FlashStage.Erasing => $"Sector {p.Current}/{p.Total}",
                    FlashStage.Flashing => $"{p.Current:N0} / {p.Total:N0} bytes",
                    _ => ""
                };
            }
            else
            {
                CounterText = "";
            }

            if (p.Stage == FlashStage.Complete || p.Stage == FlashStage.Failed)
            {
                IsFlashing = false;
                CanClose = true;
                CanStart = p.Stage == FlashStage.Failed && FirmwareValid;
            }
        });

        try
        {
            await service.FlashAsync(options, progressHandler, _cts.Token);

            // Reconnect config tool
            if (options.WasConnected)
            {
                AppendLog("Reconnecting config tool...");
                await Task.Delay(1000); // Wait for app to boot
                var reconnected = await _mainVm.ReconnectAfterFirmwareUpdate();
                if (reconnected)
                    AppendLog("Config tool reconnected");
                else
                    AppendLog("Reconnect failed — reconnect manually");
            }
        }
        catch (OperationCanceledException)
        {
            // Already handled in service
        }
        catch (Exception ex)
        {
            AppendLog($"Flash failed: {ex.Message}");
        }
        finally
        {
            IsFlashing = false;
            CanClose = true;
            UpdateCanStart();
        }
    }

    [RelayCommand]
    private void CancelFlash()
    {
        _cts?.Cancel();
    }
}
