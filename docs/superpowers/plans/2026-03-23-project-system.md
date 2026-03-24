# Project System — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `.clpkg` project files (ZIP bundles) that capture the entire config tool state — databases, connection settings, bus monitor config, logger presets — so users can save, share, and restore complete working environments.

**Architecture:** A `ProjectService` manages the project lifecycle (open/save/create/apply). Projects are ZIP archives containing a `project.json` manifest and embedded copies of DBC/LDF files. An `AppSettings` service persists user preferences (auto-load last project) to `%AppData%`. MainViewModel delegates project operations to ProjectService and exposes them via File menu commands.

**Tech Stack:** .NET 8, WPF, System.IO.Compression (ZipFile), System.Text.Json

**Spec:** `docs/superpowers/specs/2026-03-23-bus-monitor-logger-design.md` (Section 9)

**Important:** Do NOT commit specs or plans. Only commit actual code changes, and only after the feature is completed and tested.

---

## File Map

### New Files

| File | Responsibility |
|------|---------------|
| `software/CanLinConfig/Models/Project.cs` | Project data model + JSON manifest schema |
| `software/CanLinConfig/Services/ProjectService.cs` | Open/save/create/apply .clpkg ZIP bundles |
| `software/CanLinConfig/Services/AppSettings.cs` | App-level preferences (loadLastProject, lastProjectPath, recent projects) |
| `software/CanLinConfig.Tests/ProjectServiceTests.cs` | Project save/load round-trip tests |
| `software/CanLinConfig.Tests/AppSettingsTests.cs` | Settings persistence tests |

### Modified Files

| File | Changes |
|------|---------|
| `software/CanLinConfig/ViewModels/MainViewModel.cs` | Add ProjectService, project commands (New/Open/Save/SaveAs/Close), unsaved changes tracking, auto-load on startup |
| `software/CanLinConfig/Views/MainWindow.xaml` | Add File menu bar (New/Open/Save/Save As/Close/Recent) |
| `software/CanLinConfig/Views/MainWindow.xaml.cs` | Wire Closing event to save prompt |

---

## Task 1: AppSettings Service (TDD)

Application-level preferences persisted to `%AppData%/CanLinConfig/settings.json`. Follows the existing `FirmwareUpdateSettings` pattern.

**Files:**
- Create: `software/CanLinConfig/Services/AppSettings.cs`
- Create: `software/CanLinConfig.Tests/AppSettingsTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
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

    public void Cleanup()
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
        Assert.Equal(@"C:\test\project14.clpkg", settings.RecentProjects[0]); // most recent first

        // Adding existing path moves it to front
        settings.AddRecentProject(@"C:\test\project10.clpkg");
        Assert.Equal(@"C:\test\project10.clpkg", settings.RecentProjects[0]);
        Assert.Equal(10, settings.RecentProjects.Count);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
cd software && dotnet test CanLinConfig.Tests --filter "AppSettingsTests" -v n
```

- [ ] **Step 3: Implement AppSettings**

```csharp
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
        catch
        {
            return new AppSettings();
        }
    }

    private static string GetDefaultDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CanLinConfig");
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
cd software && dotnet test CanLinConfig.Tests --filter "AppSettingsTests" -v n
```

Expected: 3 tests PASS.

- [ ] **Step 5: Commit**

```
feat: add AppSettings service for app-level preferences
```

---

## Task 2: Project Model

Data model for the project manifest (project.json) and the in-memory Project object.

**Files:**
- Create: `software/CanLinConfig/Models/Project.cs`

- [ ] **Step 1: Create Project model**

