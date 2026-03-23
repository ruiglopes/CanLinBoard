using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanLinConfig.Helpers;
using CanLinConfig.Models;
using CanLinConfig.Protocol;
using CanLinConfig.Services;
using CanLinConfig.Services.Export;
using Microsoft.Win32;

namespace CanLinConfig.ViewModels;

public partial class LogDownloadViewModel : ObservableObject
{
    private ConfigProtocol? _protocol;
    private BusDataService? _busDataService;

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _downloadProgress; // 0-100
    [ObservableProperty] private string _downloadStatus = "";
    [ObservableProperty] private int _downloadedEntries;
    [ObservableProperty] private bool _hasDownloadedData;

    private List<LogEntry> _downloadedLog = new();
    private CancellationTokenSource? _downloadCts;

    public record LogEntry(
        uint TimestampMs, uint FrameId, byte Bus, byte Dlc, byte[] Data);

    public void SetProtocol(ConfigProtocol? protocol, BusDataService? busDataService)
    {
        _protocol = protocol;
        _busDataService = busDataService;
        IsConnected = protocol != null;
        if (!IsConnected)
        {
            IsDownloading = false;
            DownloadStatus = "";
        }
        DownloadCommand.NotifyCanExecuteChanged();
        CancelDownloadCommand.NotifyCanExecuteChanged();
        ExportLogCommand.NotifyCanExecuteChanged();
        FeedToBusMonitorCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task Download()
    {
        if (_protocol == null) return;

        IsDownloading = true;
        DownloadProgress = 0;
        _downloadedLog.Clear();
        DownloadedEntries = 0;
        _downloadCts = new CancellationTokenSource();

        try
        {
            // Read write_offset (32-bit, split across sub=0 and sub=1)
            uint writeOffset = await ReadUint32ParamAsync(ProtocolConstants.LogParamWriteOffset);
            if (writeOffset == 0)
            {
                DownloadStatus = "Failed to read write offset";
                return;
            }

            // Read wrap count (32-bit, split)
            uint wrapCount = await ReadUint32ParamAsync(ProtocolConstants.LogParamWrapCount);

            // Calculate total bytes to download
            // LOG_DATA_OFFSET on device is 0x021000
            const uint logDataOffset = 0x021000;
            const uint logDataEnd = 0x1000000; // 16 MB
            const uint logDataSize = logDataEnd - logDataOffset;

            uint totalBytes;
            uint startOffset; // relative to LOG_DATA_OFFSET

            if (wrapCount == 0)
            {
                // No wrap — data from LOG_DATA_OFFSET to writeOffset
                totalBytes = writeOffset - logDataOffset;
                startOffset = 0;
            }
            else
            {
                // Wrapped — read entire ring buffer
                totalBytes = logDataSize;
                startOffset = writeOffset - logDataOffset;
            }

            if (totalBytes == 0)
            {
                DownloadStatus = "No log data on device";
                return;
            }

            DownloadStatus = $"Downloading {totalBytes / 1024} KB...";

            // Download in 4 KB chunks
            var allData = new MemoryStream();
            uint downloaded = 0;
            const ushort chunkSize = ProtocolConstants.LogChunkSize;

            // For wrapped ring, start reading from writeOffset (oldest data)
            uint readOffset = startOffset;

            while (downloaded < totalBytes)
            {
                _downloadCts.Token.ThrowIfCancellationRequested();

                ushort thisChunk = (ushort)Math.Min(chunkSize, totalBytes - downloaded);

                // Wrap read offset within ring buffer
                uint actualOffset = readOffset % logDataSize;

                var result = await _protocol.LogReadChunkAsync(actualOffset, thisChunk);
                if (!result.Success)
                {
                    DownloadStatus = $"Chunk read failed at offset 0x{actualOffset:X6} — retrying...";
                    // Retry once
                    result = await _protocol.LogReadChunkAsync(actualOffset, thisChunk);
                    if (!result.Success)
                    {
                        DownloadStatus = $"Download failed at offset 0x{actualOffset:X6}";
                        return;
                    }
                }

                allData.Write(result.Data, 0, result.ActualSize);
                downloaded += result.ActualSize;
                readOffset += result.ActualSize;

                DownloadProgress = (double)downloaded / totalBytes * 100;
                DownloadStatus = $"Downloaded {downloaded / 1024} / {totalBytes / 1024} KB";
            }

            // Parse entries
            var (entries, gapDrops) = ParseLogEntriesWithGaps(allData.ToArray());
            _downloadedLog = entries;
            DownloadedEntries = _downloadedLog.Count;
            HasDownloadedData = _downloadedLog.Count > 0;
            DownloadStatus = gapDrops > 0
                ? $"Complete — {_downloadedLog.Count} entries ({gapDrops} frames dropped)"
                : $"Complete — {_downloadedLog.Count} entries";
        }
        catch (OperationCanceledException)
        {
            DownloadStatus = "Download cancelled";
        }
        finally
        {
            IsDownloading = false;
            _downloadCts = null;
            DownloadCommand.NotifyCanExecuteChanged();
            CancelDownloadCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanDownload() => IsConnected && !IsDownloading;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void CancelDownload()
    {
        _downloadCts?.Cancel();
    }

    private bool CanCancel() => IsDownloading;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private void ExportLog()
    {
        if (_downloadedLog.Count == 0 || _busDataService == null) return;

        // Convert LogEntries to BusFrames
        var frames = new List<BusFrame>();
        DateTime baseTime = DateTime.Now;
        foreach (var entry in _downloadedLog)
        {
            var data = new byte[8];
            Array.Copy(entry.Data, data, Math.Min(entry.Data.Length, 8));
            var bus = (BusFrame.Bus)entry.Bus;
            var timestamp = baseTime.AddMilliseconds(entry.TimestampMs);
            frames.Add(new BusFrame(bus, entry.FrameId, entry.Dlc, data, timestamp, false));
        }

        var dlg = new SaveFileDialog
        {
            Filter = "CSV Files (*.csv)|*.csv|ASC Files (*.asc)|*.asc|BLF Files (*.blf)|*.blf",
            DefaultExt = ".csv"
        };

        if (dlg.ShowDialog() != true) return;

        IFrameExporter exporter = Path.GetExtension(dlg.FileName).ToLowerInvariant() switch
        {
            ".asc" => new AscExporter(),
            ".blf" => new BlfExporter(),
            _ => new CsvExporter()
        };

        using var fs = File.Create(dlg.FileName);
        exporter.Export(fs, frames, _busDataService.DatabaseManager);
        DownloadStatus = $"Exported to {Path.GetFileName(dlg.FileName)}";
    }

    private bool CanExport() => HasDownloadedData;

    [RelayCommand(CanExecute = nameof(CanFeedToBusMonitor))]
    private void FeedToBusMonitor()
    {
        if (_downloadedLog.Count == 0 || _busDataService == null) return;

        DateTime baseTime = DateTime.Now;
        foreach (var entry in _downloadedLog)
        {
            var data = new byte[8];
            Array.Copy(entry.Data, data, Math.Min(entry.Data.Length, 8));
            var bus = (BusFrame.Bus)entry.Bus;
            var timestamp = baseTime.AddMilliseconds(entry.TimestampMs);
            var frame = new BusFrame(bus, entry.FrameId, entry.Dlc, data, timestamp, false);
            _busDataService.OnFrame(frame);
        }

        DownloadStatus = $"Fed {_downloadedLog.Count} frames to Bus Monitor";
    }

    private bool CanFeedToBusMonitor() => HasDownloadedData;

    partial void OnHasDownloadedDataChanged(bool value)
    {
        ExportLogCommand.NotifyCanExecuteChanged();
        FeedToBusMonitorCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Read a 32-bit param split across sub=0 (low16) and sub=1 (high16).
    /// </summary>
    private async Task<uint> ReadUint32ParamAsync(byte param)
    {
        if (_protocol == null) return 0;
        var lo = await _protocol.ReadParamAsync(
            ProtocolConstants.SectionLog, param, 0);
        var hi = await _protocol.ReadParamAsync(
            ProtocolConstants.SectionLog, param, 1);

        ushort loVal = (lo.Success && lo.Value.Length >= 2)
            ? (ushort)(lo.Value[0] | (lo.Value[1] << 8)) : (ushort)0;
        ushort hiVal = (hi.Success && hi.Value.Length >= 2)
            ? (ushort)(hi.Value[0] | (hi.Value[1] << 8)) : (ushort)0;

        return (uint)(loVal | (hiVal << 16));
    }

    /* ---- Log Entry Parsing (public static for testability) ---- */

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

    public static List<LogEntry> ParseLogEntries(byte[] data)
    {
        var entries = new List<LogEntry>();
        const int entrySize = 20;

        for (int i = 0; i + entrySize <= data.Length; i += entrySize)
        {
            byte bus = data[i + 8];

            // Skip gap markers (bus = 0xFF)
            if (bus == 0xFF) continue;

            // Skip erased flash (all 0xFF)
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

        return entries;
    }
}
