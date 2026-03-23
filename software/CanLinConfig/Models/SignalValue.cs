namespace CanLinConfig.Models;

public record SignalValue(
    string Name,
    double RawValue,
    double PhysicalValue,
    string Unit,
    byte Bus,
    uint FrameId,
    DateTime Timestamp);
