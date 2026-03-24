using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Ports;
using System.Text;
using System.Threading;
using CanBus;

namespace CanBus.Adapters;

public class SlcanService : ICanAdapter
{
    private volatile uint _debugCanId = 0x7FF;
    public uint DebugCanId { get => _debugCanId; set => _debugCanId = value; }
    private volatile uint _responseCanId = 0x701;
    public uint ResponseCanId { get => _responseCanId; set => _responseCanId = value; }

    private SerialPort? _serialPort;
    private bool _initialized;
    private readonly object _lock = new();

    private Thread? _readerThread;
    private volatile bool _readerRunning;
    private readonly ConcurrentQueue<(uint Id, byte[] Data)> _rxQueue = new();

    public event Action<byte[]>? DebugMessageReceived;
    public event Action? HeartbeatReceived;
    public event Action<CanTraceFrame>? FrameTraced;

    private string[] _portNames;

    // SLCAN bitrate codes: S4=125k, S5=250k, S6=500k, S8=1M
    private static readonly (string Name, string Code)[] Bitrates =
    {
        ("125 kbit/s", "S4"),
        ("250 kbit/s", "S5"),
        ("500 kbit/s", "S6"),
        ("1 Mbit/s",   "S8"),
    };

    public string AdapterName => "SLCAN (Serial)";
    public string[] ChannelNames => _portNames;
    public string[] BitrateNames => Array.ConvertAll(Bitrates, b => b.Name);
    public bool IsConnected => _initialized;

    public SlcanService()
    {
        _portNames = EnumeratePorts();
    }

    private static string[] EnumeratePorts()
    {
        var ports = SerialPort.GetPortNames();
        // Natural sort: COM1, COM2, ..., COM10, COM11
        Array.Sort(ports, (a, b) =>
        {
            if (a.StartsWith("COM") && b.StartsWith("COM") &&
                int.TryParse(a.AsSpan(3), out int na) &&
                int.TryParse(b.AsSpan(3), out int nb))
                return na.CompareTo(nb);
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        });
        return ports;
    }

    public void Initialize(int channelIndex, int bitrateIndex)
    {
        lock (_lock)
        {
            if (_initialized)
                Shutdown();

            // Re-enumerate in case ports changed since construction
            _portNames = EnumeratePorts();
            if (channelIndex < 0 || channelIndex >= _portNames.Length)
                throw new InvalidOperationException("No serial port selected");

            var portName = _portNames[channelIndex];
            var bitrateCode = Bitrates[bitrateIndex].Code;

            _serialPort = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
            {
                Handshake = Handshake.None,
                DtrEnable = true,
                RtsEnable = false,
                ReadTimeout = 1000,
                WriteTimeout = 1000,
                ReadBufferSize = 4096,
                WriteBufferSize = 256,
                NewLine = "\r"
            };

            _serialPort.Open();

            try
            {
                // Close any previously-open channel (best-effort)
                SendCommand("C");
                Thread.Sleep(50);

                // Set bitrate
                var resp = SendCommand(bitrateCode);
                if (resp == '\x07')
                    throw new InvalidOperationException($"SLCAN: bitrate command '{bitrateCode}' rejected");

                // Open CAN channel
                resp = SendCommand("O");
                if (resp == '\x07')
                    throw new InvalidOperationException("SLCAN: open channel rejected");

                _initialized = true;
            }
            catch
            {
                _serialPort.Close();
                _serialPort.Dispose();
                _serialPort = null;
                throw;
            }
        }

        StartReaderThread();
    }

