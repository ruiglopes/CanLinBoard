// software/CanLinConfig.Tests/ProjectServiceTests.cs
using CanLinConfig.Models;
using CanLinConfig.Services;

namespace CanLinConfig.Tests;

public class ProjectServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _testDbcPath;

    public ProjectServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"CanLinConfig_Test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _testDbcPath = Path.Combine(AppContext.BaseDirectory, "TestData", "test.dbc");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void CreateFromState_captures_connection_and_databases()
    {
        var svc = new ProjectService();
        var state = new ProjectState
        {
            ProjectName = "Test Project",
            AdapterType = "PCAN",
            Channel = "PCAN_USBBUS1",
            Bitrate = 500000,
            Can1DbPath = _testDbcPath,
            Can2DbPath = null,
            GraphTimeWindow = 60.0
        };

        var project = svc.CreateFromState(state);

        Assert.Equal("Test Project", project.Manifest.Name);
        Assert.Equal("PCAN", project.Manifest.Connection.AdapterType);
        Assert.Equal(500000u, project.Manifest.Connection.Bitrate);
        Assert.NotNull(project.Manifest.Databases.Can1);
        Assert.Null(project.Manifest.Databases.Can2);
        Assert.Equal(60.0, project.Manifest.BusMonitor.GraphTimeWindow);
    }

    [Fact]
    public void SaveAndOpen_round_trips_manifest()
    {
        var svc = new ProjectService();
        var state = new ProjectState
        {
            AdapterType = "PCAN",
            Bitrate = 250000,
            Can1DbPath = _testDbcPath,
            ProjectName = "Round Trip Test"
        };
        var project = svc.CreateFromState(state);
        var savePath = Path.Combine(_tempDir, "test.clpkg");

        svc.Save(project, savePath);
        Assert.True(File.Exists(savePath));

        var loaded = svc.Open(savePath);
        Assert.Equal("Round Trip Test", loaded.Manifest.Name);
        Assert.Equal("PCAN", loaded.Manifest.Connection.AdapterType);
        Assert.Equal(250000u, loaded.Manifest.Connection.Bitrate);
        Assert.Equal(savePath, loaded.FilePath);
    }

    [Fact]
    public void SaveAndOpen_embeds_dbc_file()
    {
        var svc = new ProjectService();
        var state = new ProjectState
        {
            Can1DbPath = _testDbcPath,
            ProjectName = "DBC Test"
        };
        var project = svc.CreateFromState(state);
        var savePath = Path.Combine(_tempDir, "dbc_test.clpkg");

        svc.Save(project, savePath);

        var loaded = svc.Open(savePath);
        Assert.NotNull(loaded.Manifest.Databases.Can1);
        var extractedPath = svc.GetExtractedDbPath(loaded, loaded.Manifest.Databases.Can1!);
        Assert.True(File.Exists(extractedPath));
    }

    [Fact]
    public void Save_updates_modified_timestamp()
    {
        var svc = new ProjectService();
        var project = svc.CreateFromState(new ProjectState { ProjectName = "Timestamp Test" });
        var before = project.Manifest.Modified;

        System.Threading.Thread.Sleep(50);
        var savePath = Path.Combine(_tempDir, "ts_test.clpkg");
        svc.Save(project, savePath);

        Assert.True(project.Manifest.Modified > before);
    }

    [Fact]
    public void Open_nonexistent_file_throws()
    {
        var svc = new ProjectService();
        Assert.Throws<FileNotFoundException>(() => svc.Open(@"C:\nonexistent\fake.clpkg"));
    }
}
