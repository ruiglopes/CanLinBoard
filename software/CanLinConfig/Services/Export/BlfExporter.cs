using System.IO;
using System.Text;
using CanLinConfig.Models;

namespace CanLinConfig.Services.Export;

public class BlfExporter : IFrameExporter
{
    public string FileExtension => ".blf";
    public string FileFilter => "BLF Files (*.blf)|*.blf";

    private const string FileSignature = "BLF0400";
    private const uint HeaderSize = 144;
    private const uint ObjectSignature = 0x4F4A4C42; // "LOBJ"
    private const uint CanMsgObjectType = 1;
    private const uint ContainerObjectType = 10;

    public void Export(Stream stream, IReadOnlyList<BusFrame> frames, DatabaseManager? dbManager = null)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        var startTime = frames.Count > 0 ? frames[0].Timestamp : DateTime.UtcNow;
        var endTime = frames.Count > 0 ? frames[^1].Timestamp : startTime;

        WriteFileHeader(writer, startTime, endTime, (uint)frames.Count);

        foreach (var frame in frames)
            WriteCanMessageContainer(writer, frame, startTime);

        // Update file size in header
        var fileSize = stream.Position;
        stream.Seek(8, SeekOrigin.Begin);
        writer.Write((uint)fileSize);
    }

    private static void WriteFileHeader(BinaryWriter w, DateTime start, DateTime end, uint objectCount)
    {
        var pos = w.BaseStream.Position;
        w.Write(Encoding.ASCII.GetBytes(FileSignature));
        w.Write((byte)0);
        w.Write((uint)0);        // file size placeholder (offset 8)
        w.Write((uint)0x0403);   // API version
        w.Write((uint)1);        // platform (Windows)
        w.Write((uint)0);        // creation flags
        WriteSystemTime(w, start);
        WriteSystemTime(w, end);
        w.Write(objectCount);
        w.Write((uint)0);        // object read count
        w.Write((long)0);        // start timestamp ns
        var durationNs = (long)((end - start).TotalSeconds * 1_000_000_000);
        w.Write(durationNs);
        var written = w.BaseStream.Position - pos;
        if (written < HeaderSize)
            w.Write(new byte[HeaderSize - written]);
    }

    private static void WriteCanMessageContainer(BinaryWriter w, BusFrame frame, DateTime startTime)
    {
        // Build CAN_MESSAGE object
        using var objMs = new MemoryStream();
        using var objW = new BinaryWriter(objMs);

        // Object header (16 bytes)
        objW.Write(ObjectSignature);
        objW.Write((ushort)16);          // header size
        objW.Write((ushort)1);           // header version
        objW.Write((uint)40);            // object size (16 header + 24 CAN data)
        objW.Write(CanMsgObjectType);

        // Timestamp ns
        var tsNs = (long)((frame.Timestamp - startTime).TotalSeconds * 1_000_000_000);
        objW.Write(tsNs);

        // CAN_MESSAGE data (24 bytes)
        objW.Write((ushort)BusToChannel(frame.SourceBus));
        objW.Write(frame.Dlc);
        objW.Write((byte)0);             // flags
        objW.Write(frame.Id);
        var data = new byte[8];
        Array.Copy(frame.Data, data, Math.Min((int)frame.Dlc, 8));
        objW.Write(data);

        var objectData = objMs.ToArray();

        // Uncompressed container
        w.Write(ObjectSignature);
        w.Write((ushort)16);
        w.Write((ushort)1);
        w.Write((uint)(16 + 16 + objectData.Length)); // container size
        w.Write(ContainerObjectType);

        // Container fields (16 bytes)
        w.Write((ushort)0);              // compression: uncompressed
        w.Write(new byte[6]);            // padding
        w.Write((uint)objectData.Length); // uncompressed size
        w.Write(new byte[4]);            // padding

        w.Write(objectData);
    }

    private static void WriteSystemTime(BinaryWriter w, DateTime dt)
    {
        w.Write((ushort)dt.Year);
        w.Write((ushort)dt.Month);
        w.Write((ushort)dt.DayOfWeek);
        w.Write((ushort)dt.Day);
        w.Write((ushort)dt.Hour);
        w.Write((ushort)dt.Minute);
        w.Write((ushort)dt.Second);
        w.Write((ushort)dt.Millisecond);
    }

    private static ushort BusToChannel(BusFrame.Bus bus) => bus switch
    {
        BusFrame.Bus.CAN1 => 1, BusFrame.Bus.CAN2 => 2,
        BusFrame.Bus.LIN1 => 3, BusFrame.Bus.LIN2 => 4,
        BusFrame.Bus.LIN3 => 5, BusFrame.Bus.LIN4 => 6, _ => 1
    };
}
