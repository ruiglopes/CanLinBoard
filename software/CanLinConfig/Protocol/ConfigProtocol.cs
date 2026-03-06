using CanLinConfig.Adapters;
using CanLinConfig.Helpers;

namespace CanLinConfig.Protocol;

public record ConnectResult(bool Success, byte Major, byte Minor, byte Patch, ushort ConfigSize, byte RuleCount);
public record StatusResult(bool Success, byte Status);
public record GetStatusResult(bool Success, uint WriteCount);
public record ReadParamResult(bool Success, byte Section, byte Param, byte Sub, byte[] Value);

public class ConfigProtocol : IDisposable
{
    private readonly ICanAdapter _adapter;
    private readonly SemaphoreSlim _cmdLock = new(1, 1);
    private TaskCompletionSource<CanFrame>? _pendingResponse;
    private byte _expectedCmd;
    private readonly TimeSpan _defaultTimeout = TimeSpan.FromMilliseconds(500);
    private readonly TimeSpan _longTimeout = TimeSpan.FromMilliseconds(5000);

    // Bulk read state
    private TaskCompletionSource<byte[]>? _bulkReadTcs;
    private byte[]? _bulkReadBuffer;
    private int _bulkReadExpectedSize;
    private uint _bulkReadExpectedCrc;
    private int _bulkReadReceived;
    private int _bulkReadSeq;

    public event EventHandler<CanFrameEventArgs>? RawFrameReceived;

    public ConfigProtocol(ICanAdapter adapter)
    {
        _adapter = adapter;
        _adapter.FrameReceived += OnFrameReceived;
    }

    private void OnFrameReceived(object? sender, CanFrameEventArgs e)
    {
        RawFrameReceived?.Invoke(this, e);

        if (e.Frame.Id == ProtocolConstants.ConfigRespId)
        {
            if (_pendingResponse != null && e.Frame.Data[0] == _expectedCmd)
            {
                _pendingResponse.TrySetResult(e.Frame);
            }
        }
        else if (e.Frame.Id == ProtocolConstants.ConfigBulkRespId)
        {
            HandleBulkReadData(e.Frame);
        }
    }

    private void HandleBulkReadData(CanFrame frame)
    {
        if (_bulkReadBuffer == null || _bulkReadTcs == null) return;

        byte seq = frame.Data[0];
        if (seq != _bulkReadSeq)
        {
            _bulkReadTcs.TrySetException(new InvalidOperationException($"Bulk read sequence error: expected {_bulkReadSeq}, got {seq}"));
            return;
        }

        int payloadLen = Math.Min(frame.Dlc - 1, _bulkReadExpectedSize - _bulkReadReceived);
        if (payloadLen > 0)
        {
            Array.Copy(frame.Data, 1, _bulkReadBuffer, _bulkReadReceived, payloadLen);
            _bulkReadReceived += payloadLen;
        }
        _bulkReadSeq = (_bulkReadSeq + 1) & 0xFF;

        if (_bulkReadReceived >= _bulkReadExpectedSize)
        {
            uint actualCrc = Crc32.Compute(_bulkReadBuffer, 0, _bulkReadExpectedSize);
            if (actualCrc != _bulkReadExpectedCrc)
            {
                _bulkReadTcs.TrySetException(new InvalidOperationException("Bulk read CRC mismatch"));
            }
            else
            {
                var result = new byte[_bulkReadExpectedSize];
                Array.Copy(_bulkReadBuffer, result, _bulkReadExpectedSize);
                _bulkReadTcs.TrySetResult(result);
            }
        }
    }

    private CanFrame MakeCmd(byte cmd, params byte[] payload)
    {
        var data = new byte[8];
        data[0] = cmd;
        for (int i = 0; i < payload.Length && i < 7; i++)
            data[i + 1] = payload[i];
        return new CanFrame { Id = ProtocolConstants.ConfigCmdId, Dlc = (byte)(1 + payload.Length), Data = data };
    }

    private async Task<CanFrame?> SendCommandAsync(byte cmd, byte[] payload, TimeSpan timeout)
    {
        await _cmdLock.WaitAsync();
        try
        {
            _expectedCmd = cmd;
            _pendingResponse = new TaskCompletionSource<CanFrame>(TaskCreationOptions.RunContinuationsAsynchronously);

            var frame = MakeCmd(cmd, payload);
            if (!_adapter.Send(frame))
                return null;

            using var cts = new CancellationTokenSource(timeout);
            cts.Token.Register(() => _pendingResponse.TrySetCanceled());

            try
            {
                return await _pendingResponse.Task;
            }
            catch (TaskCanceledException)
            {
                return null;
            }
        }
        finally
        {
            _pendingResponse = null;
            _cmdLock.Release();
        }
    }

