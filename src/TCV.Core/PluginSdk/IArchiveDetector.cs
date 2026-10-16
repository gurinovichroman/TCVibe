namespace TCV.Core.PluginSdk;

/// <summary>Service for detecting archive formats.</summary>
public interface IArchiveDetector
{
    /// <summary>Detects the archive format from a file path.</summary>
    /// <param name="filePath">Path to the file to detect.</param>
    /// <returns>The detected archive format, or Unknown if not recognized.</returns>
    ArchiveFormat DetectFormat(string filePath);

    /// <summary>Detects the archive format from a file extension.</summary>
    /// <param name="filePath">File name or path to check extension.</param>
    /// <returns>The detected archive format based on extension, or Unknown.</returns>
    ArchiveFormat DetectByExtension(string filePath);

    /// <summary>Checks if a file is a supported archive.</summary>
    /// <param name="filePath">Path to the file to check.</param>
    /// <returns>True if the file is a supported archive format.</returns>
    bool IsArchive(string filePath);
}
