using CanBus;
using CanBus.Adapters;

namespace CanLinConfig.Services;

/// <summary>
/// Maps config tool adapter type names to bootloader protocol lib adapter instances.
/// </summary>
public static class AdapterFactory
{
    private static readonly uint[] StandardBitrates = [500000, 250000, 1000000, 125000];

    public static ICanAdapter Create(string adapterType) => adapterType switch
    {
        "PCAN"      => new PcanService(),
        "Vector XL" => new VectorService(),
        "Kvaser"    => new KvaserService(),
        "SLCAN"     => new SlcanService(),
        _           => throw new ArgumentException($"Unknown adapter type: {adapterType}")
    };

    /// <summary>
    /// Find the channel index in the lib adapter matching the config tool's channel name.
    /// </summary>
    public static int FindChannelIndex(ICanAdapter adapter, string channelName)
    {
        var names = adapter.ChannelNames;

        // Exact match (case-insensitive)
        for (int i = 0; i < names.Length; i++)
        {
            if (names[i].Equals(channelName, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        // Substring fallback (Vector XL channel name formats may differ)
        for (int i = 0; i < names.Length; i++)
        {
            if (names[i].Contains(channelName, StringComparison.OrdinalIgnoreCase) ||
                channelName.Contains(names[i], StringComparison.OrdinalIgnoreCase))
                return i;
        }

        throw new ArgumentException($"Channel '{channelName}' not found in {adapter.AdapterName}");
    }

    /// <summary>
    /// Find the bitrate index in the lib adapter matching the target bitrate value.
    /// </summary>
    public static int FindBitrateIndex(ICanAdapter adapter, uint bitrate)
    {
        var names = adapter.BitrateNames;
        string target = bitrate.ToString();

        for (int i = 0; i < names.Length; i++)
        {
            // BitrateNames are like "500 kbit/s" — extract numeric part
            var numeric = new string(names[i].Where(c => char.IsDigit(c)).ToArray());
            // Compare as kbps (names use kbit/s) and as raw value
            if (numeric == (bitrate / 1000).ToString() || numeric == target)
                return i;
        }

        throw new ArgumentException($"Bitrate {bitrate} not found in {adapter.AdapterName}");
    }

    /// <summary>
    /// Returns standard bitrates to try during auto-scan, excluding the one already tried.
    /// </summary>
    public static uint[] GetScanBitrates(uint excludeBitrate)
    {
        return StandardBitrates.Where(b => b != excludeBitrate).ToArray();
    }
}
