using Peak.Can.Basic;
using Peak.Can.Basic.BackwardCompatibility;

namespace CanLinConfig.Adapters;

public class PcanAdapter : ICanAdapter
{
    private PcanChannel _channel;
    private bool _connected;
    private Thread? _rxThread;
    private volatile bool _rxRunning;

    public bool IsConnected => _connected;
    public string AdapterName => "PCAN";
    public event EventHandler<CanFrameEventArgs>? FrameReceived;

    private static readonly Dictionary<string, PcanChannel> ChannelMap = new()
    {
        ["PCAN_USBBUS1"] = PcanChannel.Usb01,
        ["PCAN_USBBUS2"] = PcanChannel.Usb02,
        ["PCAN_USBBUS3"] = PcanChannel.Usb03,
        ["PCAN_USBBUS4"] = PcanChannel.Usb04,
        ["PCAN_USBBUS5"] = PcanChannel.Usb05,
        ["PCAN_USBBUS6"] = PcanChannel.Usb06,
        ["PCAN_USBBUS7"] = PcanChannel.Usb07,
        ["PCAN_USBBUS8"] = PcanChannel.Usb08,
    };

    private static Bitrate GetBitrate(uint bps) => bps switch
    {
        125000 => Bitrate.Pcan125,
        250000 => Bitrate.Pcan250,
        500000 => Bitrate.Pcan500,
        1000000 => Bitrate.Pcan1000,
        _ => Bitrate.Pcan500,
    };

    public IReadOnlyList<string> GetAvailableChannels()
    {
        var channels = new List<string>();
        try
        {
            foreach (var kvp in ChannelMap)
            {
                var result = Api.GetStatus(kvp.Value);
                // Channel exists if we get OK or BusLight/BusHeavy/BusOff (not IllegalHandle)
                if (result != PcanStatus.IllegalHandle)
                    channels.Add(kvp.Key);
            }
        }
        catch
        {
            // PCAN driver not installed
        }
        return channels;
    }

    public Task<bool> ConnectAsync(string channel, uint bitrate)
    {
        if (_connected) Disconnect();

        if (!ChannelMap.TryGetValue(channel, out var pcanCh))
            return Task.FromResult(false);

        try
        {
            var result = Api.Initialize(pcanCh, GetBitrate(bitrate));
            if (result != PcanStatus.OK)
                return Task.FromResult(false);

            _channel = pcanCh;
            _connected = true;

            _rxRunning = true;
            _rxThread = new Thread(ReceiveLoop)
            {
                IsBackground = true,
                Name = "PCAN_RX"
            };
            _rxThread.Start();

            return Task.FromResult(true);
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

        if (_connected)
        {
            try { Api.Uninitialize(_channel); } catch { }
            _connected = false;
        }
    }

    public bool Send(CanFrame frame)
    {
        if (!_connected) return false;
        try
        {
            var msgType = frame.IsExtended ? MessageType.Extended : MessageType.Standard;
            var data = new byte[8];
            Array.Copy(frame.Data, data, Math.Min((int)frame.Dlc, 8));
            var msg = new PcanMessage(frame.Id, msgType, frame.Dlc, data, false);

            var result = Api.Write(_channel, msg);
            return result == PcanStatus.OK;
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
                var result = Api.Read(_channel, out var msg, out _);
                if (result == PcanStatus.OK)
                {
                    var frame = new CanFrame
                    {
                        Id = msg.ID,
                        Dlc = msg.DLC,
                        IsExtended = msg.MsgType.HasFlag(MessageType.Extended),
                        IsRtr = msg.MsgType.HasFlag(MessageType.RemoteRequest),
                        Timestamp = DateTime.Now,
                    };
                    Array.Copy(msg.Data, frame.Data, Math.Min((int)msg.DLC, 8));
                    FrameReceived?.Invoke(this, new CanFrameEventArgs(frame));
                }
                else if (result == PcanStatus.ReceiveQueueEmpty)
                {
                    Thread.Sleep(1);
                }
                else
                {
                    Thread.Sleep(10);
                }
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