```csharp
// software/CanLinConfig/Models/Project.cs
using System.Text.Json.Serialization;

namespace CanLinConfig.Models;

/// <summary>
/// In-memory representation of a .clpkg project.
/// </summary>
public class Project
{
    /// <summary>Path to the .clpkg file on disk. Null if never saved.</summary>
    public string? FilePath { get; set; }

    /// <summary>Deserialized manifest.</summary>
    public ProjectManifest Manifest { get; set; } = new();

    /// <summary>Temp directory where ZIP contents are extracted for the active session.</summary>
    public string? ExtractedDir { get; set; }
}

/// <summary>
/// Serialized as project.json inside the .clpkg ZIP.
/// </summary>
public class ProjectManifest
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("name")]
    public string Name { get; set; } = "Untitled";

    [JsonPropertyName("created")]
    public DateTime Created { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("modified")]
    public DateTime Modified { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("connection")]
    public ProjectConnection Connection { get; set; } = new();

    [JsonPropertyName("databases")]
    public ProjectDatabases Databases { get; set; } = new();

    [JsonPropertyName("bus_monitor")]
    public ProjectBusMonitor BusMonitor { get; set; } = new();
}

public class ProjectConnection
{
    [JsonPropertyName("adapter_type")]
    public string? AdapterType { get; set; }

    [JsonPropertyName("channel")]
    public string? Channel { get; set; }

    [JsonPropertyName("bitrate")]
    public uint Bitrate { get; set; } = 500000;
}

public class ProjectDatabases
{
    [JsonPropertyName("can1")]
    public string? Can1 { get; set; }

    [JsonPropertyName("can2")]
    public string? Can2 { get; set; }

    [JsonPropertyName("lin1")]
    public string? Lin1 { get; set; }

    [JsonPropertyName("lin2")]
    public string? Lin2 { get; set; }

    [JsonPropertyName("lin3")]
    public string? Lin3 { get; set; }

    [JsonPropertyName("lin4")]
    public string? Lin4 { get; set; }
}

public class ProjectBusMonitor
{
    [JsonPropertyName("graph_time_window")]
    public double GraphTimeWindow { get; set; } = 30.0;
}
```

- [ ] **Step 2: Verify build**

```bash
cd software && dotnet build CanLinConfig/CanLinConfig.csproj
```

- [ ] **Step 3: Commit**

```
feat: add Project model and manifest schema
```

---

## Task 3: ProjectService (TDD)

Core service that creates, opens, saves, and applies .clpkg projects.

**Files:**
- Create: `software/CanLinConfig/Services/ProjectService.cs`
- Create: `software/CanLinConfig.Tests/ProjectServiceTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
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
            AdapterType = "PCAN",
            Channel = "PCAN_USBBUS1",
            Bitrate = 500000,
            Can1DbPath = _testDbcPath,
            Can2DbPath = null,
            GraphTimeWindow = 60.0,
            ProjectName = "Test Project"
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
        // The DBC path should point to an extracted temp file
        var extractedPath = svc.GetExtractedDbPath(loaded, loaded.Manifest.Databases.Can1!);
        Assert.True(File.Exists(extractedPath));
    }

    [Fact]
    public void Save_updates_modified_timestamp()
    {
        var svc = new ProjectService();
        var project = svc.CreateFromState(new ProjectState { ProjectName = "Timestamp Test" });
        var before = project.Manifest.Modified;

        System.Threading.Thread.Sleep(50); // ensure time passes
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
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
cd software && dotnet test CanLinConfig.Tests --filter "ProjectServiceTests" -v n
```

- [ ] **Step 3: Implement ProjectService and ProjectState**