    public async Task<ConnectResult> ConnectAsync()
    {
        var resp = await SendCommandAsync(ProtocolConstants.CmdConnect, [], _defaultTimeout);
        if (resp == null || resp.Data[1] != ProtocolConstants.StatusOk)
            return new ConnectResult(false, 0, 0, 0, 0, 0);

        return new ConnectResult(
            true,
            resp.Data[2],  // major
            resp.Data[3],  // minor
            resp.Data[4],  // patch
            (ushort)(resp.Data[5] | (resp.Data[6] << 8)),  // config size
            resp.Data[7]   // rule count
        );
    }

    public async Task<StatusResult> SaveAsync()
    {
        var resp = await SendCommandAsync(ProtocolConstants.CmdSave, [], _longTimeout);
        if (resp == null) return new StatusResult(false, 0xFF);
        return new StatusResult(resp.Data[1] == ProtocolConstants.StatusOk, resp.Data[1]);
    }

    public async Task<StatusResult> LoadDefaultsAsync()
    {
        var resp = await SendCommandAsync(ProtocolConstants.CmdDefaults, [], _defaultTimeout);
        if (resp == null) return new StatusResult(false, 0xFF);
        return new StatusResult(resp.Data[1] == ProtocolConstants.StatusOk, resp.Data[1]);
    }

    public async Task<StatusResult> RebootAsync()
    {
        var resp = await SendCommandAsync(ProtocolConstants.CmdReboot, [], _defaultTimeout);
        if (resp == null) return new StatusResult(false, 0xFF);
        return new StatusResult(resp.Data[1] == ProtocolConstants.StatusOk, resp.Data[1]);
    }

    public async Task<StatusResult> EnterBootloaderAsync()
    {
        var key = ProtocolConstants.ResetUnlockKey;
        var payload = new byte[]
        {
            (byte)(key),
            (byte)(key >> 8),
            (byte)(key >> 16),
            (byte)(key >> 24),
        };
        var resp = await SendCommandAsync(ProtocolConstants.CmdEnterBootloader, payload, _defaultTimeout);
        if (resp == null) return new StatusResult(false, 0xFF);
        return new StatusResult(resp.Data[1] == ProtocolConstants.StatusOk, resp.Data[1]);
    }

    public async Task<GetStatusResult> GetStatusAsync()
    {
        var resp = await SendCommandAsync(ProtocolConstants.CmdGetStatus, [], _defaultTimeout);
        if (resp == null) return new GetStatusResult(false, 0);
        if (resp.Data[1] != ProtocolConstants.StatusOk) return new GetStatusResult(false, 0);

        uint wc = (uint)(resp.Data[2] | (resp.Data[3] << 8) | (resp.Data[4] << 16) | (resp.Data[5] << 24));
        return new GetStatusResult(true, wc);
    }

    public async Task<ReadParamResult> ReadParamAsync(byte section, byte param, byte sub)
    {
        var resp = await SendCommandAsync(ProtocolConstants.CmdReadParam, [section, param, sub], _defaultTimeout);
        if (resp == null) return new ReadParamResult(false, section, param, sub, []);
        if (resp.Data[1] != ProtocolConstants.StatusOk) return new ReadParamResult(false, section, param, sub, []);

        // Value starts at data[5], echo at data[2..4]
        int valueLen = resp.Dlc - 5;
        var value = new byte[valueLen > 0 ? valueLen : 0];
        if (valueLen > 0)
            Array.Copy(resp.Data, 5, value, 0, valueLen);

        return new ReadParamResult(true, resp.Data[2], resp.Data[3], resp.Data[4], value);
    }

    public async Task<StatusResult> WriteParamAsync(byte section, byte param, byte sub, byte[] value)
    {
        var payload = new byte[3 + value.Length];
        payload[0] = section;
        payload[1] = param;
        payload[2] = sub;
        Array.Copy(value, 0, payload, 3, value.Length);

        var resp = await SendCommandAsync(ProtocolConstants.CmdWriteParam, payload, _defaultTimeout);
        if (resp == null) return new StatusResult(false, 0xFF);
        return new StatusResult(resp.Data[1] == ProtocolConstants.StatusOk, resp.Data[1]);
    }

