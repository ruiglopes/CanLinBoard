// software/CanLinConfig.Tests/LogDownloadViewModelTests.cs
using CanLinConfig.ViewModels;

namespace CanLinConfig.Tests;

public class LogDownloadViewModelTests
{
    [Fact]
    public void ParseLogEntries_parses_single_entry()
    {
        var data = new byte[20];
        // timestamp_ms = 1000 (0x000003E8)
        data[0] = 0xE8; data[1] = 0x03; data[2] = 0x00; data[3] = 0x00;
        // frame_id = 0x123
        data[4] = 0x23; data[5] = 0x01; data[6] = 0x00; data[7] = 0x00;
        // bus = 1 (CAN2)
        data[8] = 0x01;
        // dlc = 4
        data[9] = 0x04;
        // data = AA BB CC DD 00 00 00 00
        data[10] = 0xAA; data[11] = 0xBB; data[12] = 0xCC; data[13] = 0xDD;
        // reserved
        data[18] = 0x00; data[19] = 0x00;

        var entries = LogDownloadViewModel.ParseLogEntries(data);

        Assert.Single(entries);
        Assert.Equal(1000u, entries[0].TimestampMs);
        Assert.Equal(0x123u, entries[0].FrameId);
        Assert.Equal(1, entries[0].Bus);
        Assert.Equal(4, entries[0].Dlc);
        Assert.Equal(0xAA, entries[0].Data[0]);
        Assert.Equal(0xDD, entries[0].Data[3]);
    }

    [Fact]
    public void ParseLogEntries_skips_gap_markers()
    {
        var data = new byte[40]; // 2 entries
        // Entry 1: normal frame
        data[8] = 0x00; // bus = CAN1
        data[9] = 0x02; // dlc = 2

        // Entry 2: gap marker (bus = 0xFF)
        data[28] = 0xFF; // bus = gap marker

        var entries = LogDownloadViewModel.ParseLogEntries(data);

        Assert.Single(entries); // gap marker skipped
    }

    [Fact]
    public void ParseLogEntries_handles_empty_data()
    {
        var entries = LogDownloadViewModel.ParseLogEntries(Array.Empty<byte>());
        Assert.Empty(entries);
    }

    [Fact]
    public void ParseLogEntries_handles_partial_entry()
    {
        // 15 bytes — not enough for one full entry (20 bytes)
        var data = new byte[15];
        var entries = LogDownloadViewModel.ParseLogEntries(data);
        Assert.Empty(entries);
    }

    [Fact]
    public void ParseLogEntries_multiple_entries()
    {
        var data = new byte[60]; // 3 entries
        // Entry 0: CAN1, ID=0x100
        data[4] = 0x00; data[5] = 0x01; data[8] = 0x00; data[9] = 0x03;
        // Entry 1: CAN2, ID=0x200
        data[24] = 0x00; data[25] = 0x02; data[28] = 0x01; data[29] = 0x05;
        // Entry 2: LIN1, ID=0x3C
        data[44] = 0x3C; data[48] = 0x02; data[49] = 0x08;

        var entries = LogDownloadViewModel.ParseLogEntries(data);

        Assert.Equal(3, entries.Count);
        Assert.Equal(0x100u, entries[0].FrameId);
        Assert.Equal(0x200u, entries[1].FrameId);
        Assert.Equal(0x3Cu, entries[2].FrameId);
    }
}
