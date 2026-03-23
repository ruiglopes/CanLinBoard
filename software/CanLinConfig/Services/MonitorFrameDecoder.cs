using CanLinConfig.Adapters;
using CanLinConfig.Models;
using CanLinConfig.Protocol;

namespace CanLinConfig.Services;

/// <summary>
/// Pairs monitor header (0x604) and data (0x605) frames by sequence number,
/// reconstructing the original BusFrame.
/// </summary>
public class MonitorFrameDecoder
{
    private CanFrame? _pendingHeader;
    private int _expectedSeq = -1;  // -1 = no expectation yet (first frame)
    private uint _seqGapCount;

    public event EventHandler<BusFrame>? FrameDecoded;

    public uint SequenceGapCount => _seqGapCount;

    public void Reset()
    {
        _pendingHeader = null;
        _expectedSeq = -1;
        _seqGapCount = 0;
    }

    public void OnCanFrame(CanFrame frame)
    {
        if (frame.Id == ProtocolConstants.MonitorHeaderId)
            HandleHeader(frame);
        else if (frame.Id == ProtocolConstants.MonitorDataId)
            HandleData(frame);
    }

    private void HandleHeader(CanFrame header)
    {
        if (header.Dlc < 8) return;

        byte seq = header.Data[0];
        byte dlc = (byte)(header.Data[6] & 0x0F);

        // Check for sequence gap: either we expected a different seq from
        // a previous completed frame, or we have a pending header whose
        // data frame never arrived (consecutive headers = dropped data).
        if (_expectedSeq >= 0 && seq != _expectedSeq)
        {
            _seqGapCount++;
        }
        else if (_pendingHeader != null)
        {
            // Previous header's data was lost — count as gap
            _seqGapCount++;
        }

        if (dlc == 0)
        {
            // DLC=0 — header-only frame, no data frame expected
            var busFrame = DecodeHeaderOnly(header);
            _expectedSeq = (seq + 1) & 0xFF;
            _pendingHeader = null;
            FrameDecoded?.Invoke(this, busFrame);
        }
        else
        {
            _pendingHeader = header;
        }
    }

    private void HandleData(CanFrame data)
    {
        if (_pendingHeader == null) return;

        byte headerSeq = _pendingHeader.Data[0];
        byte dataSeq = data.Data[0];

        if (dataSeq != headerSeq)
        {
            _pendingHeader = null;
            return;
        }

        var busFrame = DecodeHeaderData(_pendingHeader, data);
        _expectedSeq = (headerSeq + 1) & 0xFF;
        _pendingHeader = null;
        FrameDecoded?.Invoke(this, busFrame);
    }

    private static BusFrame DecodeHeaderOnly(CanFrame header)
    {
        byte busId = (byte)(header.Data[1] & 0x0F);
        bool extended = (header.Data[1] & 0x10) != 0;
        uint id = (uint)(header.Data[2] | (header.Data[3] << 8) |
                         (header.Data[4] << 16) | (header.Data[5] << 24));

        return new BusFrame(
            (BusFrame.Bus)busId, id, 0, new byte[8],
            DateTime.Now, extended);
    }

    private static BusFrame DecodeHeaderData(CanFrame header, CanFrame data)
    {
        byte busId = (byte)(header.Data[1] & 0x0F);
        bool extended = (header.Data[1] & 0x10) != 0;
        uint id = (uint)(header.Data[2] | (header.Data[3] << 8) |
                         (header.Data[4] << 16) | (header.Data[5] << 24));
        byte dlc = (byte)(header.Data[6] & 0x0F);

        var payload = new byte[8];
        int copyLen = Math.Min((int)dlc, 7);
        if (data.Dlc > 1)
            Array.Copy(data.Data, 1, payload, 0, Math.Min(copyLen, data.Dlc - 1));

        if (dlc == 8)
            payload[7] = (byte)((header.Data[6] >> 4) & 0x0F);

        return new BusFrame(
            (BusFrame.Bus)busId, id, dlc, payload,
            DateTime.Now, extended);
    }
}
