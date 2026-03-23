using CanLinConfig.Parsers;
using CanLinConfig.Services;

namespace CanLinConfig.Tests;

public class SignalExtractorTests
{
    private static DbcSignal Sig(string name, int startBit, int bitLength,
        bool littleEndian = true, bool signed_ = false,
        double factor = 1.0, double offset = 0.0, string unit = "")
        => new()
        {
            Name = name, StartBit = startBit, BitLength = bitLength,
            IsLittleEndian = littleEndian, IsSigned = signed_,
            Factor = factor, Offset = offset, Unit = unit,
            MinValue = 0, MaxValue = 0
        };

    [Fact]
    public void Extract_Intel_8bit_byte0()
    {
        var signal = Sig("TestSig", startBit: 0, bitLength: 8);
        byte[] data = [0xAB, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        double raw = SignalExtractor.ExtractRaw(data, signal);
        Assert.Equal(0xAB, raw);
    }

    [Fact]
    public void Extract_Intel_16bit_spanning_bytes()
    {
        var signal = Sig("RPM", startBit: 8, bitLength: 16);
        byte[] data = [0x00, 0xD2, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00];
        double raw = SignalExtractor.ExtractRaw(data, signal);
        Assert.Equal(1234.0, raw);
    }

    [Fact]
    public void Extract_Intel_with_factor_offset()
    {
        var signal = Sig("Temp", startBit: 0, bitLength: 8, factor: 0.5, offset: -40, unit: "C");
        byte[] data = [200, 0, 0, 0, 0, 0, 0, 0];
        double physical = SignalExtractor.ExtractPhysical(data, signal);
        Assert.Equal(60.0, physical);
    }

    [Fact]
    public void Extract_Intel_signed_negative()
    {
        var signal = Sig("Temp", startBit: 0, bitLength: 8, signed_: true);
        byte[] data = [0xFE, 0, 0, 0, 0, 0, 0, 0];
        double raw = SignalExtractor.ExtractRaw(data, signal);
        Assert.Equal(-2.0, raw);
    }

    [Fact]
    public void Extract_Intel_4bit_nibble()
    {
        var signal = Sig("Gear", startBit: 4, bitLength: 4);
        byte[] data = [0xA7, 0, 0, 0, 0, 0, 0, 0];
        double raw = SignalExtractor.ExtractRaw(data, signal);
        Assert.Equal(10.0, raw);
    }

    [Fact]
    public void Extract_Intel_1bit_boolean()
    {
        var signal = Sig("EngineOn", startBit: 2, bitLength: 1);
        byte[] data = [0x04, 0, 0, 0, 0, 0, 0, 0];
        double raw = SignalExtractor.ExtractRaw(data, signal);
        Assert.Equal(1.0, raw);
    }

    [Fact]
    public void Extract_Motorola_16bit()
    {
        var signal = Sig("Speed", startBit: 7, bitLength: 16, littleEndian: false);
        byte[] data = [0x04, 0xD2, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        double raw = SignalExtractor.ExtractRaw(data, signal);
        Assert.Equal(1234.0, raw);
    }

    [Fact]
    public void Extract_Motorola_8bit()
    {
        var signal = Sig("Status", startBit: 7, bitLength: 8, littleEndian: false);
        byte[] data = [0xAB, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        double raw = SignalExtractor.ExtractRaw(data, signal);
        Assert.Equal(0xAB, raw);
    }

    [Fact]
    public void Extract_Motorola_12bit()
    {
        var signal = Sig("Value", startBit: 7, bitLength: 12, littleEndian: false);
        byte[] data = [0xAB, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        double raw = SignalExtractor.ExtractRaw(data, signal);
        Assert.Equal(2748.0, raw);
    }
}
