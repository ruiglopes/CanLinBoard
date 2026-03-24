namespace CanLinConfig.Models;

/// <summary>
/// A CAN/LIN frame tagged with its source bus.
/// Wraps CanFrame for the BusDataService pipeline.
/// </summary>
public class BusFrame
{
    public enum Bus : byte
    {
        CAN1 = 0,
        CAN2 = 1,
        LIN1 = 2,
        LIN2 = 3,
        LIN3 = 4,
        LIN4 = 5,
        Unknown = 0xFF
    }

    public Bus SourceBus { get; }
    public uint Id { get; }
    public byte Dlc { get; }
    public byte[] Data { get; }
    public DateTime Timestamp { get; }
    public bool IsExtended { get; }

    public BusFrame(Adapters.CanFrame frame, Bus sourceBus = Bus.CAN1)
    {
        SourceBus = sourceBus;
        Id = frame.Id;
        Dlc = frame.Dlc;
        Data = frame.Data;
        Timestamp = frame.Timestamp;
        IsExtended = frame.IsExtended;
    }

    public BusFrame(Bus sourceBus, uint id, byte dlc, byte[] data, DateTime timestamp, bool isExtended = false)
    {
        SourceBus = sourceBus;
        Id = id;
        Dlc = dlc;
        Data = data;
        Timestamp = timestamp;
        IsExtended = isExtended;
    }

    public string BusName => SourceBus switch
    {
        Bus.CAN1 => "CAN1",
        Bus.CAN2 => "CAN2",
        Bus.LIN1 => "LIN1",
        Bus.LIN2 => "LIN2",
        Bus.LIN3 => "LIN3",
        Bus.LIN4 => "LIN4",
        _ => "???"
    };
}