```csharp
// software/CanLinConfig/Services/ProjectService.cs
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using CanLinConfig.Models;

namespace CanLinConfig.Services;

/// <summary>
/// Snapshot of current tool state, used to create/apply projects.
/// </summary>
public class ProjectState
{
    public string ProjectName { get; set; } = "Untitled";
    public string? AdapterType { get; set; }
    public string? Channel { get; set; }
    public uint Bitrate { get; set; } = 500000;
    public string? Can1DbPath { get; set; }
    public string? Can2DbPath { get; set; }
    public string? Lin1DbPath { get; set; }
    public string? Lin2DbPath { get; set; }
    public string? Lin3DbPath { get; set; }
    public string? Lin4DbPath { get; set; }
    public double GraphTimeWindow { get; set; } = 30.0;
}

public class ProjectService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private string? _extractDir;

    public Project? CurrentProject { get; private set; }
    public bool HasUnsavedChanges { get; set; }

    public event EventHandler? ProjectChanged;

    public Project CreateFromState(ProjectState state)
    {
        var project = new Project
        {
            Manifest = new ProjectManifest
            {
                Name = state.ProjectName,
                Created = DateTime.UtcNow,
                Modified = DateTime.UtcNow,
                Connection = new ProjectConnection
                {
                    AdapterType = state.AdapterType,
                    Channel = state.Channel,
                    Bitrate = state.Bitrate
                },
                Databases = new ProjectDatabases
                {
                    Can1 = state.Can1DbPath != null ? "databases/" + Path.GetFileName(state.Can1DbPath) : null,
                    Can2 = state.Can2DbPath != null ? "databases/" + Path.GetFileName(state.Can2DbPath) : null,
                    Lin1 = state.Lin1DbPath != null ? "databases/" + Path.GetFileName(state.Lin1DbPath) : null,
                    Lin2 = state.Lin2DbPath != null ? "databases/" + Path.GetFileName(state.Lin2DbPath) : null,
                    Lin3 = state.Lin3DbPath != null ? "databases/" + Path.GetFileName(state.Lin3DbPath) : null,
                    Lin4 = state.Lin4DbPath != null ? "databases/" + Path.GetFileName(state.Lin4DbPath) : null,
                },
                BusMonitor = new ProjectBusMonitor
                {
                    GraphTimeWindow = state.GraphTimeWindow
                }
            }
        };

        // Store original paths for embedding during save
        project.ExtractedDir = null;
        _dbSourcePaths.Clear();
        AddDbSource(state.Can1DbPath);
        AddDbSource(state.Can2DbPath);
        AddDbSource(state.Lin1DbPath);
        AddDbSource(state.Lin2DbPath);
        AddDbSource(state.Lin3DbPath);
        AddDbSource(state.Lin4DbPath);

        return project;
    }

    // Maps "databases/filename.dbc" → original source path for embedding
    private readonly Dictionary<string, string> _dbSourcePaths = new();

    private void AddDbSource(string? path)
    {
        if (path == null || !File.Exists(path)) return;
        var key = "databases/" + Path.GetFileName(path);
        _dbSourcePaths[key] = path;
    }

    public void Save(Project project, string filePath)
    {
        project.Manifest.Modified = DateTime.UtcNow;
        project.FilePath = filePath;

        // Delete existing file (ZipFile.Open doesn't overwrite cleanly)
        if (File.Exists(filePath))
            File.Delete(filePath);

        using var zip = ZipFile.Open(filePath, ZipArchiveMode.Create);

        // Write manifest
        var manifestEntry = zip.CreateEntry("project.json");
        using (var stream = manifestEntry.Open())
        {
            JsonSerializer.Serialize(stream, project.Manifest, JsonOpts);
        }

        // Embed database files
        EmbedDatabases(zip, project);

        HasUnsavedChanges = false;
        CurrentProject = project;
        ProjectChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EmbedDatabases(ZipArchive zip, Project project)
    {
        var dbPaths = new[]
        {
            project.Manifest.Databases.Can1,
            project.Manifest.Databases.Can2,
            project.Manifest.Databases.Lin1,
            project.Manifest.Databases.Lin2,
            project.Manifest.Databases.Lin3,
            project.Manifest.Databases.Lin4
        };

        var embedded = new HashSet<string>();
        foreach (var dbRelPath in dbPaths)
        {
            if (dbRelPath == null || !embedded.Add(dbRelPath)) continue;

            string? sourcePath = null;

            // Try source paths first (from CreateFromState)
            if (_dbSourcePaths.TryGetValue(dbRelPath, out var src))
                sourcePath = src;
            // Then try extracted dir (from Open)
            else if (project.ExtractedDir != null)
                sourcePath = Path.Combine(project.ExtractedDir, dbRelPath);

            if (sourcePath != null && File.Exists(sourcePath))
                zip.CreateEntryFromFile(sourcePath, dbRelPath);
        }
    }

    public Project Open(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Project file not found", filePath);

        // Extract to temp dir
        CleanupExtractDir();
        _extractDir = Path.Combine(Path.GetTempPath(), "CanLinConfig", "projects", Guid.NewGuid().ToString("N"));
        ZipFile.ExtractToDirectory(filePath, _extractDir);

        // Read manifest
        var manifestPath = Path.Combine(_extractDir, "project.json");
        var json = File.ReadAllText(manifestPath);
        var manifest = JsonSerializer.Deserialize<ProjectManifest>(json)
            ?? throw new InvalidDataException("Invalid project.json");

        var project = new Project
        {
            FilePath = filePath,
            Manifest = manifest,
            ExtractedDir = _extractDir
        };

        _dbSourcePaths.Clear();
        HasUnsavedChanges = false;
        CurrentProject = project;
        ProjectChanged?.Invoke(this, EventArgs.Empty);

        return project;
    }

    /// <summary>
    /// Resolve a manifest-relative database path to an absolute file path.
    /// </summary>
    public string? GetExtractedDbPath(Project project, string manifestRelativePath)
    {
        if (project.ExtractedDir == null) return null;
        var path = Path.Combine(project.ExtractedDir, manifestRelativePath);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Build a ProjectState from the current project manifest.
    /// Database paths are resolved to extracted temp files.
    /// </summary>
    public ProjectState ToState(Project project)
    {
        return new ProjectState
        {
            ProjectName = project.Manifest.Name,
            AdapterType = project.Manifest.Connection.AdapterType,
            Channel = project.Manifest.Connection.Channel,
            Bitrate = project.Manifest.Connection.Bitrate,
            Can1DbPath = ResolveDbPath(project, project.Manifest.Databases.Can1),
            Can2DbPath = ResolveDbPath(project, project.Manifest.Databases.Can2),
            Lin1DbPath = ResolveDbPath(project, project.Manifest.Databases.Lin1),
            Lin2DbPath = ResolveDbPath(project, project.Manifest.Databases.Lin2),
            Lin3DbPath = ResolveDbPath(project, project.Manifest.Databases.Lin3),
            Lin4DbPath = ResolveDbPath(project, project.Manifest.Databases.Lin4),
            GraphTimeWindow = project.Manifest.BusMonitor.GraphTimeWindow
        };
    }

    private string? ResolveDbPath(Project project, string? manifestPath)
    {
        if (manifestPath == null) return null;
        return GetExtractedDbPath(project, manifestPath);
    }

    public void CloseProject()
    {
        CleanupExtractDir();
        CurrentProject = null;
        HasUnsavedChanges = false;
        _dbSourcePaths.Clear();
        ProjectChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CleanupExtractDir()
    {
        if (_extractDir != null && Directory.Exists(_extractDir))
        {
            try { Directory.Delete(_extractDir, true); }
            catch { /* best effort cleanup */ }
            _extractDir = null;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
cd software && dotnet test CanLinConfig.Tests --filter "ProjectServiceTests" -v n
```

