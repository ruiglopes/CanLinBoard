using System.IO;
using System.Text.Json;

namespace CanLinConfig.Services;

public class FirmwareUpdateSettings
{
    public string? LastKeyFilePath { get; set; }
    public string? LastFirmwarePath { get; set; }
    public uint BootloaderBitrate { get; set; } = 500000;

    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CanLinConfig");
    private static readonly string SettingsPath =
        Path.Combine(SettingsDir, "firmware-update.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static FirmwareUpdateSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<FirmwareUpdateSettings>(json, JsonOptions) ?? new();
            }
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch { }
    }
}
