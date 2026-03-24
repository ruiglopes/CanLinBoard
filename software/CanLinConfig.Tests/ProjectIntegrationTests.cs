// software/CanLinConfig.Tests/ProjectIntegrationTests.cs
using CanLinConfig.Models;
using CanLinConfig.Services;

namespace CanLinConfig.Tests;

public class ProjectIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public ProjectIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"CanLinConfig_Test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void FullLifecycle_create_save_close_reopen()
    {
        var svc = new ProjectService();
        var testDbc = Path.Combine(AppContext.BaseDirectory, "TestData", "test.dbc");
        var savePath = Path.Combine(_tempDir, "lifecycle.clpkg");

        // Create
        var state = new ProjectState
        {
            ProjectName = "Lifecycle Test",
            AdapterType = "PCAN",
            Channel = "PCAN_USBBUS1",
            Bitrate = 500000,
            Can1DbPath = testDbc,
            GraphTimeWindow = 60.0
        };
        var project = svc.CreateFromState(state);

        // Save
        svc.Save(project, savePath);
        Assert.False(svc.HasUnsavedChanges);

        // Close
        svc.CloseProject();
        Assert.Null(svc.CurrentProject);

        // Reopen
        var reopened = svc.Open(savePath);
        Assert.Equal("Lifecycle Test", reopened.Manifest.Name);
        Assert.Equal("PCAN", reopened.Manifest.Connection.AdapterType);
        Assert.Equal(500000u, reopened.Manifest.Connection.Bitrate);
        Assert.Equal(60.0, reopened.Manifest.BusMonitor.GraphTimeWindow);

        // Resolve DBC
        var resolvedState = svc.ToState(reopened);
        Assert.NotNull(resolvedState.Can1DbPath);
        Assert.True(File.Exists(resolvedState.Can1DbPath));

        // Verify DBC content is intact
        var dbManager = new DatabaseManager();
        dbManager.AssignDatabase(BusFrame.Bus.CAN1, resolvedState.Can1DbPath!);
        var signals = dbManager.GetSignals(BusFrame.Bus.CAN1, 256);
        Assert.Equal(3, signals.Count);
    }

    [Fact]
    public void Clpkg_is_valid_zip()
    {
        var svc = new ProjectService();
        var savePath = Path.Combine(_tempDir, "zip_test.clpkg");
        svc.Save(svc.CreateFromState(new ProjectState { ProjectName = "ZIP Test" }), savePath);

        using var zip = System.IO.Compression.ZipFile.OpenRead(savePath);
        Assert.Contains(zip.Entries, e => e.FullName == "project.json");
    }

    [Fact]
    public void AppSettings_tracks_recent_projects()
    {
        var settingsDir = Path.Combine(_tempDir, "settings");
        var settings = new AppSettings { LoadLastProject = true };
        settings.AddRecentProject(@"C:\projects\a.clpkg");
        settings.AddRecentProject(@"C:\projects\b.clpkg");
        settings.Save(settingsDir);

        var loaded = AppSettings.Load(settingsDir);
        Assert.True(loaded.LoadLastProject);
        Assert.Equal(2, loaded.RecentProjects.Count);
        Assert.Equal(@"C:\projects\b.clpkg", loaded.RecentProjects[0]);
    }
}
