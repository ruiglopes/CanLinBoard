using System.Runtime.InteropServices;

namespace CanLinConfig.Adapters;

/// <summary>
/// Vector XL Driver Library adapter. Requires Vector XL Driver Library installed.
/// Uses P/Invoke to vxlapi64.dll.
/// Based on proven working implementation from CanFlashProgrammer/VectorService.
/// </summary>
public class VectorXlAdapter : ICanAdapter
{
    private bool _connected;
    private int _portHandle = -1;  // XLportHandle is C "long" = 4 bytes on Windows = C# int
    private ulong _accessMask;
    private Thread? _rxThread;
    private volatile bool _rxRunning;
    private nint _notifyEvent;
    private readonly object _lock = new();

    private static bool s_driverOpen;
    private static readonly object s_driverLock = new();

    private string[] _channelNames = [];
    private ulong[] _channelMasks = [];

    public bool IsConnected => _connected;
    public string AdapterName => "Vector XL";
    public event EventHandler<CanFrameEventArgs>? FrameReceived;

    private static void EnsureDriverOpen()
    {
        lock (s_driverLock)
        {
            if (!s_driverOpen)
            {
                // Close first so re-open picks up hot-plugged hardware
                try { VectorNative.xlCloseDriver(); } catch { }
                int stat = VectorNative.xlOpenDriver();
                if (stat != XlConst.XL_SUCCESS)
                    return;
                s_driverOpen = true;
            }
        }
    }