    public async Task<StatusResult> BulkWriteAsync(byte section, byte sub, byte[] data)
    {
        uint crc = Crc32.Compute(data);
        uint crc24 = crc & 0x00FFFFFF;

        // BULK_START: [section] [sub] [size_lo] [size_hi] [crc_b0] [crc_b1] [crc_b2]
        var startPayload = new byte[]
        {
            section, sub,
            (byte)(data.Length), (byte)(data.Length >> 8),
            (byte)(crc24), (byte)(crc24 >> 8), (byte)(crc24 >> 16),
        };

        var startResp = await SendCommandAsync(ProtocolConstants.CmdBulkStart, startPayload, _defaultTimeout);
        if (startResp == null || startResp.Data[1] != ProtocolConstants.StatusOk)
            return new StatusResult(false, startResp?.Data[1] ?? 0xFF);

        // Send data frames on 0x602
        int offset = 0;
        byte seq = 0;
        while (offset < data.Length)
        {
            var frame = new CanFrame { Id = ProtocolConstants.ConfigDataId };
            frame.Data[0] = seq++;
            int chunk = Math.Min(7, data.Length - offset);
            Array.Copy(data, offset, frame.Data, 1, chunk);
            frame.Dlc = (byte)(1 + chunk);
            offset += chunk;

            if (!_adapter.Send(frame))
            {
                await Task.Delay(5);
                _adapter.Send(frame); // retry once
            }
            await Task.Delay(2); // pace to avoid TX overflow
        }

        // BULK_END
        await Task.Delay(10);
        var endResp = await SendCommandAsync(ProtocolConstants.CmdBulkEnd, [], _longTimeout);
        if (endResp == null) return new StatusResult(false, 0xFF);
        return new StatusResult(endResp.Data[1] == ProtocolConstants.StatusOk, endResp.Data[1]);
    }

    public async Task<byte[]?> BulkReadAsync(byte section, byte sub)
    {
        // Step 1: BULK_READ — request metadata (size + CRC), firmware prepares data
        var resp = await SendCommandAsync(ProtocolConstants.CmdBulkRead, [section, sub], _defaultTimeout);
        if (resp == null || resp.Data[1] != ProtocolConstants.StatusOk)
            return null;

        // Parse header: [cmd] [status] [size_lo] [size_hi] [crc_b0..b3]
        int size = resp.Data[2] | (resp.Data[3] << 8);
        uint crc = (uint)(resp.Data[4] | (resp.Data[5] << 8) |
                          (resp.Data[6] << 16) | (resp.Data[7] << 24));

        if (size == 0)
            return [];

        // Step 2: BULK_READ_DATA — allocate buffer, then tell firmware to stream
        await _cmdLock.WaitAsync();
        try
        {
            _bulkReadBuffer = new byte[size];
            _bulkReadExpectedSize = size;
            _bulkReadExpectedCrc = crc;
            _bulkReadReceived = 0;
            _bulkReadSeq = 0;
            _bulkReadTcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

            _expectedCmd = ProtocolConstants.CmdBulkReadData;
            _pendingResponse = new TaskCompletionSource<CanFrame>(TaskCreationOptions.RunContinuationsAsynchronously);

            var cmdFrame = MakeCmd(ProtocolConstants.CmdBulkReadData);
            if (!_adapter.Send(cmdFrame))
                return null;

            using var cts = new CancellationTokenSource(_longTimeout);
            cts.Token.Register(() => _pendingResponse.TrySetCanceled());
            cts.Token.Register(() => _bulkReadTcs.TrySetCanceled());

            // Wait for ACK response
            CanFrame ackResp;
            try
            {
                ackResp = await _pendingResponse.Task;
            }
            catch (TaskCanceledException)
            {
                return null;
            }
            finally
            {
                _pendingResponse = null;
            }

            if (ackResp.Data[1] != ProtocolConstants.StatusOk)
                return null;

            // Wait for all data frames on 0x603
            try
            {
                return await _bulkReadTcs.Task;
            }
            catch (TaskCanceledException)
            {
                return null;
            }
        }
        finally
        {
            _bulkReadTcs = null;
            _bulkReadBuffer = null;
            _cmdLock.Release();
        }
    }

    public record DeviceSizes(int RoutingRuleSize, int LinEntrySize, int LinTableSize);

    public async Task<DeviceSizes?> QueryDeviceSizesAsync()
    {
        var r0 = await ReadParamAsync(ProtocolConstants.SectionDevice, ProtocolConstants.DeviceParamRoutingRuleSize, 0);
        var r1 = await ReadParamAsync(ProtocolConstants.SectionDevice, ProtocolConstants.DeviceParamLinEntrySize, 0);
        var r2 = await ReadParamAsync(ProtocolConstants.SectionDevice, ProtocolConstants.DeviceParamLinTableSize, 0);

        if (!r0.Success || r0.Value.Length < 2 ||
            !r1.Success || r1.Value.Length < 2 ||
            !r2.Success || r2.Value.Length < 2)
            return null;

        return new DeviceSizes(
            r0.Value[0] | (r0.Value[1] << 8),
            r1.Value[0] | (r1.Value[1] << 8),
            r2.Value[0] | (r2.Value[1] << 8));
    }

    public void Dispose()
    {
        _adapter.FrameReceived -= OnFrameReceived;
        _cmdLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
