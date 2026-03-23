using System.IO;
using System.IO.Compression;
using System.Text.Json;
using CanLinConfig.Models;

namespace CanLinConfig.Services;

/// <summary>
/// Creates, saves, opens, and applies .clpkg project files (ZIP archives).
/// </summary>
public class ProjectService
{
    private const string ManifestEntry = "project.json";
    private const string DbPrefix = "databases/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // Tracks source paths for databases so Save() can embed them.
    // Key: manifest-relative path (e.g. "databases/can1.dbc"), Value: absolute source path.
    private readonly Dictionary<string, string> _sourcePaths = new();

    // Temp directories created during Open(), cleaned up by CloseProject().
    private readonly List<string> _extractedDirs = new();

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------

    /// <summary>
    /// The currently open project, or null if no project is open.
    /// </summary>
    public Project? CurrentProject { get; private set; }

    /// <summary>
    /// True if the project has been modified since the last Save().
    /// </summary>
    public bool HasUnsavedChanges { get; private set; }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds a new Project from tool state. Database source paths are recorded
    /// so they will be embedded when Save() is called.
    /// </summary>
    public Project CreateFromState(ProjectState state)
    {
        var manifest = new ProjectManifest
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
            BusMonitor = new ProjectBusMonitor
            {
                GraphTimeWindow = state.GraphTimeWindow
            },
            Databases = new ProjectDatabases()
        };

        manifest.Databases.Can1 = RegisterDb(state.Can1DbPath, "can1");
        manifest.Databases.Can2 = RegisterDb(state.Can2DbPath, "can2");
        manifest.Databases.Lin1 = RegisterDb(state.Lin1DbPath, "lin1");
        manifest.Databases.Lin2 = RegisterDb(state.Lin2DbPath, "lin2");
        manifest.Databases.Lin3 = RegisterDb(state.Lin3DbPath, "lin3");
        manifest.Databases.Lin4 = RegisterDb(state.Lin4DbPath, "lin4");

        CurrentProject = new Project { Manifest = manifest };
        HasUnsavedChanges = true;
        return CurrentProject;
    }

    /// <summary>
    /// Saves the project as a .clpkg ZIP file. Updates the Modified timestamp and FilePath.
    /// Embeds any registered database source files.
    /// </summary>
    public void Save(Project project, string filePath)
    {
        project.Manifest.Modified = DateTime.UtcNow;

        using var archive = ZipFile.Open(filePath, ZipArchiveMode.Create);

        // Write manifest
        var manifestEntry = archive.CreateEntry(ManifestEntry);
        using (var stream = manifestEntry.Open())
        {
            var json = JsonSerializer.Serialize(project.Manifest, JsonOptions);
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            stream.Write(bytes, 0, bytes.Length);
        }

        // Embed database files from registered source paths
        foreach (var (entryName, sourcePath) in _sourcePaths)
        {
            if (File.Exists(sourcePath))
            {
                archive.CreateEntryFromFile(sourcePath, entryName);
            }
        }

        project.FilePath = filePath;
        CurrentProject = project;
        HasUnsavedChanges = false;
    }

    /// <summary>
    /// Opens a .clpkg file. Extracts to a temp directory and deserializes the manifest.
    /// </summary>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist.</exception>
    public Project Open(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Project file not found: {filePath}", filePath);

        var extractDir = Path.Combine(Path.GetTempPath(), $"CanLinConfig_{Guid.NewGuid():N}");
        Directory.CreateDirectory(extractDir);
        _extractedDirs.Add(extractDir);

        ZipFile.ExtractToDirectory(filePath, extractDir);

        var manifestPath = Path.Combine(extractDir, ManifestEntry);
        if (!File.Exists(manifestPath))
            throw new InvalidDataException($"Missing {ManifestEntry} in project archive.");

        var json = File.ReadAllText(manifestPath);
        var manifest = JsonSerializer.Deserialize<ProjectManifest>(json, JsonOptions)
            ?? throw new InvalidDataException("Failed to deserialize project manifest.");

        CurrentProject = new Project
        {
            FilePath = filePath,
            Manifest = manifest,
            ExtractedDir = extractDir
        };
        HasUnsavedChanges = false;
        return CurrentProject;
    }

    /// <summary>
    /// Resolves a manifest-relative database path to the extracted temp file path.
    /// </summary>
    public string GetExtractedDbPath(Project project, string manifestRelativePath)
    {
        if (project.ExtractedDir is null)
            throw new InvalidOperationException("Project has no extracted directory. Open it first.");

        // Normalize separators: manifest uses forward slashes
        var normalizedPath = manifestRelativePath.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(project.ExtractedDir, normalizedPath);
    }

    /// <summary>
    /// Builds a ProjectState from an opened project, resolving extracted paths for databases.
    /// </summary>
    public ProjectState ToState(Project project)
    {
        var m = project.Manifest;
        return new ProjectState
        {
            ProjectName = m.Name,
            AdapterType = m.Connection.AdapterType,
            Channel = m.Connection.Channel,
            Bitrate = m.Connection.Bitrate,
            Can1DbPath = ResolveExtracted(project, m.Databases.Can1),
            Can2DbPath = ResolveExtracted(project, m.Databases.Can2),
            Lin1DbPath = ResolveExtracted(project, m.Databases.Lin1),
            Lin2DbPath = ResolveExtracted(project, m.Databases.Lin2),
            Lin3DbPath = ResolveExtracted(project, m.Databases.Lin3),
            Lin4DbPath = ResolveExtracted(project, m.Databases.Lin4),
            GraphTimeWindow = m.BusMonitor.GraphTimeWindow
        };
    }

    /// <summary>
    /// Cleans up all temp directories created by Open().
    /// </summary>
    public void CloseProject()
    {
        foreach (var dir in _extractedDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
            catch { /* best-effort */ }
        }
        _extractedDirs.Clear();
        CurrentProject = null;
        HasUnsavedChanges = false;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private string? RegisterDb(string? sourcePath, string busKey)
    {
        if (string.IsNullOrEmpty(sourcePath))
            return null;

        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        var entryName = $"{DbPrefix}{busKey}{ext}";
        _sourcePaths[entryName] = sourcePath;
        return entryName;
    }

    private string? ResolveExtracted(Project project, string? manifestRelativePath)
    {
        if (manifestRelativePath is null || project.ExtractedDir is null)
            return null;
        return GetExtractedDbPath(project, manifestRelativePath);
    }
}
