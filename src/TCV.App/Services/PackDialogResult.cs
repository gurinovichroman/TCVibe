using TCV.Core.PluginSdk;

namespace TCV.App.Services;

public enum ArchiveCompressionLevel { Store, Fast, Normal, Ultra }

public record PackDialogResult(
    string ArchivePath,
    ArchiveFormat Format,
    ArchiveCompressionLevel CompressionLevel,
    string? Password,
    bool AddToExisting = false);
