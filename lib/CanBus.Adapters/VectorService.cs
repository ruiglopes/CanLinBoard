using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using CanBus;

namespace CanBus.Adapters;

public class VectorService : ICanAdapter
{
    // --- vxlapi P/Invoke ---
    // Vector XL Driver Library: vxlapi64.dll (64-bit) / vxlapi.dll (32-bit)
    // Calling convention is StdCall (confirmed by python-can / ctypes.windll)
    private const string DllName = "vxlapi64";

    // XLstatus values
    private const int XL_SUCCESS = 0;
    private const int XL_ERR_QUEUE_IS_EMPTY = 10;

    // XL bus types
    private const int XL_BUS_TYPE_CAN = 0x00000001;

    // XL access mask
    private const int XL_INTERFACE_VERSION = 3;

    // XL event tags
    private const byte XL_RECEIVE_MSG = 1;
    private const byte XL_TRANSMIT_MSG = 10;

    // XL_CAN_MSG flags
    private const ushort XL_CAN_MSG_FLAG_ERROR_FRAME = 0x0020;
    private const ushort XL_CAN_MSG_FLAG_TX_COMPLETED = 0x0040;

    // Channel config constants
    private const int XL_MAX_APPNAME = 31;
    private const int XL_CONFIG_MAX_CHANNELS = 64;

    // XLevent size: 16-byte header + 32-byte s_xl_can_msg = 48 bytes
    private const int XL_EVENT_SIZE = 48;

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int xlOpenDriver();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int xlCloseDriver();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int xlGetDriverConfig(ref XL_DRIVER_CONFIG config);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int xlOpenPort(
        out int portHandle, string appName, ulong accessMask, ref ulong permissionMask,
        int rxQueueSize, int interfaceVersion, int busType);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int xlClosePort(int portHandle);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int xlCanSetChannelBitrate(int portHandle, ulong accessMask, uint bitrate);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int xlActivateChannel(int portHandle, ulong accessMask, int busType, int flags);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int xlDeactivateChannel(int portHandle, ulong accessMask);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int xlCanTransmit(int portHandle, ulong accessMask, ref uint messageCount, byte[] messages);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int xlReceive(int portHandle, ref uint messageCount, byte[] eventBuffer);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int xlSetNotification(int portHandle, ref IntPtr eventHandle, int queueLevel);

