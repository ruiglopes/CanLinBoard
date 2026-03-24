namespace CanLinConfig.Models;

/// <summary>
/// DTO that captures the current tool state for project save/restore.
/// </summary>
public class ProjectState
{
    public string ProjectName { get; set; } = "Untitled";

    // Connection
    public string? AdapterType { get; set; }
    public string? Channel { get; set; }
    public uint Bitrate { get; set; } = 500000;

    // Database source paths (absolute paths on disk — embedded into .clpkg on save)
    public string? Can1DbPath { get; set; }
    public string? Can2DbPath { get; set; }
    public string? Lin1DbPath { get; set; }
    public string? Lin2DbPath { get; set; }
    public string? Lin3DbPath { get; set; }
    public string? Lin4DbPath { get; set; }

    // Bus monitor settings
    public double GraphTimeWindow { get; set; } = 30.0;
}
