using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanLinConfig.Models;
using CanLinConfig.Services;
using Microsoft.Win32;

namespace CanLinConfig.ViewModels;

public partial class LogReplayViewModel : ObservableObject
{
    private BusDataService? _busDataService;
    private List<BusFrame> _frames = new();
    private DispatcherTimer? _timer;
    private int _currentIndex;
    private DateTime _replayBaseTime;

    // Timestamps from the loaded file (ms offsets from first frame)
    private List<double> _timestampsMs = new();

    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private int _totalFrames;
    [ObservableProperty] private int _currentFrame;
    [ObservableProperty] private double _progress; // 0-100
    [ObservableProperty] private string _statusText = "No file loaded";
    [ObservableProperty] private int _selectedSpeedIndex = 0; // 0=1x

    public string[] SpeedNames { get; } = ["1x", "2x", "5x", "10x"];
    private static readonly double[] SpeedMultipliers = [1.0, 2.0, 5.0, 10.0];

    public void SetBusDataService(BusDataService? service)
    {
        _busDataService = service;
    }

    [RelayCommand]
    private void LoadFile()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "CSV Log Files (*.csv)|*.csv|All Files (*.*)|*.*",
            Title = "Open Log File"
        };

        if (dlg.ShowDialog() != true) return;

        try
        {
            using var stream = File.OpenRead(dlg.FileName);
            _frames = CsvLogImporter.ImportFromStream(stream);

            if (_frames.Count == 0)
            {
                StatusText = "No frames found in file";
                IsLoaded = false;
                return;
            }

            // Extract timestamp offsets in ms relative to first frame
            var baseTime = _frames[0].Timestamp;
            _timestampsMs = _frames.Select(f => (f.Timestamp - baseTime).TotalMilliseconds).ToList();

            TotalFrames = _frames.Count;
            _currentIndex = 0;
            CurrentFrame = 0;
            Progress = 0;
            IsLoaded = true;
            IsPlaying = false;
            StatusText = $"Loaded {_frames.Count} frames from {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            StatusText = $"Error loading file: {ex.Message}";
            IsLoaded = false;
        }

        PlayCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        StopReplayCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private void Play()
    {
        if (_frames.Count == 0 || _busDataService == null) return;

        _replayBaseTime = DateTime.Now;
        IsPlaying = true;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1)
        };
        _timer.Tick += OnTimerTick;
        _timer.Start();

        StatusText = "Playing...";
        PlayCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        StopReplayCommand.NotifyCanExecuteChanged();
    }

    private bool CanPlay() => IsLoaded && !IsPlaying;

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause()
    {
        _timer?.Stop();
        _timer = null;
        IsPlaying = false;
        StatusText = $"Paused at frame {_currentIndex}/{TotalFrames}";
        PlayCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
    }

    private bool CanPause() => IsPlaying;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void StopReplay()
    {
        _timer?.Stop();
        _timer = null;
        IsPlaying = false;
        _currentIndex = 0;
        CurrentFrame = 0;
        Progress = 0;
        StatusText = $"Stopped — {TotalFrames} frames loaded";
        PlayCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        StopReplayCommand.NotifyCanExecuteChanged();
    }

    private bool CanStop() => IsLoaded;

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_currentIndex >= _frames.Count)
        {
            // Replay complete
            _timer?.Stop();
            _timer = null;
            IsPlaying = false;
            StatusText = $"Replay complete — {TotalFrames} frames";
            PlayCommand.NotifyCanExecuteChanged();
            PauseCommand.NotifyCanExecuteChanged();
            return;
        }

        double speed = SpeedMultipliers[SelectedSpeedIndex];
        double wallMs = (DateTime.Now - _replayBaseTime).TotalMilliseconds;

        // Feed all frames whose (scaled) timestamp has been reached
        while (_currentIndex < _frames.Count)
        {
            double frameOffsetMs = _timestampsMs[_currentIndex];
            double targetWallMs = frameOffsetMs / speed;

            if (wallMs < targetWallMs) break;

            _busDataService!.OnFrame(_frames[_currentIndex]);
            _currentIndex++;
            CurrentFrame = _currentIndex;
            Progress = TotalFrames > 0 ? (double)_currentIndex / TotalFrames * 100 : 0;
        }
    }
}
