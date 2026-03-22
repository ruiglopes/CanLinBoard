using System;
using System.IO;

namespace CanLinConfig.Models;

/// <summary>
/// Parses and validates .dfw dual-bank firmware container files.
/// Format: 16-byte header (magic, format_version, bank_a_size, bank_b_size) + Bank A image + Bank B image.
/// </summary>
public class DfwContainer
{
    public const uint DfwMagic = 0x44465701;
    public const int DfwHeaderSize = 16;

    // Bank A entry point range: 0x10008000–0x10207FFF
    public const uint BankAEntryMin = 0x10008000;
    public const uint BankAEntryMax = 0x10207FFF;

    // Bank B entry point range: 0x10208000–0x103FFFFF
    public const uint BankBEntryMin = 0x10208000;
    public const uint BankBEntryMax = 0x103FFFFF;

    public AppHeader BankA { get; private set; } = null!;
    public byte[] BankAFirmware { get; private set; } = Array.Empty<byte>();
    public AppHeader BankB { get; private set; } = null!;
    public byte[] BankBFirmware { get; private set; } = Array.Empty<byte>();
    public string VersionString => BankA.VersionString;
    public uint RawVersion => BankA.RawVersion;

    /// <summary>
    /// Validate a .dfw container. Returns an error message, or null if valid.
    /// </summary>
    public static string? Validate(byte[] data)
    {
        if (data.Length < DfwHeaderSize)
            return "File too small for .dfw header (minimum 16 bytes)";

        uint magic = BitConverter.ToUInt32(data, 0);
        if (magic != DfwMagic)
            return $"Bad .dfw magic: 0x{magic:X8}, expected 0x{DfwMagic:X8}";

        uint formatVersion = BitConverter.ToUInt32(data, 4);
        if (formatVersion != 1)
            return $"Unsupported .dfw format version: {formatVersion}, expected 1";

        uint bankASize = BitConverter.ToUInt32(data, 8);
        uint bankBSize = BitConverter.ToUInt32(data, 12);

        if (data.Length != DfwHeaderSize + bankASize + bankBSize)
            return $"File size mismatch: expected {DfwHeaderSize + bankASize + bankBSize} bytes, got {data.Length}";

        // Parse Bank A
        byte[] bankAData = new byte[bankASize];
        Array.Copy(data, DfwHeaderSize, bankAData, 0, (int)bankASize);
        var bankA = AppHeader.TryParse(bankAData);
        if (bankA == null || !bankA.IsValid)
            return "Bank A image is not a valid firmware binary";

        // Validate Bank A entry point range
        if (bankA.EntryPoint < BankAEntryMin || bankA.EntryPoint > BankAEntryMax)
            return $"Bank A entry point 0x{bankA.EntryPoint:X8} outside range 0x{BankAEntryMin:X8}–0x{BankAEntryMax:X8}";

        // Parse Bank B
        byte[] bankBData = new byte[bankBSize];
        Array.Copy(data, DfwHeaderSize + (int)bankASize, bankBData, 0, (int)bankBSize);
        var bankB = AppHeader.TryParse(bankBData);
        if (bankB == null || !bankB.IsValid)
            return "Bank B image is not a valid firmware binary";

        // Validate Bank B entry point range
        if (bankB.EntryPoint < BankBEntryMin || bankB.EntryPoint > BankBEntryMax)
            return $"Bank B entry point 0x{bankB.EntryPoint:X8} outside range 0x{BankBEntryMin:X8}–0x{BankBEntryMax:X8}";

        // Both banks must have same version
        if (bankA.RawVersion != bankB.RawVersion)
            return $"Version mismatch: Bank A v{bankA.VersionString}, Bank B v{bankB.VersionString}";

        return null;
    }

    /// <summary>
    /// Parse a .dfw container from a file path. Returns null on failure.
    /// </summary>
    public static DfwContainer? TryParse(string filePath)
    {
        if (!File.Exists(filePath))
            return null;

        var data = File.ReadAllBytes(filePath);
        return TryParse(data);
    }

    /// <summary>
    /// Parse a .dfw container from raw bytes. Returns null if validation fails.
    /// </summary>
    public static DfwContainer? TryParse(byte[] data)
    {
        if (Validate(data) != null)
            return null;

        uint bankASize = BitConverter.ToUInt32(data, 8);
        uint bankBSize = BitConverter.ToUInt32(data, 12);

        byte[] bankAData = new byte[bankASize];
        Array.Copy(data, DfwHeaderSize, bankAData, 0, (int)bankASize);

        byte[] bankBData = new byte[bankBSize];
        Array.Copy(data, DfwHeaderSize + (int)bankASize, bankBData, 0, (int)bankBSize);

        return new DfwContainer
        {
            BankA = AppHeader.TryParse(bankAData)!,
            BankAFirmware = bankAData,
            BankB = AppHeader.TryParse(bankBData)!,
            BankBFirmware = bankBData,
        };
    }
}
