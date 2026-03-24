// software/CanLinConfig/Models/Project.cs
using System.Text.Json.Serialization;

namespace CanLinConfig.Models;

public class Project
{
    public string? FilePath { get; set; }
    public ProjectManifest Manifest { get; set; } = new();
    public string? ExtractedDir { get; set; }
}

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

    [JsonPropertyName("instruments")]
    public List<WidgetLayout> Instruments { get; set; } = [];
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
