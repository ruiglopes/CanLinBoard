using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using CanBus;

namespace CanBus.Adapters;

public class KvaserService : ICanAdapter
{
    // --- canlib32.dll P/Invoke ---

    private const string DllName = "canlib32.dll";

    // canStatus values
    private const int canOK = 0;
    private const int canERR_NOMSG = -2;

    // canOPEN flags
    private const int canOPEN_ACCEPT_VIRTUAL = 0x0020;

    // Predefined bitrate constants
    private const int canBITRATE_125K = -4;
    private const int canBITRATE_250K = -3;
    private const int canBITRATE_500K = -2;
    private const int canBITRATE_1M   = -1;

    // canMSG flags
    private const uint canMSG_EXT = 0x0004;
    private const uint canMSG_STD = 0x0002;

    // canGetChannelData items
    private const int canCHANNELDATA_CHANNEL_NAME = 13;

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int canInitializeLibrary();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int canGetNumberOfChannels(out int channelCount);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int canGetChannelData(int channel, int item, byte[] buffer, int bufsize);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int canOpenChannel(int channel, int flags);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int canClose(int handle);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int canSetBusParams(int handle, int freq, int tseg1, int tseg2, int sjw, int noSamp, int syncMode);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int canBusOn(int handle);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int canBusOff(int handle);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int canWrite(int handle, int id, byte[] msg, int dlc, uint flag);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int canReadWait(int handle, out int id, byte[] msg, out int dlc, out uint flag, out uint time, int timeout);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int canUnloadLibrary();

    // --- Fields ---

    private volatile uint _debugCanId = 0x7FF;
    public uint DebugCanId { get => _debugCanId; set => _debugCanId = value; }
    private volatile uint _responseCanId = 0x701;
    public uint ResponseCanId { get => _responseCanId; set => _responseCanId = value; }

    private int _handle = -1;
    private bool _initialized;
    private readonly object _lock = new();

    private Thread? _readerThread;
    private volatile bool _readerRunning;
    private readonly ConcurrentQueue<(uint Id, byte[] Data)> _rxQueue = new();

    private string[] _channelNames = Array.Empty<string>();
    private int[] _channelIndices = Array.Empty<int>();

    public event Action<byte[]>? DebugMessageReceived;
    public event Action? HeartbeatReceived;
    public event Action<CanTraceFrame>? FrameTraced;

    private static readonly (string Name, int Param)[] Bitrates =
    {
        ("125 kbit/s", canBITRATE_125K),
        ("250 kbit/s", canBITRATE_250K),
        ("500 kbit/s", canBITRATE_500K),
        ("1 Mbit/s",   canBITRATE_1M),
    };

    public KvaserService()
    {
        // Unload first so re-init picks up hot-plugged hardware
        try { canUnloadLibrary(); } catch { /* OK if not yet initialized */ }

        int stat = canInitializeLibrary();
        if (stat != canOK)
            throw new InvalidOperationException($"canInitializeLibrary failed: {stat}");

        EnumerateChannels();
    }

    // --- ICanAdapter ---

    public string AdapterName => "Kvaser CANlib";
    public string[] ChannelNames => _channelNames;
    public string[] BitrateNames => Array.ConvertAll(Bitrates, b => b.Name);

    public void Initialize(int channelIndex, int bitrateIndex)
    {
        lock (_lock)
        {
            if (_initialized)
                Shutdown();

            int hwChannel = _channelIndices[channelIndex];
            int handle = canOpenChannel(hwChannel, canOPEN_ACCEPT_VIRTUAL);
            if (handle < 0)
                throw new InvalidOperationException($"canOpenChannel failed: {handle}");

            int bitrateParam = Bitrates[bitrateIndex].Param;
            int stat = canSetBusParams(handle, bitrateParam, 0, 0, 0, 0, 0);
            if (stat != canOK)
            {
                canClose(handle);
                throw new InvalidOperationException($"canSetBusParams failed: {stat}");
            }

            stat = canBusOn(handle);
            if (stat != canOK)
            {
                canClose(handle);
                throw new InvalidOperationException($"canBusOn failed: {stat}");
            }

            _handle = handle;
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
                throw new InvalidOperationException("Kvaser not initialized");

            int stat = canWrite(_handle, (int)canId, data, data.Length, canMSG_STD);
            if (stat != canOK)
                throw new InvalidOperationException($"canWrite failed: {stat}");
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
            canBusOff(_handle);
            canClose(_handle);
            _initialized = false;
            _handle = -1;
        }
    }

    public void Dispose()
    {
        Shutdown();
    }

    // --- Private ---

    private void EnumerateChannels()
    {
        int stat = canGetNumberOfChannels(out int count);
        if (stat != canOK || count <= 0)
        {
            _channelNames = new[] { "Channel 0" };
            _channelIndices = new[] { 0 };
            return;
        }

        var names = new List<string>();
        var indices = new List<int>();
        var buffer = new byte[256];

        for (int i = 0; i < count; i++)
        {
            stat = canGetChannelData(i, canCHANNELDATA_CHANNEL_NAME, buffer, buffer.Length);
            string name;
            if (stat == canOK)
            {
                int len = Array.IndexOf(buffer, (byte)0);
                if (len < 0) len = buffer.Length;
                name = Encoding.ASCII.GetString(buffer, 0, len).Trim();
                if (string.IsNullOrEmpty(name))
                    name = $"Channel {i}";
            }
            else
            {
                name = $"Channel {i}";
            }

            names.Add(name);
            indices.Add(i);
        }

        _channelNames = names.ToArray();
        _channelIndices = indices.ToArray();
    }

    private void StartReaderThread()
    {
        _readerRunning = true;
        _readerThread = new Thread(ReaderLoop)
        {
            Name = "Kvaser CAN Reader",
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
        var msgBuf = new byte[8];

        while (_readerRunning)
        {
            int id;
            int dlc;
            uint flags;
            uint timestamp;
            int stat;

            lock (_lock)
            {
                if (!_initialized)
                    break;
                stat = canReadWait(_handle, out id, msgBuf, out dlc, out flags, out timestamp, 0);
            }

            if (stat == canOK)
            {
                // Skip extended/error frames
                if ((flags & canMSG_EXT) != 0)
                    continue;

                if (dlc < 0) dlc = 0;
                if (dlc > 8) dlc = 8;
                var data = new byte[dlc];
                Array.Copy(msgBuf, data, dlc);

                uint canId = (uint)id;

                FrameTraced?.Invoke(new CanTraceFrame
                {
                    Timestamp = DateTime.Now, IsTx = false, CanId = canId, Data = data
                });

                if (canId == _debugCanId)
                {
                    DebugMessageReceived?.Invoke(data);
                }
                else
                {
                    if (canId == _responseCanId)
                        HeartbeatReceived?.Invoke();
                    _rxQueue.Enqueue((canId, data));
                }
            }
            else if (stat == canERR_NOMSG)
            {
                Thread.Sleep(1);
            }
        }
    }
}