    public IReadOnlyList<string> GetAvailableChannels()
    {
        try
        {
            EnsureDriverOpen();
            EnumerateChannels();
            return _channelNames;
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

    private void EnumerateChannels()
    {
        var config = new XL_DRIVER_CONFIG();
        config.reserved = new uint[10];
        config.channel = new XL_CHANNEL_CONFIG[XlConst.XL_CONFIG_MAX_CHANNELS];
        for (int i = 0; i < XlConst.XL_CONFIG_MAX_CHANNELS; i++)
        {
            config.channel[i].busParams_data = new byte[32];
            config.channel[i].raw_data = new uint[10];
            config.channel[i].reserved = new uint[3];
        }

        int stat = VectorNative.xlGetDriverConfig(ref config);
        if (stat != XlConst.XL_SUCCESS || config.channelCount == 0)
        {
            _channelNames = [];
            _channelMasks = [];
            return;
        }

        var names = new List<string>();
        var masks = new List<ulong>();

        for (int i = 0; i < (int)config.channelCount && i < XlConst.XL_CONFIG_MAX_CHANNELS; i++)
        {
            var ch = config.channel[i];
            // Only include CAN-capable channels
            if ((ch.connectedBusType & XlConst.XL_BUS_TYPE_CAN) == 0 &&
                (ch.channelBusCapabilities & XlConst.XL_BUS_TYPE_CAN) == 0)
                continue;

            string name = ch.name ?? $"Channel {i}";
            if (string.IsNullOrWhiteSpace(name))
                name = $"Channel {i}";
            names.Add(name);
            masks.Add(ch.channelMask);
        }

        if (names.Count == 0)
        {
            _channelNames = [];
            _channelMasks = [];
            return;
        }

        _channelNames = names.ToArray();
        _channelMasks = masks.ToArray();
    }

    public Task<bool> ConnectAsync(string channel, uint bitrate)
    {
        if (_connected) Disconnect();

        try
        {
            EnsureDriverOpen();
            EnumerateChannels();

            // Find the channel index by name
            int chIdx = -1;
            for (int i = 0; i < _channelNames.Length; i++)
            {
                if (_channelNames[i] == channel)
                {
                    chIdx = i;
                    break;
                }
            }

            if (chIdx < 0 || chIdx >= _channelMasks.Length)
                return Task.FromResult(false);

            ulong mask = _channelMasks[chIdx];
            ulong permMask = mask;

            int status = VectorNative.xlOpenPort(out int portHandle, "CanLinConfig", mask,
                ref permMask, 256, XlConst.XL_INTERFACE_VERSION, XlConst.XL_BUS_TYPE_CAN);
            if (status != XlConst.XL_SUCCESS)
                return Task.FromResult(false);

            _portHandle = portHandle;
            _accessMask = mask;

            if (permMask != 0)
            {
                status = VectorNative.xlCanSetChannelBitrate(portHandle, mask, bitrate);
                if (status != XlConst.XL_SUCCESS)
                {
                    VectorNative.xlClosePort(portHandle);
                    _portHandle = -1;
                    return Task.FromResult(false);
                }
            }

            status = VectorNative.xlActivateChannel(portHandle, mask, XlConst.XL_BUS_TYPE_CAN, 0);
            if (status != XlConst.XL_SUCCESS)
            {
                VectorNative.xlClosePort(portHandle);
                _portHandle = -1;
                return Task.FromResult(false);
            }

            _notifyEvent = nint.Zero;
            VectorNative.xlSetNotification(portHandle, ref _notifyEvent, 1);

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
            if (_portHandle >= 0)
            {
                try { VectorNative.xlClosePort(_portHandle); } catch { }
                _portHandle = -1;
            }
            return Task.FromResult(false);
        }
    }

    public void Disconnect()
    {
        _rxRunning = false;

        lock (_lock)
        {
            if (!_connected) return;
            _connected = false;

            if (_portHandle >= 0)
            {
                try { VectorNative.xlDeactivateChannel(_portHandle, _accessMask); } catch { }
                try { VectorNative.xlClosePort(_portHandle); } catch { }
                _portHandle = -1;
            }
            _notifyEvent = nint.Zero;
        }

        _rxThread?.Join(2000);
        _rxThread = null;
    }

    public bool Send(CanFrame frame)
    {
        lock (_lock)
        {
            if (!_connected) return false;
            try
            {
                // Build raw 48-byte XLevent buffer matching vxlapi.h layout
                var evt = new byte[XlConst.XL_EVENT_SIZE];
                evt[0] = XlConst.XL_TRANSMIT_MSG;  // tag

                // tagData.msg.id at offset 16
                uint id = frame.Id;
                if (frame.IsExtended)
                    id |= XlConst.XL_CAN_EXT_MSG_ID;
                BitConverter.GetBytes(id).CopyTo(evt, 16);

                // tagData.msg.dlc at offset 22
                BitConverter.GetBytes((ushort)frame.Dlc).CopyTo(evt, 22);

                // tagData.msg.data at offset 32 (after 8-byte res1)
                int copyLen = Math.Min(frame.Data.Length, 8);
                Array.Copy(frame.Data, 0, evt, 32, copyLen);

                uint msgCount = 1;
                int status = VectorNative.xlCanTransmit(_portHandle, _accessMask, ref msgCount, evt);
                return status == XlConst.XL_SUCCESS;
            }
            catch
            {
                return false;
            }
        }
    }

    private void ReceiveLoop()
    {
        var evtBuf = new byte[XlConst.XL_EVENT_SIZE];

        while (_rxRunning)
        {
            uint count = 1;
            int status;

            lock (_lock)
            {
                if (!_connected)
                    break;
                status = VectorNative.xlReceive(_portHandle, ref count, evtBuf);
            }

            if (status == XlConst.XL_SUCCESS && count > 0)
            {
                byte tag = evtBuf[0];

                if (tag == XlConst.XL_RECEIVE_MSG)
                {
                    uint id = BitConverter.ToUInt32(evtBuf, 16);
                    ushort flags = BitConverter.ToUInt16(evtBuf, 20);
                    ushort dlc = BitConverter.ToUInt16(evtBuf, 22);

                    // Skip error frames and TX confirmations
                    if ((flags & XlConst.XL_CAN_MSG_FLAG_ERROR_FRAME) != 0)
                        continue;
                    if ((flags & XlConst.XL_CAN_MSG_FLAG_TX_COMPLETED) != 0)
                        continue;

                    bool extended = (id & XlConst.XL_CAN_EXT_MSG_ID) != 0;
                    id &= ~XlConst.XL_CAN_EXT_MSG_ID;

                    if (dlc > 8) dlc = 8;
                    var data = new byte[8];
                    Array.Copy(evtBuf, 32, data, 0, dlc);  // data at offset 32

                    var canFrame = new CanFrame
                    {
                        Id = id,
                        Dlc = (byte)dlc,
                        Data = data,
                        IsExtended = extended,
                    };
                    FrameReceived?.Invoke(this, new CanFrameEventArgs(canFrame));
                }
            }
            else if (status == XlConst.XL_ERR_QUEUE_IS_EMPTY)
            {
                if (_notifyEvent != nint.Zero)
                    VectorNative.WaitForSingleObject(_notifyEvent, 100);
                else
                    Thread.Sleep(1);
            }
            else
            {
                // Port closed or error — exit
                break;
            }
        }
    }

    public void Dispose()
    {
        Disconnect();
        GC.SuppressFinalize(this);
    }
}

// --- XL API Constants ---

internal static class XlConst
{
    public const int XL_SUCCESS = 0;
    public const int XL_ERR_QUEUE_IS_EMPTY = 10;
    public const byte XL_RECEIVE_MSG = 1;
    public const byte XL_TRANSMIT_MSG = 10;
    public const uint XL_CAN_EXT_MSG_ID = 0x80000000;
    public const int XL_INTERFACE_VERSION = 3;
    public const int XL_BUS_TYPE_CAN = 1;
    public const int XL_CONFIG_MAX_CHANNELS = 64;
    public const int XL_MAX_APPNAME = 31;
    public const int XL_EVENT_SIZE = 48;

    public const ushort XL_CAN_MSG_FLAG_ERROR_FRAME = 0x0020;
    public const ushort XL_CAN_MSG_FLAG_TX_COMPLETED = 0x0040;
}

// --- XL API Structs (matching vxlapi.h, verified against working VectorService) ---

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
internal struct XL_CHANNEL_CONFIG
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = XlConst.XL_MAX_APPNAME + 1)]
    public string name;                    // 32 bytes
    public byte hwType;
    public byte hwIndex;
    public byte hwChannel;
    public ushort transceiverType;
    public ushort transceiverState;
    public ushort configError;
    public byte channelIndex;
    public ulong channelMask;
    public uint channelCapabilities;
    public uint channelBusCapabilities;
    public byte isOnBus;
    public uint connectedBusType;
    // XL_BUS_PARAMS: busType(4) + union data(28) = 32 bytes
    public uint busParams_busType;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 28)]
    public byte[] busParams_data;
    public uint _doNotUse;
    public uint driverVersion;
    public uint interfaceVersion;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)]
    public uint[] raw_data;
    public uint serialNumber;
    public uint articleNumber;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string transceiverName;
    public uint specialCabFlags;
    public uint dominantTimeout;
    public byte dominantRecessiveDelay;
    public byte recessiveDominantDelay;
    public byte connectionInfo;
    public byte currentlyAvailableTimestamps;
    public ushort minimalSupplyVoltage;
    public ushort maximalSupplyVoltage;
    public uint maximalBaudrate;
    public byte fpgaCoreCapabilities;
    public byte specialDeviceStatus;
    public ushort channelBusActiveCapabilities;
    public ushort breakOffset;
    public ushort delimiterOffset;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public uint[] reserved;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
