using System.IO;
using CanLinConfig.Models;

namespace CanLinConfig.Services.Export;

public interface IFrameExporter
{
    string FileExtension { get; }
    string FileFilter { get; }
    void Export(Stream stream, IReadOnlyList<BusFrame> frames, DatabaseManager? dbManager = null);
}