Expected: 5 tests PASS.

- [ ] **Step 5: Commit**

```
feat: add ProjectService with .clpkg ZIP packaging
```

---

## Task 4: Wire into MainViewModel

Add project commands and auto-load support.

**Files:**
- Modify: `software/CanLinConfig/ViewModels/MainViewModel.cs`

- [ ] **Step 1: Read MainViewModel.cs and add project support**

Add these fields/properties to MainViewModel:

```csharp
private readonly ProjectService _projectService = new();
private readonly AppSettings _appSettings;

[ObservableProperty] private string _windowTitle = "CanLinConfig";
```

In the constructor, load settings and auto-load last project:

```csharp
_appSettings = AppSettings.Load();
if (_appSettings.LoadLastProject && _appSettings.LastProjectPath != null
    && File.Exists(_appSettings.LastProjectPath))
{
    try { OpenProject(_appSettings.LastProjectPath); }
    catch { /* ignore — file may have been deleted */ }
}
```

Add project commands:

```csharp
[RelayCommand]
private void NewProject()
{
    if (!ConfirmUnsavedChanges()) return;
    var state = CaptureCurrentState();
    state.ProjectName = "New Project";
    var project = _projectService.CreateFromState(state);
    _projectService.CurrentProject = project; // not yet saved
    _projectService.HasUnsavedChanges = true;
    UpdateWindowTitle();
    StatusBarText = "New project created";
}

[RelayCommand]
private void OpenProject()
{
    if (!ConfirmUnsavedChanges()) return;
    var dlg = new Microsoft.Win32.OpenFileDialog
    {
        Filter = "CanLinConfig Projects (*.clpkg)|*.clpkg|All Files (*.*)|*.*",
        Title = "Open Project"
    };
    if (dlg.ShowDialog() != true) return;
    OpenProject(dlg.FileName);
}

private void OpenProject(string path)
{
    try
    {
        var project = _projectService.Open(path);
        var state = _projectService.ToState(project);
        ApplyState(state);
        _appSettings.LastProjectPath = path;
        _appSettings.AddRecentProject(path);
        _appSettings.Save();
        UpdateWindowTitle();
        StatusBarText = $"Opened project: {project.Manifest.Name}";
    }
    catch (Exception ex)
    {
        StatusBarText = $"Failed to open project: {ex.Message}";
    }
}

[RelayCommand]
private void SaveProject()
{
    var project = _projectService.CurrentProject;
    if (project?.FilePath == null)
    {
        SaveProjectAs();
        return;
    }
    var state = CaptureCurrentState();
    project.Manifest.Name = state.ProjectName;
    var freshProject = _projectService.CreateFromState(state);
    freshProject.FilePath = project.FilePath;
    freshProject.Manifest.Created = project.Manifest.Created;
    _projectService.Save(freshProject, project.FilePath);
    _appSettings.AddRecentProject(project.FilePath);
    _appSettings.Save();
    UpdateWindowTitle();
    StatusBarText = "Project saved";
}

[RelayCommand]
private void SaveProjectAs()
{
    var dlg = new Microsoft.Win32.SaveFileDialog
    {
        Filter = "CanLinConfig Projects (*.clpkg)|*.clpkg",
        Title = "Save Project As",
        FileName = _projectService.CurrentProject?.Manifest.Name ?? "project"
    };
    if (dlg.ShowDialog() != true) return;
    var state = CaptureCurrentState();
    var project = _projectService.CreateFromState(state);
    _projectService.Save(project, dlg.FileName);
    _appSettings.LastProjectPath = dlg.FileName;
    _appSettings.AddRecentProject(dlg.FileName);
    _appSettings.Save();
    UpdateWindowTitle();
    StatusBarText = $"Project saved as {Path.GetFileName(dlg.FileName)}";
}

[RelayCommand]
private void CloseProject()
{
    if (!ConfirmUnsavedChanges()) return;
    _projectService.CloseProject();
    BusMonitor.ClearCan1DbCommand.Execute(null);
    BusMonitor.ClearCan2DbCommand.Execute(null);
    UpdateWindowTitle();
    StatusBarText = "Project closed";
}

private ProjectState CaptureCurrentState()
{
    return new ProjectState
    {
        ProjectName = _projectService.CurrentProject?.Manifest.Name ?? "Untitled",
        AdapterType = SelectedAdapter,
        Channel = SelectedChannel,
        Bitrate = uint.TryParse(SelectedBitrate?.ToString(), out var br) ? br : 500000,
        Can1DbPath = BusDataService.DatabaseManager.GetDatabasePath(BusFrame.Bus.CAN1),
        Can2DbPath = BusDataService.DatabaseManager.GetDatabasePath(BusFrame.Bus.CAN2),
        GraphTimeWindow = BusMonitor.Graph.TimeWindowSeconds
    };
}

private void ApplyState(ProjectState state)
{
    // Apply connection settings
    if (state.AdapterType != null)
        SelectedAdapter = state.AdapterType;
    if (state.Channel != null)
        SelectedChannel = state.Channel;

    // Apply databases
    if (state.Can1DbPath != null && File.Exists(state.Can1DbPath))
    {
        BusDataService.DatabaseManager.AssignDatabase(BusFrame.Bus.CAN1, state.Can1DbPath);
        BusMonitor.Can1DbPath = Path.GetFileName(state.Can1DbPath);
    }
    if (state.Can2DbPath != null && File.Exists(state.Can2DbPath))
    {
        BusDataService.DatabaseManager.AssignDatabase(BusFrame.Bus.CAN2, state.Can2DbPath);
        BusMonitor.Can2DbPath = Path.GetFileName(state.Can2DbPath);
    }

    // Apply graph settings
    BusMonitor.Graph.TimeWindowSeconds = state.GraphTimeWindow;
}

private void UpdateWindowTitle()
{
    var project = _projectService.CurrentProject;
    if (project == null)
        WindowTitle = "CanLinConfig";
    else
    {
        var dirty = _projectService.HasUnsavedChanges ? " *" : "";
        WindowTitle = $"CanLinConfig — {project.Manifest.Name}{dirty}";
    }
}

private bool ConfirmUnsavedChanges()
{
    if (!_projectService.HasUnsavedChanges) return true;
    var result = System.Windows.MessageBox.Show(
        "Save changes to the current project?",
        "Unsaved Changes",
        System.Windows.MessageBoxButton.YesNoCancel);
    if (result == System.Windows.MessageBoxResult.Cancel) return false;
    if (result == System.Windows.MessageBoxResult.Yes) SaveProject();
    return true;
}
```

