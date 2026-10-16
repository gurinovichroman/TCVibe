// Re-export IArchiveEngine from TCV.Core.PluginSdk for convenience
namespace TCV.Archives.Engines;

/// <summary>Abstraction over a specific archive format (ZIP, 7z, RAR, TAR).</summary>
/// <remarks>This is the same interface as <c>TCV.Core.PluginSdk.IArchiveEngine</c>.</remarks>
public interface IArchiveEngine : TCV.Core.PluginSdk.IArchiveEngine
{
}
