// software/CanLinConfig/Services/AppSettings.cs
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CanLinConfig.Services;

public class AppSettings
{
    private const string FileName = "settings.json";
    private const int MaxRecentProjects = 10;

    [JsonPropertyName("load_last_project")]
    public bool LoadLastProject { get; set; }

    [JsonPropertyName("last_project_path")]
    public string? LastProjectPath { get; set; }

    [JsonPropertyName("recent_projects")]
    public List<string> RecentProjects { get; set; } = [];

    public void AddRecentProject(string path)
    {
        RecentProjects.Remove(path);
        RecentProjects.Insert(0, path);
        while (RecentProjects.Count > MaxRecentProjects)
            RecentProjects.RemoveAt(RecentProjects.Count - 1);
    }

    public void Save(string? appDataDir = null)
    {
        var dir = appDataDir ?? GetDefaultDir();
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(dir, FileName), json);
    }

    public static AppSettings Load(string? appDataDir = null)
    {
        var dir = appDataDir ?? GetDefaultDir();
        var path = Path.Combine(dir, FileName);
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch { return new AppSettings(); }
    }

    private static string GetDefaultDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CanLinConfig");
}
