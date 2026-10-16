using TCV.Core.PluginSdk;

namespace TCV.Archives;

/// <summary>Adapter that implements IArchiveDetector using the internal ArchiveDetector.</summary>
public sealed class ArchiveDetectorService : IArchiveDetector
{
    public ArchiveFormat DetectFormat(string filePath)
    {
        var format = ArchiveDetector.DetectFormat(filePath);
        return format switch
        {
            ArchiveDetector.ArchiveFormat.Zip => ArchiveFormat.Zip,
            ArchiveDetector.ArchiveFormat.SevenZip => ArchiveFormat.SevenZip,
            ArchiveDetector.ArchiveFormat.Rar => ArchiveFormat.Rar,
            ArchiveDetector.ArchiveFormat.Tar => ArchiveFormat.Tar,
            ArchiveDetector.ArchiveFormat.Iso => ArchiveFormat.Iso,
            ArchiveDetector.ArchiveFormat.Cab => ArchiveFormat.Cab,
            _ => ArchiveFormat.Unknown,
        };
    }

    public ArchiveFormat DetectByExtension(string filePath)
    {
        var format = ArchiveDetector.DetectByExtension(filePath);
        return format switch
        {
            ArchiveDetector.ArchiveFormat.Zip => ArchiveFormat.Zip,
            ArchiveDetector.ArchiveFormat.SevenZip => ArchiveFormat.SevenZip,
            ArchiveDetector.ArchiveFormat.Rar => ArchiveFormat.Rar,
            ArchiveDetector.ArchiveFormat.Tar => ArchiveFormat.Tar,
            ArchiveDetector.ArchiveFormat.Iso => ArchiveFormat.Iso,
            ArchiveDetector.ArchiveFormat.Cab => ArchiveFormat.Cab,
            _ => ArchiveFormat.Unknown,
        };
    }

    public bool IsArchive(string filePath) => ArchiveDetector.IsArchive(filePath);
}
