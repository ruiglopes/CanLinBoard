namespace CanLinConfig.Adapters;

public interface ICanAdapter : IDisposable
{
    bool IsConnected { get; }
    string AdapterName { get; }
    event EventHandler<CanFrameEventArgs>? FrameReceived;
    Task<bool> ConnectAsync(string channel, uint bitrate);
    void Disconnect();
    bool Send(CanFrame frame);
    IReadOnlyList<string> GetAvailableChannels();
}
