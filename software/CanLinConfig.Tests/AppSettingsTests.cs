// software/CanLinConfig.Tests/AppSettingsTests.cs
using CanLinConfig.Services;

namespace CanLinConfig.Tests;

public class AppSettingsTests
{
    private string _tempDir = null!;

    private string SetupTempDir()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"CanLinConfig_Test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        return _tempDir;
    }

    private void Cleanup()
    {
        if (_tempDir != null && Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void Load_returns_defaults_when_file_missing()
    {
        var dir = SetupTempDir();
        try
        {
            var settings = AppSettings.Load(dir);
            Assert.False(settings.LoadLastProject);
            Assert.Null(settings.LastProjectPath);
            Assert.Empty(settings.RecentProjects);
        }
        finally { Cleanup(); }
    }

    [Fact]
    public void Save_then_Load_round_trips()
    {
        var dir = SetupTempDir();
        try
        {
            var settings = new AppSettings
            {
                LoadLastProject = true,
                LastProjectPath = @"C:\test\my.clpkg"
            };
            settings.AddRecentProject(@"C:\test\my.clpkg");
            settings.Save(dir);

            var loaded = AppSettings.Load(dir);
            Assert.True(loaded.LoadLastProject);
            Assert.Equal(@"C:\test\my.clpkg", loaded.LastProjectPath);
            Assert.Single(loaded.RecentProjects);
        }
        finally { Cleanup(); }
    }

    [Fact]
    public void RecentProjects_caps_at_10_and_deduplicates()
    {
        var settings = new AppSettings();
        for (int i = 0; i < 15; i++)
            settings.AddRecentProject($@"C:\test\project{i}.clpkg");

        Assert.Equal(10, settings.RecentProjects.Count);
        Assert.Equal(@"C:\test\project14.clpkg", settings.RecentProjects[0]);

        settings.AddRecentProject(@"C:\test\project10.clpkg");
        Assert.Equal(@"C:\test\project10.clpkg", settings.RecentProjects[0]);
        Assert.Equal(10, settings.RecentProjects.Count);
    }
}