internal struct XL_DRIVER_CONFIG
{
    public uint dllVersion;
    public uint channelCount;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)]
    public uint[] reserved;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = XlConst.XL_CONFIG_MAX_CHANNELS)]
    public XL_CHANNEL_CONFIG[] channel;
}

// --- P/Invoke ---

internal static class VectorNative
{
    private const string DllName = "vxlapi64";

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int xlOpenDriver();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int xlCloseDriver();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int xlGetDriverConfig(ref XL_DRIVER_CONFIG config);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern int xlOpenPort(out int portHandle, string userName, ulong accessMask,
        ref ulong permissionMask, int rxQueueSize, int xlInterfaceVersion, int busType);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int xlClosePort(int portHandle);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int xlActivateChannel(int portHandle, ulong accessMask, int busType, int flags);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int xlDeactivateChannel(int portHandle, ulong accessMask);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int xlCanSetChannelBitrate(int portHandle, ulong accessMask, uint bitrate);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int xlCanTransmit(int portHandle, ulong accessMask, ref uint messageCount, byte[] messages);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int xlReceive(int portHandle, ref uint pEventCount, byte[] pEventBuffer);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int xlSetNotification(int portHandle, ref nint handle, int queueLevel);

    [DllImport("kernel32.dll")]
    public static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);
}
