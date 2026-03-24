using System;

namespace CanBus;

public interface ICanAdapter : IDisposable
{
    string AdapterName { get; }
    string[] ChannelNames { get; }
    string[] BitrateNames { get; }
    void Initialize(int channelIndex, int bitrateIndex);

    bool IsConnected { get; }
    void Send(uint canId, byte[] data);
    (uint Id, byte[] Data)? Receive(int timeoutMs, uint? filterCanId = null);
    (uint Id, byte[] Data)? ReceiveMultiFilter(int timeoutMs, params uint[] acceptIds);
    void Drain(int durationMs = 200);
    void Shutdown();

    uint DebugCanId { get; set; }
    uint ResponseCanId { get; set; }

    event Action<byte[]>? DebugMessageReceived;
    event Action? HeartbeatReceived;
    event Action<CanTraceFrame>? FrameTraced;
}
