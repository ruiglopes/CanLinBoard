namespace CanLinConfig.Adapters;

/// <summary>
/// Vector XL Driver Library adapter stub. Requires Vector XL Driver Library installed.
/// Uses P/Invoke to vxlapi64.dll (or vxlapi.dll on x86).
/// </summary>
public class VectorXlAdapter : ICanAdapter
{
    private bool _connected;
    private long _portHandle = -1;
    private ulong _accessMask;
    private ulong _permissionMask;
    private Thread? _rxThread;
    private volatile bool _rxRunning;

    public bool IsConnected => _connected;
    public string AdapterName => "Vector XL";
#pragma warning disable CS0067 // Stub adapter - event invoked in full implementation
    public event EventHandler<CanFrameEventArgs>? FrameReceived;
#pragma warning restore CS0067

    public IReadOnlyList<string> GetAvailableChannels()
    {
        try
        {
            VectorNative.xlOpenDriver();
            var channels = new List<string>();
            // Enumerate up to 8 channels
            for (int i = 0; i < 8; i++)
                channels.Add($"Vector_CH{i}");
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
            int chIdx = 0;
            if (channel.StartsWith("Vector_CH") && int.TryParse(channel[9..], out int idx))
                chIdx = idx;

            VectorNative.xlOpenDriver();

            _accessMask = 1UL << chIdx;
            _permissionMask = _accessMask;

            int status = VectorNative.xlOpenPort(ref _portHandle, "CanLinConfig", _accessMask,
                ref _permissionMask, 256, 3 /* XL_INTERFACE_VERSION */, 1 /* XL_BUS_TYPE_CAN */);
            if (status != 0) return Task.FromResult(false);

            // Set bitrate
            VectorNative.xlCanSetChannelBitrate(_portHandle, _accessMask, bitrate);

            // Activate channel
            status = VectorNative.xlActivateChannel(_portHandle, _accessMask, 1 /* XL_BUS_TYPE_CAN */, 0);
            if (status != 0) return Task.FromResult(false);

            _connected = true;
            _rxRunning = true;
            _rxThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "Vector_RX" };
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

        if (_connected && _portHandle >= 0)
        {
            try
            {
                VectorNative.xlDeactivateChannel(_portHandle, _accessMask);
                VectorNative.xlClosePort(_portHandle);
            }
            catch { }
        }
        _connected = false;
        _portHandle = -1;
    }

    public bool Send(CanFrame frame)
    {
        if (!_connected) return false;
        try
        {
            // Simplified: uses xlCanTransmit (XL Driver Lib 20.x+)
            var xlEvent = new byte[64]; // xl_event struct placeholder
            // In a full implementation, properly marshal the xl_can_msg_t struct
            // For now, this is a stub that will need real struct marshaling
            return false; // Stub - needs proper implementation with struct marshaling
        }
        catch
        {
            return false;
        }
    }

    private void ReceiveLoop()
    {
        while (_rxRunning)
        {
            try
            {
                // Simplified stub - proper implementation needs xlReceive with event marshaling
                Thread.Sleep(10);
            }
            catch
            {
                if (_rxRunning) Thread.Sleep(50);
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
/// P/Invoke declarations for Vector XL Driver Library.
/// </summary>
internal static class VectorNative
{
    private const string DllName = "vxlapi64.dll";

    [System.Runtime.InteropServices.DllImport(DllName, CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
    public static extern int xlOpenDriver();

    [System.Runtime.InteropServices.DllImport(DllName)]
    public static extern int xlCloseDriver();

    [System.Runtime.InteropServices.DllImport(DllName, CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
    public static extern int xlOpenPort(ref long portHandle, string userName, ulong accessMask,
        ref ulong permissionMask, int rxQueueSize, int xlInterfaceVersion, int busType);

    [System.Runtime.InteropServices.DllImport(DllName)]
    public static extern int xlClosePort(long portHandle);

    [System.Runtime.InteropServices.DllImport(DllName)]
    public static extern int xlActivateChannel(long portHandle, ulong accessMask, int busType, int flags);

    [System.Runtime.InteropServices.DllImport(DllName)]
    public static extern int xlDeactivateChannel(long portHandle, ulong accessMask);

    [System.Runtime.InteropServices.DllImport(DllName)]
    public static extern int xlCanSetChannelBitrate(long portHandle, ulong accessMask, uint bitrate);
}
