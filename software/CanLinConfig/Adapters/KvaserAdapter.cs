namespace CanLinConfig.Adapters;

/// <summary>
/// Kvaser CANlib adapter stub. Requires Kvaser CANlib SDK installed.
/// The actual DLL (Kvaser.CanLib.dll) is loaded dynamically to gracefully handle
/// systems without the Kvaser driver installed.
/// </summary>
public class KvaserAdapter : ICanAdapter
{
    private bool _connected;
    private int _handle = -1;
    private Thread? _rxThread;
    private volatile bool _rxRunning;

    public bool IsConnected => _connected;
    public string AdapterName => "Kvaser";
    public event EventHandler<CanFrameEventArgs>? FrameReceived;

    public IReadOnlyList<string> GetAvailableChannels()
    {
        try
        {
            // Try to load Kvaser CANlib
            KvaserNative.canInitializeLibrary();
            int count = 0;
            KvaserNative.canGetNumberOfChannels(ref count);
            var channels = new List<string>();
            for (int i = 0; i < count; i++)
                channels.Add($"Kvaser_CH{i}");
            return channels;
        }
        catch (DllNotFoundException)
        {
            return [];
        }
        catch
        {
            return [];
        }
    }

    public Task<bool> ConnectAsync(string channel, uint bitrate)
    {
        if (_connected) Disconnect();

        try
        {
            // Parse channel index from "Kvaser_CHx"
            int chIdx = 0;
            if (channel.StartsWith("Kvaser_CH") && int.TryParse(channel[9..], out int idx))
                chIdx = idx;

            KvaserNative.canInitializeLibrary();
            _handle = KvaserNative.canOpenChannel(chIdx, 0);
            if (_handle < 0) return Task.FromResult(false);

            // Set bus params
            int freq = (int)bitrate;
            int tseg1, tseg2, sjw;
            switch (bitrate)
            {
                case 125000:  tseg1 = 11; tseg2 = 4; sjw = 1; break;
                case 250000:  tseg1 = 5;  tseg2 = 2; sjw = 1; break;
                case 1000000: tseg1 = 5;  tseg2 = 2; sjw = 1; break;
                default:      tseg1 = 5;  tseg2 = 2; sjw = 1; break; // 500k default params
            }
            KvaserNative.canSetBusParams(_handle, freq, tseg1, tseg2, sjw, 1, 0);
            KvaserNative.canBusOn(_handle);

            _connected = true;
            _rxRunning = true;
            _rxThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "Kvaser_RX" };
            _rxThread.Start();

            return Task.FromResult(true);
        }
        catch (DllNotFoundException)
        {
            return Task.FromResult(false);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public void Disconnect()
    {
        _rxRunning = false;
        _rxThread?.Join(1000);
        _rxThread = null;

        if (_connected && _handle >= 0)
        {
            try
            {
                KvaserNative.canBusOff(_handle);
                KvaserNative.canClose(_handle);
            }
            catch { }
        }
        _connected = false;
        _handle = -1;
    }

    public bool Send(CanFrame frame)
    {
        if (!_connected || _handle < 0) return false;
        try
        {
            int flags = frame.IsExtended ? 0x04 : 0; // canMSG_EXT
            return KvaserNative.canWrite(_handle, (int)frame.Id, frame.Data, frame.Dlc, flags) >= 0;
        }
        catch
        {
            return false;
        }
    }

    private void ReceiveLoop()
    {
        var data = new byte[8];
        while (_rxRunning)
        {
            try
            {
                int id = 0, dlc = 0, flags = 0;
                long timestamp = 0;
                int stat = KvaserNative.canReadWait(_handle, ref id, data, ref dlc, ref flags, ref timestamp, 50);
                if (stat >= 0)
                {
                    var frame = new CanFrame
                    {
                        Id = (uint)id,
                        Dlc = (byte)dlc,
                        IsExtended = (flags & 0x04) != 0,
                        IsRtr = (flags & 0x01) != 0,
                        Timestamp = DateTime.Now,
                    };
                    Array.Copy(data, frame.Data, Math.Min(dlc, 8));
                    FrameReceived?.Invoke(this, new CanFrameEventArgs(frame));
                }
            }
            catch
            {
                if (_rxRunning) Thread.Sleep(10);
            }
        }
    }

    public void Dispose()
    {
        Disconnect();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// P/Invoke declarations for Kvaser CANlib DLL.
/// </summary>
internal static class KvaserNative
{
    private const string DllName = "canlib32.dll";

    [System.Runtime.InteropServices.DllImport(DllName)] public static extern void canInitializeLibrary();
    [System.Runtime.InteropServices.DllImport(DllName)] public static extern int canGetNumberOfChannels(ref int channelCount);
    [System.Runtime.InteropServices.DllImport(DllName)] public static extern int canOpenChannel(int channel, int flags);
    [System.Runtime.InteropServices.DllImport(DllName)] public static extern int canSetBusParams(int handle, int freq, int tseg1, int tseg2, int sjw, int noSamp, int syncMode);
    [System.Runtime.InteropServices.DllImport(DllName)] public static extern int canBusOn(int handle);
    [System.Runtime.InteropServices.DllImport(DllName)] public static extern int canBusOff(int handle);
    [System.Runtime.InteropServices.DllImport(DllName)] public static extern int canClose(int handle);
    [System.Runtime.InteropServices.DllImport(DllName)] public static extern int canWrite(int handle, int id, byte[] msg, int dlc, int flag);
    [System.Runtime.InteropServices.DllImport(DllName)] public static extern int canReadWait(int handle, ref int id, byte[] msg, ref int dlc, ref int flag, ref long time, int timeout);
}
