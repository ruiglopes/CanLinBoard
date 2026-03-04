namespace CanLinConfig.Adapters;

public class CanFrame
{
    public uint Id { get; set; }
    public byte Dlc { get; set; }
    public byte[] Data { get; set; } = new byte[8];
    public bool IsExtended { get; set; }
    public bool IsRtr { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;

    public CanFrame() { }

    public CanFrame(uint id, byte[] data, byte dlc = 0)
    {
        Id = id;
        Data = new byte[8];
        int len = Math.Min(data.Length, 8);
        Array.Copy(data, Data, len);
        Dlc = dlc > 0 ? dlc : (byte)len;
    }

    public override string ToString()
    {
        var hex = string.Join(" ", Data.Take(Dlc).Select(b => b.ToString("X2")));
        return $"0x{Id:X3} [{Dlc}] {hex}";
    }
}

public class CanFrameEventArgs : EventArgs
{
    public CanFrame Frame { get; }

    public CanFrameEventArgs(CanFrame frame)
    {
        Frame = frame;
    }
}
