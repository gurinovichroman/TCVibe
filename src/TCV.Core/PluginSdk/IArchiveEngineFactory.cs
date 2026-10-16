namespace TCV.Core.PluginSdk;

/// <summary>Factory for creating archive engines by format.</summary>
public interface IArchiveEngineFactory
{
    /// <summary>Creates an archive engine for the specified format.</summary>
    /// <param name="format">Archive format (e.g., "zip", "7z", "rar", "tar", "iso", "cab").</param>
    /// <param name="password">Optional password for encrypted archives.</param>
    /// <returns>An archive engine instance.</returns>
    /// <exception cref="NotSupportedException">Thrown when the format is not supported.</exception>
    IArchiveEngine Create(string format, string? password = null);
}