    // --- Structs (matching vxlapi.h, verified against python-can xlclass.py) ---

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct XL_BUS_PARAMS
    {
        public uint busType;
        // Union: largest member is a429 at 28 bytes
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 28)]
        public byte[] data;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct XL_CHANNEL_CONFIG
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = XL_MAX_APPNAME + 1)] // 32 bytes
        public string name;
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
        public XL_BUS_PARAMS busParams;
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
    private struct XL_DRIVER_CONFIG
    {
        public uint dllVersion;
        public uint channelCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)]
        public uint[] reserved;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = XL_CONFIG_MAX_CHANNELS)]
        public XL_CHANNEL_CONFIG[] channel;
    }

    // XLevent layout (raw byte offsets):
    //   0:  tag (1)          8:  timeStamp (8)
    //   1:  chanIndex (1)   16:  tagData.msg.id (4)
    //   2:  transId (2)     20:  tagData.msg.flags (2)
    //   4:  portHandle (2)  22:  tagData.msg.dlc (2)
    //   6:  flags (1)       24:  tagData.msg.res1 (8)
    //   7:  reserved (1)    32:  tagData.msg.data[8]
    //                       40:  tagData.msg.res2 (8)

    // --- Fields ---

    private volatile uint _debugCanId = 0x7FF;
    public uint DebugCanId { get => _debugCanId; set => _debugCanId = value; }
    private volatile uint _responseCanId = 0x701;
    public uint ResponseCanId { get => _responseCanId; set => _responseCanId = value; }
    private const string AppName = "CanFlashProgrammer";

    private int _portHandle = -1;
    private ulong _accessMask;
    private bool _initialized;
    private readonly object _lock = new();

    private Thread? _readerThread;
    private volatile bool _readerRunning;
    private readonly ConcurrentQueue<(uint Id, byte[] Data)> _rxQueue = new();
    private IntPtr _notifyEvent = IntPtr.Zero;

    public event Action<byte[]>? DebugMessageReceived;
    public event Action? HeartbeatReceived;
    public event Action<CanTraceFrame>? FrameTraced;

    private string[] _channelNames = Array.Empty<string>();
    private ulong[] _channelMasks = Array.Empty<ulong>();

    private static readonly (string Name, uint Rate)[] Bitrates =
    {
        ("125 kbit/s", 125000),
        ("250 kbit/s", 250000),
        ("500 kbit/s", 500000),
        ("1 Mbit/s",   1000000),
    };

    public VectorService()
    {
        // Close first so re-open picks up hot-plugged hardware
        try { xlCloseDriver(); } catch { /* OK if not yet opened */ }

        int stat = xlOpenDriver();
        if (stat != XL_SUCCESS)
            throw new InvalidOperationException($"xlOpenDriver failed: {stat}");

        EnumerateChannels();
    }

    // --- ICanAdapter ---

    public string AdapterName => "Vector XL";
    public string[] ChannelNames => _channelNames;
    public string[] BitrateNames => Array.ConvertAll(Bitrates, b => b.Name);

    public void Initialize(int channelIndex, int bitrateIndex)
    {
        lock (_lock)
        {
            if (_initialized)
                Shutdown();

            ulong mask = _channelMasks[channelIndex];
            ulong permMask = mask;
            uint bitrate = Bitrates[bitrateIndex].Rate;

            int stat = xlOpenPort(out int portHandle, AppName, mask, ref permMask,
                256, XL_INTERFACE_VERSION, XL_BUS_TYPE_CAN);
            if (stat != XL_SUCCESS)
                throw new InvalidOperationException($"xlOpenPort failed: {stat}");

            _portHandle = portHandle;
            _accessMask = mask;

            if (permMask != 0)
            {
                stat = xlCanSetChannelBitrate(portHandle, mask, bitrate);
                if (stat != XL_SUCCESS)
                {
                    xlClosePort(portHandle);
                    _portHandle = -1;
                    throw new InvalidOperationException($"xlCanSetChannelBitrate failed: {stat}");
                }
            }

            stat = xlActivateChannel(portHandle, mask, XL_BUS_TYPE_CAN, 0);
            if (stat != XL_SUCCESS)
            {
                xlClosePort(portHandle);
                _portHandle = -1;
                throw new InvalidOperationException($"xlActivateChannel failed: {stat}");
            }

            // Set up notification event for reader thread
            _notifyEvent = IntPtr.Zero;
            xlSetNotification(portHandle, ref _notifyEvent, 1);

            _initialized = true;
        }

        StartReaderThread();
    }

    public bool IsConnected => _initialized;

    public void Send(uint canId, byte[] data)
    {
        lock (_lock)
        {
            if (!_initialized)
                throw new InvalidOperationException("Vector XL not initialized");

            // Build a full 48-byte XLevent for xlCanTransmit
            var evt = new byte[XL_EVENT_SIZE];
            evt[0] = XL_TRANSMIT_MSG;                                    // tag
            // bytes 1-15: chanIndex, transId, portHandle, flags, reserved, timeStamp = 0
            BitConverter.GetBytes(canId).CopyTo(evt, 16);                // tagData.msg.id
            // bytes 20-21: flags = 0
            BitConverter.GetBytes((ushort)data.Length).CopyTo(evt, 22);  // tagData.msg.dlc
            // bytes 24-31: res1 = 0
            Array.Copy(data, 0, evt, 32, data.Length);                   // tagData.msg.data

            uint count = 1;
            int stat = xlCanTransmit(_portHandle, _accessMask, ref count, evt);
            if (stat != XL_SUCCESS)
                throw new InvalidOperationException($"xlCanTransmit failed: {stat}");
        }

        FrameTraced?.Invoke(new CanTraceFrame
        {
            Timestamp = DateTime.Now, IsTx = true, CanId = canId, Data = data
        });
    }

    public (uint Id, byte[] Data)? Receive(int timeoutMs, uint? filterCanId = null)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            if (_rxQueue.TryDequeue(out var item))
            {
                if (filterCanId.HasValue && item.Id != filterCanId.Value)
                    continue;
                return item;
            }

            Thread.Sleep(1);
        }

        return null;
    }

    public (uint Id, byte[] Data)? ReceiveMultiFilter(int timeoutMs, params uint[] acceptIds)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        var idSet = new HashSet<uint>(acceptIds);

        while (Environment.TickCount64 < deadline)
        {
            if (_rxQueue.TryDequeue(out var item))
            {
                if (idSet.Count > 0 && !idSet.Contains(item.Id))
                    continue;
                return item;
            }

            Thread.Sleep(1);
        }

        return null;
    }

    public void Drain(int durationMs = 200)
    {
        var deadline = Environment.TickCount64 + durationMs;
        while (Environment.TickCount64 < deadline)
        {
            while (_rxQueue.TryDequeue(out _)) { }
            Thread.Sleep(1);
        }
    }

    public void Shutdown()
    {
        StopReaderThread();

        lock (_lock)
        {
            if (!_initialized) return;
            xlDeactivateChannel(_portHandle, _accessMask);
            xlClosePort(_portHandle);
            _initialized = false;
            _portHandle = -1;
            _notifyEvent = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        Shutdown();
        try { xlCloseDriver(); } catch { }
    }

    // --- Private ---

    private void EnumerateChannels()
    {
        var config = new XL_DRIVER_CONFIG();
        config.reserved = new uint[10];
        config.channel = new XL_CHANNEL_CONFIG[XL_CONFIG_MAX_CHANNELS];
        for (int i = 0; i < XL_CONFIG_MAX_CHANNELS; i++)
        {
            config.channel[i].busParams.data = new byte[28];
            config.channel[i].raw_data = new uint[10];
            config.channel[i].reserved = new uint[3];
        }

        int stat = xlGetDriverConfig(ref config);
        if (stat != XL_SUCCESS || config.channelCount == 0)
        {
            _channelNames = new[] { "Channel 0" };
            _channelMasks = new ulong[] { 1 };
            return;
        }

        var names = new List<string>();
        var masks = new List<ulong>();

        for (int i = 0; i < (int)config.channelCount && i < XL_CONFIG_MAX_CHANNELS; i++)
        {
            var ch = config.channel[i];
            // Only include CAN-capable channels
            if ((ch.connectedBusType & XL_BUS_TYPE_CAN) == 0 &&
                (ch.channelBusCapabilities & XL_BUS_TYPE_CAN) == 0)
                continue;

            string name = ch.name ?? $"Channel {i}";
            if (string.IsNullOrWhiteSpace(name))
                name = $"Channel {i}";
            names.Add(name);
            masks.Add(ch.channelMask);
        }

        if (names.Count == 0)
        {
            _channelNames = new[] { "Channel 0" };
            _channelMasks = new ulong[] { 1 };
            return;
        }

        _channelNames = names.ToArray();
        _channelMasks = masks.ToArray();
    }

    private void StartReaderThread()
    {
        _readerRunning = true;
        _readerThread = new Thread(ReaderLoop)
        {
            Name = "Vector CAN Reader",
            IsBackground = true
        };
        _readerThread.Start();
    }

    private void StopReaderThread()
    {
        _readerRunning = false;
        _readerThread?.Join(2000);
        _readerThread = null;

        while (_rxQueue.TryDequeue(out _)) { }
    }

    private void ReaderLoop()
    {
        var evtBuf = new byte[XL_EVENT_SIZE];

        while (_readerRunning)
        {
            uint count = 1;
            int stat;

            lock (_lock)
            {
                if (!_initialized)
                    break;
                stat = xlReceive(_portHandle, ref count, evtBuf);
            }

            if (stat == XL_SUCCESS && count > 0)
            {
                // XLevent header: tag(1) chanIndex(1) transId(2) portHandle(2) flags(1) reserved(1) timeStamp(8)
                // s_xl_can_msg at offset 16: id(4) flags(2) dlc(2) res1(8) data[8] res2(8)
                byte tag = evtBuf[0];

                if (tag == XL_RECEIVE_MSG)
                {
                    uint id = BitConverter.ToUInt32(evtBuf, 16);
                    ushort flags = BitConverter.ToUInt16(evtBuf, 20);
                    ushort dlc = BitConverter.ToUInt16(evtBuf, 22);

                    // Skip error frames and TX confirmations
                    if ((flags & XL_CAN_MSG_FLAG_ERROR_FRAME) != 0)
                        continue;
                    if ((flags & XL_CAN_MSG_FLAG_TX_COMPLETED) != 0)
                        continue;

                    if (dlc > 8) dlc = 8;
                    var data = new byte[dlc];
                    Array.Copy(evtBuf, 32, data, 0, dlc);  // data is at offset 32 (after 8-byte res1)

                    FrameTraced?.Invoke(new CanTraceFrame
                    {
                        Timestamp = DateTime.Now, IsTx = false, CanId = id, Data = data
                    });

                    if (id == _debugCanId)
                    {
                        DebugMessageReceived?.Invoke(data);
                    }
                    else
                    {
                        if (id == _responseCanId)
                            HeartbeatReceived?.Invoke();
                        _rxQueue.Enqueue((id, data));
                    }
                }
            }
            else if (stat == XL_ERR_QUEUE_IS_EMPTY)
            {
                if (_notifyEvent != IntPtr.Zero)
                    WaitForSingleObject(_notifyEvent, 100);
                else
                    Thread.Sleep(1);
            }
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);
}