    /// <summary>
    /// Send an SLCAN command and return the first response character ('\r' = OK, '\x07' = error).
    /// Returns '\0' on timeout.
    /// </summary>
    private char SendCommand(string cmd)
    {
        if (_serialPort == null) return '\0';

        // Discard any pending input
        _serialPort.DiscardInBuffer();

        _serialPort.Write(cmd + "\r");

        try
        {
            // Read until we get CR (OK) or BEL (error)
            var deadline = Environment.TickCount64 + 1000;
            while (Environment.TickCount64 < deadline)
            {
                if (_serialPort.BytesToRead > 0)
                {
                    int b = _serialPort.ReadByte();
                    if (b == '\r') return '\r';
                    if (b == '\x07') return '\x07';
                    // Skip other chars (echo, etc.)
                }
                else
                {
                    Thread.Sleep(1);
                }
            }
        }
        catch (TimeoutException) { }

        return '\0';
    }

    public void Send(uint canId, byte[] data)
    {
        lock (_lock)
        {
            if (!_initialized || _serialPort == null)
                throw new InvalidOperationException("SLCAN not initialized");

            // Format: t<3-hex-id><dlc><hex-data>\r
            var sb = new StringBuilder(32);
            sb.Append('t');
            sb.Append(canId.ToString("X3"));
            sb.Append(data.Length.ToString("X1"));
            for (int i = 0; i < data.Length; i++)
                sb.Append(data[i].ToString("X2"));
            sb.Append('\r');

            _serialPort.Write(sb.ToString());
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
            _initialized = false;

            if (_serialPort != null)
            {
                try { _serialPort.Write("C\r"); }
                catch { /* best-effort close */ }

                try { _serialPort.Close(); }
                catch { }

                _serialPort.Dispose();
                _serialPort = null;
            }
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
            Name = "SLCAN Reader",
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
        var buffer = new StringBuilder(64);

        while (_readerRunning)
        {
            try
            {
                SerialPort? port;
                lock (_lock)
                {
                    if (!_initialized) break;
                    port = _serialPort;
                }

                if (port == null || !port.IsOpen)
                    break;

                if (port.BytesToRead == 0)
                {
                    Thread.Sleep(1);
                    continue;
                }

                int b = port.ReadByte();
                if (b < 0) continue;

                char c = (char)b;

                // Line terminators complete a frame
                if (c == '\r' || c == '\n')
                {
                    if (buffer.Length > 0)
                    {
                        ProcessSlcanFrame(buffer.ToString());
                        buffer.Clear();
                    }
                    continue;
                }

                buffer.Append(c);

                // Safety: prevent unbounded buffer growth
                if (buffer.Length > 64)
                    buffer.Clear();
            }
            catch (System.IO.IOException)
            {
                // Serial port disconnected
                break;
            }
            catch
            {
                Thread.Sleep(1);
            }
        }
    }

    private void ProcessSlcanFrame(string frame)
    {
        // Standard received frame: t<3-hex-id><1-hex-dlc><hex-data>
        // Extended received frame: T<8-hex-id><1-hex-dlc><hex-data> (skip)
        if (frame.Length < 1) return;

        char type = frame[0];
        if (type == 't' && frame.Length >= 5)
        {
            // Parse 11-bit ID (3 hex chars)
            if (!uint.TryParse(frame.AsSpan(1, 3), System.Globalization.NumberStyles.HexNumber, null, out uint id))
                return;

            // Parse DLC (1 hex char)
            if (!int.TryParse(frame.AsSpan(4, 1), System.Globalization.NumberStyles.HexNumber, null, out int dlc))
                return;

            if (dlc < 0 || dlc > 8) return;

            // Parse data bytes
            int dataStart = 5;
            int dataHexLen = dlc * 2;
            if (frame.Length < dataStart + dataHexLen) return;

            var data = new byte[dlc];
            for (int i = 0; i < dlc; i++)
            {
                if (!byte.TryParse(frame.AsSpan(dataStart + i * 2, 2),
                    System.Globalization.NumberStyles.HexNumber, null, out data[i]))
                    return;
            }

            FrameTraced?.Invoke(new CanTraceFrame
            {
                Timestamp = DateTime.Now, IsTx = false, CanId = id, Data = data
            });

            // Dispatch: debug frames go to event, others to queue
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
        // T (extended), z (TX ack), F (status), etc. — ignore
    }
}