- [ ] **Step 2: Verify build**

```bash
cd software && dotnet build CanLinConfig/CanLinConfig.csproj
```

- [ ] **Step 3: Commit**

```
feat: wire ProjectService into MainViewModel with project commands
```

---

## Task 5: File Menu in MainWindow

Add a menu bar with File > New / Open / Save / Save As / Close / Recent Projects.

**Files:**
- Modify: `software/CanLinConfig/Views/MainWindow.xaml`
- Modify: `software/CanLinConfig/Views/MainWindow.xaml.cs`

- [ ] **Step 1: Add menu bar to MainWindow.xaml**

Read `MainWindow.xaml`. Add a Menu as the first child of the main Grid, before the Connection Bar. Add a new row at the top of the Grid for the menu:

```xml
<!-- Add as first RowDefinition -->
<RowDefinition Height="Auto" />  <!-- Menu bar -->
```

Shift existing rows down (Row="0" becomes Row="1", etc.). Add the menu:

```xml
<!-- Menu Bar (Grid.Row="0") -->
<Menu Grid.Row="0">
    <MenuItem Header="_File">
        <MenuItem Header="_New Project" Command="{Binding NewProjectCommand}" InputGestureText="Ctrl+N"/>
        <MenuItem Header="_Open Project..." Command="{Binding OpenProjectCommand}" InputGestureText="Ctrl+O"/>
        <Separator/>
        <MenuItem Header="_Save Project" Command="{Binding SaveProjectCommand}" InputGestureText="Ctrl+S"/>
        <MenuItem Header="Save Project _As..." Command="{Binding SaveProjectAsCommand}"/>
        <Separator/>
        <MenuItem Header="_Close Project" Command="{Binding CloseProjectCommand}"/>
    </MenuItem>
</Menu>
```

