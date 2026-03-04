using System.IO.Ports;
using System.Text;

namespace CanLinConfig.Adapters;

/// <summary>
/// SLCAN (Serial CAN) adapter — ASCII protocol over serial port.
/// Compatible with CANable, USBtin, and similar SLCAN devices.
/// </summary>
public class SlcanAdapter : ICanAdapter
{
    private SerialPort? _serial;
    private Thread? _rxThread;
    private volatile bool _rxRunning;
    private readonly StringBuilder _lineBuffer = new();

    public bool IsConnected => _serial?.IsOpen == true;
    public string AdapterName => "SLCAN";
    public event EventHandler<CanFrameEventArgs>? FrameReceived;

    private static readonly string[] CommonBaudRates = ["115200", "921600", "1000000", "2000000", "3000000"];

    public IReadOnlyList<string> GetAvailableChannels()
    {
        try
        {
            var ports = SerialPort.GetPortNames();
            // Return COM ports with a default serial baud suffix
            return ports.Select(p => $"{p}@115200").ToList();
        }
        catch
        {
            return [];
        }
    }

    public Task<bool> ConnectAsync(string channel, uint bitrate)
    {
        if (IsConnected) Disconnect();

        // Parse "COMx@serialBaud" format
        var parts = channel.Split('@');
        string portName = parts[0];
        int serialBaud = parts.Length > 1 && int.TryParse(parts[1], out int sb) ? sb : 115200;

        try
        {
            _serial = new SerialPort(portName, serialBaud, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 100,
                WriteTimeout = 100,
                NewLine = "\r",
            };
            _serial.Open();

            // Close any existing connection
            SendLine("C");
            Thread.Sleep(50);

            // Set CAN bitrate
            string bitrateCmd = bitrate switch
            {
                10000 => "S0",
                20000 => "S1",
                50000 => "S2",
                100000 => "S3",
                125000 => "S4",
                250000 => "S5",
                500000 => "S6",
                800000 => "S7",
                1000000 => "S8",
                _ => $"S6", // default 500k
            };
            SendLine(bitrateCmd);
            Thread.Sleep(50);

            // Open CAN channel
            SendLine("O");
            Thread.Sleep(50);

            _rxRunning = true;
            _rxThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "SLCAN_RX" };
            _rxThread.Start();

            return Task.FromResult(true);
        }
        catch
        {
            _serial?.Close();
            _serial?.Dispose();
            _serial = null;
            return Task.FromResult(false);
        }
    }

    public void Disconnect()
    {
        _rxRunning = false;
        _rxThread?.Join(1000);
        _rxThread = null;

        if (_serial?.IsOpen == true)
        {
            try { SendLine("C"); } catch { }
            try { _serial.Close(); } catch { }
        }
        _serial?.Dispose();
        _serial = null;
    }

    public bool Send(CanFrame frame)
    {
        if (!IsConnected) return false;

        try
        {
            // Standard frame: tIIIDLDD...
            // Extended frame: TIIIIIIIIDLDD...
            var sb = new StringBuilder();
            if (frame.IsExtended)
            {
                sb.Append('T');
                sb.Append(frame.Id.ToString("X8"));
            }
            else
            {
                sb.Append('t');
                sb.Append(frame.Id.ToString("X3"));
            }
            sb.Append(frame.Dlc);
            for (int i = 0; i < frame.Dlc; i++)
                sb.Append(frame.Data[i].ToString("X2"));

            SendLine(sb.ToString());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SendLine(string line)
    {
        _serial?.Write(line + "\r");
    }

    private void ReceiveLoop()
    {
        while (_rxRunning && _serial?.IsOpen == true)
        {
            try
            {
                int b = _serial.ReadByte();
                if (b < 0) continue;
                char c = (char)b;

                if (c == '\r' || c == '\n')
                {
                    if (_lineBuffer.Length > 0)
                    {
                        ParseLine(_lineBuffer.ToString());
                        _lineBuffer.Clear();
                    }
                }
                else
                {
                    _lineBuffer.Append(c);
                }
            }
            catch (TimeoutException)
            {
                // Normal
            }
            catch
            {
                if (_rxRunning) Thread.Sleep(10);
            }
        }
    }

    private void ParseLine(string line)
    {
        if (line.Length < 1) return;

        char type = line[0];
        bool extended = type == 'T' || type == 'R';
        bool isRtr = type == 'r' || type == 'R';

        if (type != 't' && type != 'T' && type != 'r' && type != 'R') return;

        try
        {
            int idLen = extended ? 8 : 3;
            if (line.Length < 1 + idLen + 1) return;

            uint id = uint.Parse(line.Substring(1, idLen), System.Globalization.NumberStyles.HexNumber);
            byte dlc = (byte)(line[1 + idLen] - '0');

            var frame = new CanFrame
            {
                Id = id,
                Dlc = dlc,
                IsExtended = extended,
                IsRtr = isRtr,
                Timestamp = DateTime.Now,
            };

            int dataStart = 2 + idLen;
            for (int i = 0; i < dlc && dataStart + i * 2 + 1 < line.Length; i++)
            {
                frame.Data[i] = byte.Parse(line.Substring(dataStart + i * 2, 2),
                    System.Globalization.NumberStyles.HexNumber);
            }

            FrameReceived?.Invoke(this, new CanFrameEventArgs(frame));
        }
        catch { }
    }

    public void Dispose()
    {
        Disconnect();
        GC.SuppressFinalize(this);
    }
}
