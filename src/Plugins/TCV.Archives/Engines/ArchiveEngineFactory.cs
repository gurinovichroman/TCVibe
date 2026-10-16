using TCV.Core.PluginSdk;

namespace TCV.Archives.Engines;

/// <summary>Factory for creating archive engines by format.</summary>
public sealed class ArchiveEngineFactory : IArchiveEngineFactory
{
    /// <summary>Creates an archive engine for the specified format.</summary>
    /// <param name="format">Archive format (e.g., "zip", "7z", "rar", "tar", "iso", "cab").</param>
    /// <param name="password">Optional password for encrypted archives.</param>
    /// <returns>An archive engine instance.</returns>
    /// <exception cref="NotSupportedException">Thrown when the format is not supported.</exception>
    public TCV.Core.PluginSdk.IArchiveEngine Create(string format, string? password = null)
    {
        return format.ToLowerInvariant() switch
        {
            "zip" => new ZipEngine(password),
            "7z" => new SevenZipEngine(password),
            "rar" => new RarEngine(password),
            "tar" => new TarEngine(),
            "iso" => new IsoEngine(),
            "cab" => new CabEngine(),
            _ => throw new NotSupportedException($"Unsupported archive format: {format}"),
        };
    }
}
