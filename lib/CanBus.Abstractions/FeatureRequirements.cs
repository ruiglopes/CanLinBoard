using System.Collections.Generic;

namespace CanBus;

public static class FeatureRequirements
{
    public const string DebugCanId = "DebugCanId";
    public const string DeviceIdentity = "DeviceIdentity";
    public const string MultiDevice = "MultiDevice";
    public const string TransferResume = "TransferResume";
    public const string Diagnostics = "Diagnostics";

    private static readonly Dictionary<string, (int Major, int Minor, int Patch)> MinVersions = new()
    {
        { DebugCanId, (1, 1, 0) },
        { DeviceIdentity, (1, 2, 1) },
        { MultiDevice, (1, 2, 4) },
        { TransferResume, (1, 3, 3) },
        { Diagnostics, (1, 3, 11) },
    };

    public static bool IsSupported(string feature, BootloaderInfo? info)
    {
        if (info == null)
            return false;

        if (!MinVersions.TryGetValue(feature, out var min))
            return false;

        int device = (info.VersionMajor << 16) | (info.VersionMinor << 8) | info.VersionPatch;
        int required = (min.Major << 16) | (min.Minor << 8) | min.Patch;
        return device >= required;
    }
}
