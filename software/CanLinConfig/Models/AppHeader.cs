using System;
using System.IO;
using System.Security.Cryptography;

namespace CanLinConfig.Models;

public class AppHeader
{
    public const uint Magic = 0x41505001;
    public const int HeaderSize = 256;
    public const uint AppMaxSize = 4161536;  // 4MB - 32KB
    public const uint AppBaseAddress = 0x10008000;
    public const int HmacOffset = 20;  // offset of fw_hmac in header
    public const int HmacSize = 32;    // HMAC-SHA256 output size

    public uint RawMagic { get; private set; }
    public uint RawVersion { get; private set; }
    public uint Size { get; private set; }
    public uint Crc32 { get; private set; }
    public uint EntryPoint { get; private set; }
    public byte[] FwHmac { get; private set; } = new byte[HmacSize];

    public int Major => (int)((RawVersion >> 16) & 0xFF);
    public int Minor => (int)((RawVersion >> 8) & 0xFF);
    public int Patch => (int)(RawVersion & 0xFF);
    public string VersionString => $"{Major}.{Minor}.{Patch}";

    public bool MagicValid => RawMagic == Magic;
    public bool CrcValid { get; private set; }
    public bool SizeValid => Size <= AppMaxSize && Size == (uint)AppData.Length;
    public bool EntryPointValid => EntryPoint >= AppBaseAddress && EntryPoint < AppBaseAddress + AppMaxSize;
    public bool HasSignature => !IsAllSame(FwHmac, 0x00) && !IsAllSame(FwHmac, 0xFF);
    public bool IsValid => MagicValid && CrcValid && SizeValid && EntryPointValid;

    public byte[] FullBinary { get; private set; } = Array.Empty<byte>();
    public byte[] AppData { get; private set; } = Array.Empty<byte>();

    public static AppHeader? TryParse(string filePath)
    {
        if (!File.Exists(filePath))
            return null;

        var fi = new FileInfo(filePath);
        if (fi.Length > AppMaxSize + HeaderSize + (1024 * 1024))
            return null;  // Reject obviously oversized files to prevent OOM

        var data = File.ReadAllBytes(filePath);
        return TryParse(data);
    }

    public static AppHeader? TryParse(byte[] data)
    {
        if (data.Length < HeaderSize)
            return null;

        var header = new AppHeader
        {
            RawMagic = BitConverter.ToUInt32(data, 0),
            RawVersion = BitConverter.ToUInt32(data, 4),
            Size = BitConverter.ToUInt32(data, 8),
            Crc32 = BitConverter.ToUInt32(data, 12),
            EntryPoint = BitConverter.ToUInt32(data, 16),
            FwHmac = data[HmacOffset..(HmacOffset + HmacSize)],
            FullBinary = data,
            AppData = new byte[data.Length - HeaderSize],
        };

        Array.Copy(data, HeaderSize, header.AppData, 0, header.AppData.Length);

        uint actualCrc = ComputeCrc32(header.AppData);
        header.CrcValid = actualCrc == header.Crc32;

        return header;
    }

    /// <summary>
    /// Returns a copy of the full binary with HMAC-SHA256 signature injected into the header.
    /// </summary>
    public static byte[] SignFirmware(byte[] fullBinary, byte[] key)
    {
        if (fullBinary.Length < HeaderSize)
            throw new ArgumentException("Binary too small for signing");

        var signed = (byte[])fullBinary.Clone();

        // Create header copy with fw_hmac zeroed for HMAC input
        var headerForHmac = new byte[HeaderSize];
        Array.Copy(signed, 0, headerForHmac, 0, HeaderSize);
        Array.Clear(headerForHmac, HmacOffset, HmacSize);

        // Compute HMAC-SHA256(key, header_sans_hmac || app_data)
        using var hmac = new HMACSHA256(key);
        hmac.TransformBlock(headerForHmac, 0, HeaderSize, null, 0);
        hmac.TransformFinalBlock(signed, HeaderSize, signed.Length - HeaderSize);

        // Inject HMAC into signed copy
        Array.Copy(hmac.Hash!, 0, signed, HmacOffset, HmacSize);

        return signed;
    }

    private static bool IsAllSame(byte[] data, byte value)
    {
        foreach (var b in data)
            if (b != value) return false;
        return true;
    }

    public static uint ComputeCrc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                if ((crc & 1) != 0)
                    crc = (crc >> 1) ^ 0xEDB88320;
                else
                    crc >>= 1;
            }
        }
        return crc ^ 0xFFFFFFFF;
    }

    /// <summary>
    /// Create a headered firmware binary from a raw application .bin file.
    /// Mirrors test-app/tools/prepend_header.py exactly.
    /// </summary>
    public static byte[] CreateHeader(byte[] rawAppBin, int major, int minor, int patch, byte[]? key = null)
    {
        uint version = (uint)((major << 16) | (minor << 8) | patch);
        uint size = (uint)rawAppBin.Length;
        uint crc = ComputeCrc32(rawAppBin);
        const uint entryPoint = 0x10008100;

        // Build 20-byte fields before fw_hmac
        var header = new byte[HeaderSize];
        BitConverter.TryWriteBytes(header.AsSpan(0), Magic);
        BitConverter.TryWriteBytes(header.AsSpan(4), version);
        BitConverter.TryWriteBytes(header.AsSpan(8), size);
        BitConverter.TryWriteBytes(header.AsSpan(12), crc);
        BitConverter.TryWriteBytes(header.AsSpan(16), entryPoint);

        // Fill fw_hmac and reserved with 0xFF
        for (int i = HmacOffset; i < HeaderSize; i++)
            header[i] = 0xFF;

        // Concatenate header + app data
        var fullBinary = new byte[HeaderSize + rawAppBin.Length];
        Array.Copy(header, 0, fullBinary, 0, HeaderSize);
        Array.Copy(rawAppBin, 0, fullBinary, HeaderSize, rawAppBin.Length);

        // Sign if key provided
        if (key != null)
            fullBinary = SignFirmware(fullBinary, key);

        return fullBinary;
    }
}