Also bind the window Title:

```xml
Title="{Binding WindowTitle, FallbackValue='CanLinConfig'}"
```

- [ ] **Step 2: Add keyboard shortcuts in MainWindow.xaml.cs**

Read `MainWindow.xaml.cs`. Add input bindings in the constructor or XAML:

```xml
<mah:MetroWindow.InputBindings>
    <KeyBinding Gesture="Ctrl+N" Command="{Binding NewProjectCommand}"/>
    <KeyBinding Gesture="Ctrl+O" Command="{Binding OpenProjectCommand}"/>
    <KeyBinding Gesture="Ctrl+S" Command="{Binding SaveProjectCommand}"/>
</mah:MetroWindow.InputBindings>
```

- [ ] **Step 3: Wire Closing event for unsaved changes prompt**

In `MainWindow.xaml.cs`, the existing `OnClosing` handler should call the VM's unsaved changes check. Read the file to see what's already there, then add:

```csharp
private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
{
    if (DataContext is MainViewModel vm)
    {
        // existing cleanup code...
        // Add: check unsaved changes (ConfirmUnsavedChanges is private,
        // so expose a public CanClose() method or handle via event)
    }
}
```

If `ConfirmUnsavedChanges()` is private in MainViewModel, add a public `bool CanClose()` method that delegates to it, and call it from OnClosing.

- [ ] **Step 4: Verify build and all tests pass**

```bash
cd software && dotnet build CanLinConfig.sln && dotnet test CanLinConfig.Tests -v quiet
```

- [ ] **Step 5: Commit**

```
feat: add File menu with project commands and keyboard shortcuts
```

---

## Task 6: Integration Test — Project Round-Trip

Verify the full project lifecycle works end-to-end.

**Files:**
- Create: `software/CanLinConfig.Tests/ProjectIntegrationTests.cs`

- [ ] **Step 1: Write integration test**

```csharp
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

        // Verify it's a valid ZIP
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
```

- [ ] **Step 2: Run all tests**

```bash
cd software && dotnet test CanLinConfig.Tests -v n
```

Expected: All existing tests + 3 new integration tests + 3 AppSettings tests + 5 ProjectService tests pass.

- [ ] **Step 3: Commit**

```
feat: add project system integration tests
```

---

## Summary

| Task | Component | Tests | Depends On |
|------|-----------|-------|------------|
| 1 | AppSettings service | 3 | — |
| 2 | Project model | 0 | — |
| 3 | ProjectService | 5 | 1, 2 |
| 4 | MainViewModel wiring | 0 | 3 |
| 5 | File menu UI | 0 | 4 |
| 6 | Integration tests | 3 | 3 |

**Total: 6 tasks, 11 new tests**
