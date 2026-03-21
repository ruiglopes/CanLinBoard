namespace CanBus;

/// <summary>
/// Minimal transport interface for protocol consumers.
/// SendCommand prepends commandId to payload before sending on CmdCanId.
/// SendData sends a raw frame on the given CAN ID (no framing).
/// </summary>
public interface IProtocolTransport
{
    void SendCommand(byte commandId, byte[] payload);
    (byte Status, byte[] Payload)? ReceiveResponse(byte commandFilter, int timeoutMs);
    void SendData(uint canId, byte[] data);
    void Drain(int durationMs = 200);
    int Scaled(int baseTimeoutMs);
    uint CmdCanId { get; }
    uint RspCanId { get; }
    uint DataCanId { get; }
}
