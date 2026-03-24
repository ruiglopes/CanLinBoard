using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using CanBus;
using Peak.Can.Basic;

namespace CanBus.Adapters;

public class PcanService : ICanAdapter
{
    private volatile uint _debugCanId = 0x7FF;
    public uint DebugCanId { get => _debugCanId; set => _debugCanId = value; }
    private volatile uint _responseCanId = 0x701;
    public uint ResponseCanId { get => _responseCanId; set => _responseCanId = value; }

    private PcanChannel _channel = PcanChannel.None;
    private bool _initialized;
    private readonly object _lock = new();

    private Thread? _readerThread;
    private volatile bool _readerRunning;
    private readonly ConcurrentQueue<(uint Id, byte[] Data)> _rxQueue = new();

    public event Action<byte[]>? DebugMessageReceived;
    public event Action? HeartbeatReceived;
    public event Action<CanTraceFrame>? FrameTraced;

    public string AdapterName => "PCAN-Basic";
    string[] ICanAdapter.ChannelNames => Array.ConvertAll(AvailableChannels, c => c.Name);
    string[] ICanAdapter.BitrateNames => Array.ConvertAll(AvailableBitrates, b => b.Name);
    void ICanAdapter.Initialize(int channelIndex, int bitrateIndex) =>
        Initialize(AvailableChannels[channelIndex].Channel, AvailableBitrates[bitrateIndex].Rate);

    public bool IsConnected => _initialized;

    public static readonly (string Name, PcanChannel Channel)[] AvailableChannels =
    {
        ("PCAN_USBBUS1", PcanChannel.Usb01),
        ("PCAN_USBBUS2", PcanChannel.Usb02),
        ("PCAN_USBBUS3", PcanChannel.Usb03),
        ("PCAN_USBBUS4", PcanChannel.Usb04),
    };

    public static readonly (string Name, Bitrate Rate)[] AvailableBitrates =
    {
        ("125 kbit/s", Bitrate.Pcan125),
        ("250 kbit/s", Bitrate.Pcan250),
        ("500 kbit/s", Bitrate.Pcan500),
        ("1 Mbit/s",   Bitrate.Pcan1000),
    };

    public void Initialize(PcanChannel channel, Bitrate bitrate)
    {
        lock (_lock)
        {
            if (_initialized)
                Shutdown();

            var status = Api.Initialize(channel, bitrate);
            if (status != PcanStatus.OK)
                throw new InvalidOperationException($"PCAN Initialize failed: {status}");

            _channel = channel;
            _initialized = true;
        }

        StartReaderThread();
    }

    public void Send(uint canId, byte[] data)
    {
        lock (_lock)
        {
            if (!_initialized)
                throw new InvalidOperationException("PCAN not initialized");

            var msg = new PcanMessage(canId, MessageType.Standard, (byte)data.Length, data, false);
            var status = Api.Write(_channel, msg);
            if (status != PcanStatus.OK)
                throw new InvalidOperationException($"PCAN Write failed: {status}");
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
            Api.Uninitialize(_channel);
            _initialized = false;
            _channel = PcanChannel.None;
        }
    }

    public void Dispose()
    {
        Shutdown();
    }

    private void StartReaderThread()
    {
        _readerRunning = true;
        _readerThread = new Thread(ReaderLoop)
        {
            Name = "CAN Reader",
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
        while (_readerRunning)
        {
            try
            {
                PcanMessage msg;
                PcanStatus status;

                lock (_lock)
                {
                    if (!_initialized)
                        break;
                    status = Api.Read(_channel, out msg);
                }

                if (status == PcanStatus.OK)
                {
                    // Skip error, status, echo, and extended frames
                    if (msg.MsgType != MessageType.Standard)
                        continue;

                    int dlc = msg.DLC;
                    if (dlc > 8) dlc = 8;

                    var data = new byte[dlc];
                    Array.Copy(msg.Data, data, dlc);

                    FrameTraced?.Invoke(new CanTraceFrame
                    {
                        Timestamp = DateTime.Now, IsTx = false, CanId = msg.ID, Data = data
                    });

                    if (msg.ID == _debugCanId)
                    {
                        DebugMessageReceived?.Invoke(data);
                    }
                    else
                    {
                        if (msg.ID == _responseCanId)
                            HeartbeatReceived?.Invoke();
                        _rxQueue.Enqueue((msg.ID, data));
                    }
                }
                else if (status == PcanStatus.ReceiveQueueEmpty)
                {
                    Thread.Sleep(1);
                }
            }
            catch
            {
                // Prevent reader thread from dying on unexpected errors
                Thread.Sleep(1);
            }
        }
    }
}
