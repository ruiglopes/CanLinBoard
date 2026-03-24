using CanLinConfig.Adapters;
using CanLinConfig.Models;
using CanLinConfig.Services;

namespace CanLinConfig.Tests;

public class MonitorFrameDecoderTests
{
    private static CanFrame MakeHeader(byte seq, byte bus, uint id, byte dlc,
        byte data7 = 0, byte tsDelta = 0, bool extended = false)
    {
        var frame = new CanFrame { Id = 0x604, Dlc = 8 };
        frame.Data[0] = seq;
        frame.Data[1] = (byte)((bus & 0x0F) | (extended ? 0x10 : 0x00));
        frame.Data[2] = (byte)(id);
        frame.Data[3] = (byte)(id >> 8);
        frame.Data[4] = (byte)(id >> 16);
        frame.Data[5] = (byte)(id >> 24);
        frame.Data[6] = (byte)((dlc & 0x0F) | ((dlc == 8 ? (data7 & 0x0F) : 0) << 4));
        frame.Data[7] = tsDelta;
        return frame;
    }

    private static CanFrame MakeData(byte seq, byte[] payload)
    {
        var frame = new CanFrame { Id = 0x605, Dlc = (byte)(1 + payload.Length) };
        frame.Data[0] = seq;
        Array.Copy(payload, 0, frame.Data, 1, payload.Length);
        return frame;
    }

    [Fact]
    public void Decode_standard_CAN2_frame()
    {
        var decoder = new MonitorFrameDecoder();
        BusFrame? result = null;
        decoder.FrameDecoded += (_, f) => result = f;

        decoder.OnCanFrame(MakeHeader(0, 1, 0x123, 4));
        decoder.OnCanFrame(MakeData(0, [0xAA, 0xBB, 0xCC, 0xDD]));

        Assert.NotNull(result);
        Assert.Equal(BusFrame.Bus.CAN2, result!.SourceBus);
        Assert.Equal(0x123u, result.Id);
        Assert.Equal(4, result.Dlc);
        Assert.Equal(0xAA, result.Data[0]);
        Assert.Equal(0xDD, result.Data[3]);
        Assert.False(result.IsExtended);
    }

    [Fact]
    public void Decode_DLC8_frame_with_data7_in_header()
    {
        var decoder = new MonitorFrameDecoder();
        BusFrame? result = null;
        decoder.FrameDecoded += (_, f) => result = f;

        decoder.OnCanFrame(MakeHeader(5, 0, 0x200, 8, data7: 0xAB));
        decoder.OnCanFrame(MakeData(5, [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07]));

        Assert.NotNull(result);
        Assert.Equal(8, result!.Dlc);
        Assert.Equal(0x07, result.Data[6]);
        // data[7] comes from header byte 6 upper nibble: 0xAB & 0x0F = 0x0B
        Assert.Equal(0x0B, result.Data[7]);
    }

    [Fact]
    public void Decode_DLC0_header_only()
    {
        var decoder = new MonitorFrameDecoder();
        BusFrame? result = null;
        decoder.FrameDecoded += (_, f) => result = f;

        decoder.OnCanFrame(MakeHeader(10, 2, 0x3C, 0));

        Assert.NotNull(result);
        Assert.Equal(BusFrame.Bus.LIN1, result!.SourceBus);
        Assert.Equal(0x3Cu, result.Id);
        Assert.Equal(0, result.Dlc);
    }

    [Fact]
    public void Decode_LIN4_frame()
    {
        var decoder = new MonitorFrameDecoder();
        BusFrame? result = null;
        decoder.FrameDecoded += (_, f) => result = f;

        decoder.OnCanFrame(MakeHeader(0, 5, 0x1A, 3));
        decoder.OnCanFrame(MakeData(0, [0x10, 0x20, 0x30]));

        Assert.NotNull(result);
        Assert.Equal(BusFrame.Bus.LIN4, result!.SourceBus);
    }

    [Fact]
    public void Decode_extended_flag()
    {
        var decoder = new MonitorFrameDecoder();
        BusFrame? result = null;
        decoder.FrameDecoded += (_, f) => result = f;

        decoder.OnCanFrame(MakeHeader(0, 0, 0x1FFFFFFF, 2, extended: true));
        decoder.OnCanFrame(MakeData(0, [0xAA, 0xBB]));

        Assert.NotNull(result);
        Assert.True(result!.IsExtended);
        Assert.Equal(0x1FFFFFFFu, result.Id);
    }

    [Fact]
    public void Sequence_gap_increments_counter()
    {
        var decoder = new MonitorFrameDecoder();
        BusFrame? result = null;
        decoder.FrameDecoded += (_, f) => result = f;

        decoder.OnCanFrame(MakeHeader(0, 1, 0x100, 2));
        decoder.OnCanFrame(MakeData(0, [0x01, 0x02]));
        Assert.NotNull(result);
        Assert.Equal(0u, decoder.SequenceGapCount);

        result = null;
        decoder.OnCanFrame(MakeHeader(2, 1, 0x101, 2));
        decoder.OnCanFrame(MakeData(2, [0x03, 0x04]));
        Assert.NotNull(result);
        Assert.Equal(1u, decoder.SequenceGapCount);
    }

    [Fact]
    public void Sequence_wraps_at_255()
    {
        var decoder = new MonitorFrameDecoder();
        int count = 0;
        decoder.FrameDecoded += (_, _) => count++;

        decoder.OnCanFrame(MakeHeader(254, 0, 0x100, 1));
        decoder.OnCanFrame(MakeData(254, [0x01]));
        decoder.OnCanFrame(MakeHeader(255, 0, 0x100, 1));
        decoder.OnCanFrame(MakeData(255, [0x02]));
        decoder.OnCanFrame(MakeHeader(0, 0, 0x100, 1));
        decoder.OnCanFrame(MakeData(0, [0x03]));

        Assert.Equal(3, count);
        Assert.Equal(0u, decoder.SequenceGapCount);
    }

    [Fact]
    public void Mismatched_data_seq_is_discarded()
    {
        var decoder = new MonitorFrameDecoder();
        BusFrame? result = null;
        decoder.FrameDecoded += (_, f) => result = f;

        decoder.OnCanFrame(MakeHeader(0, 1, 0x100, 4));
        decoder.OnCanFrame(MakeData(5, [0x01, 0x02, 0x03, 0x04]));

        Assert.Null(result);
    }

    [Fact]
    public void Non_monitor_frames_ignored()
    {
        var decoder = new MonitorFrameDecoder();
        BusFrame? result = null;
        decoder.FrameDecoded += (_, f) => result = f;

        var frame = new CanFrame { Id = 0x100, Dlc = 8 };
        decoder.OnCanFrame(frame);

        Assert.Null(result);
    }

    [Fact]
    public void Consecutive_headers_overwrites_pending()
    {
        var decoder = new MonitorFrameDecoder();
        BusFrame? result = null;
        decoder.FrameDecoded += (_, f) => result = f;

        decoder.OnCanFrame(MakeHeader(0, 1, 0x100, 4));
        decoder.OnCanFrame(MakeHeader(1, 1, 0x200, 2));
        decoder.OnCanFrame(MakeData(1, [0xAA, 0xBB]));

        Assert.NotNull(result);
        Assert.Equal(0x200u, result!.Id);
        Assert.Equal(2, result.Dlc);
        Assert.Equal(1u, decoder.SequenceGapCount);
    }
}
