namespace TCV.Core.PluginSdk;

/// <summary>Extended plugin interface for archive operations.</summary>
/// <remarks>
/// Provides access to archive-specific functionality like engine creation and format detection.
/// This interface allows the application to interact with archive plugins without direct coupling.
/// </remarks>
public interface IArchivePlugin : IFileSystemPlugin
{
    /// <summary>Gets the archive engine factory for creating format-specific engines.</summary>
    IArchiveEngineFactory EngineFactory { get; }

    /// <summary>Detects the archive format from a file path.</summary>
    /// <param name="filePath">Path to the file to detect.</param>
    /// <returns>The detected archive format, or null if not an archive.</returns>
    string? DetectFormat(string filePath);

    /// <summary>Checks if a file is a supported archive.</summary>
    /// <param name="filePath">Path to the file to check.</param>
    /// <returns>True if the file is a supported archive format.</returns>
    bool IsArchive(string filePath);

    /// <summary>
    /// Creates the engine for an existing archive on disk. When the user already typed the archive's
    /// password this session it is used, so a second operation on the same archive (copy out, view,
    /// unpack) does not prompt again. The password lives in memory only.
    /// </summary>
    /// <exception cref="NotSupportedException">The file is not a supported archive.</exception>
    IArchiveEngine CreateEngine(string archivePath, string? password = null)
    {
        var format = DetectFormat(archivePath)
            ?? throw new NotSupportedException($"Unsupported archive format: {archivePath}");
        return EngineFactory.Create(format, password);
    }

    /// <summary>Remembers the password that opened <paramref name="archivePath"/> (memory only).</summary>
    void RememberPassword(string archivePath, string password) { }

    /// <summary>Drops a remembered password that turned out to be wrong, so the user is asked again.</summary>
    void ForgetPassword(string archivePath) { }
}
