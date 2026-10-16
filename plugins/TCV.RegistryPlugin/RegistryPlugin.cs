using TCV.Core.Domain;
using TCV.Core.Interfaces;
using TCV.Core.PluginSdk;

namespace TCV.RegistryPlugin;

/// <summary>
/// Example external .NET plugin — exposes Windows Registry as a virtual filesystem.
/// Scheme: "reg" — e.g. reg://HKLM/SOFTWARE/Microsoft
/// </summary>
public class RegistryPlugin : IFileSystemPlugin
{
    public string Id => "tcv.registry";
    public string Name => "Windows Registry";
    public Version Version => new(1, 0, 0);
    public string[] Schemes => ["reg"];

    // The host registers the plugin and only then calls Initialize. Registering from here would call
    // Initialize again — endless recursion.
    public void Initialize(IPluginHost host) { }

    public IVirtualFileSystem Create(string rootPath, IReadOnlyDictionary<string, string>? options = null)
        => new RegistryFileSystem();
}

public sealed class RegistryFileSystem : IVirtualFileSystem
{
    public string Scheme => "reg";
    public string DisplayName => "Windows Registry";

    public event EventHandler<VfsChangedEventArgs>? Changed;

    public Task<IReadOnlyList<IFileEntry>> ListAsync(VfsPath path, CancellationToken ct = default)
    {
        var regPath = VfsPathToRegistryPath(path.Value);
        var results = new List<IFileEntry>();

        using var key = OpenRegistryKey(regPath);
        if (key is null) return Task.FromResult<IReadOnlyList<IFileEntry>>(results);

        // Sub-keys → directories
        foreach (var subKeyName in key.GetSubKeyNames())
        {
            results.Add(new RegistryEntry(
                Path: new VfsPath($"{path.Value}/{subKeyName}"),
                Name: subKeyName,
                IsDirectory: true,
                Size: -1,
                LastModified: DateTime.MinValue,
                Attributes: System.IO.FileAttributes.Directory));
        }

        // Values → files
        foreach (var valueName in key.GetValueNames())
        {
            var displayName = string.IsNullOrEmpty(valueName) ? "(Default)" : valueName;
            results.Add(ValueEntry(new VfsPath($"{path.Value}/{displayName}"), displayName, key.GetValue(valueName)));
        }

        return Task.FromResult<IReadOnlyList<IFileEntry>>(results);
    }

    /// <summary>
    /// The key or value at <paramref name="path"/>, or null when there is none — callers use a null
    /// here to decide that something does not exist (INV-08), so it must never be invented.
    /// </summary>
    public Task<IFileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct = default)
    {
        var name = System.IO.Path.GetFileName(path.Value.TrimEnd('/'));
        if (path.Value.Trim('/').Length == 0)
        {
            // The root lists the hives: it exists, though it is no registry key itself.
            return Task.FromResult<IFileEntry?>(new RegistryEntry(path, string.Empty, true, -1, DateTime.MinValue,
                System.IO.FileAttributes.Directory));
        }

        using (var key = OpenRegistryKey(VfsPathToRegistryPath(path.Value)))
        {
            if (key is not null)
            {
                return Task.FromResult<IFileEntry?>(new RegistryEntry(path, name, true, -1, DateTime.MinValue,
                    System.IO.FileAttributes.Directory));
            }
        }

        var parent = System.IO.Path.GetDirectoryName(path.Value.TrimEnd('/'))?.Replace('\\', '/');
        if (string.IsNullOrEmpty(parent))
        {
            return Task.FromResult<IFileEntry?>(null);
        }

        using var parentKey = OpenRegistryKey(VfsPathToRegistryPath(parent));
        var valueName = name == "(Default)" ? string.Empty : name;
        if (parentKey is null || !parentKey.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase))
        {
            return Task.FromResult<IFileEntry?>(null);
        }

        return Task.FromResult<IFileEntry?>(ValueEntry(path, name, parentKey.GetValue(valueName)));
    }

    public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct = default)
    {
        var regPath = VfsPathToRegistryPath(System.IO.Path.GetDirectoryName(path.Value)!);
        var valueName = System.IO.Path.GetFileName(path.Value);
        if (valueName == "(Default)") valueName = string.Empty;

        using var key = OpenRegistryKey(regPath);
        return Task.FromResult<Stream>(new System.IO.MemoryStream(ValueBytes(key?.GetValue(valueName))));
    }

    private static RegistryEntry ValueEntry(VfsPath path, string name, object? value) =>
        new(path, name, IsDirectory: false, Size: ValueBytes(value).Length, DateTime.MinValue,
            System.IO.FileAttributes.Normal);

    /// <summary>
    /// The value as the bytes a reader gets. The listed size is computed from the same bytes, so a
    /// copy that checks lengths agrees with the listing. Binary values read as hex and multi-strings
    /// one per line, rather than as "System.Byte[]".
    /// </summary>
    private static byte[] ValueBytes(object? value)
    {
        var text = value switch
        {
            null => string.Empty,
            byte[] bytes => Convert.ToHexString(bytes),
            string[] lines => string.Join(Environment.NewLine, lines),
            _ => value.ToString() ?? string.Empty,
        };
        return System.Text.Encoding.UTF8.GetBytes(text);
    }

    public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite = false, CancellationToken ct = default)
        => throw new NotSupportedException("Registry write not supported in read-only mode.");

    public Task CopyAsync(VfsPath source, VfsPath destination, IProgress<FileProgress>? progress, CancellationToken ct)
        => throw new NotSupportedException();

    public Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct)
        => throw new NotSupportedException();

    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct)
        => throw new NotSupportedException();

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken ct)
        => throw new NotSupportedException();

    public Task RenameAsync(VfsPath path, string newName, CancellationToken ct)
        => throw new NotSupportedException();

    public bool SupportsOperation(VfsOperation operation) =>
        operation == VfsOperation.Read;

    public void Dispose() { }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string VfsPathToRegistryPath(string vfsPath)
    {
        // "reg://HKLM/SOFTWARE" → "HKEY_LOCAL_MACHINE\SOFTWARE"
        var path = vfsPath
            .TrimStart('/')
            .Replace('/', '\\');

        if (path.StartsWith("reg://", StringComparison.OrdinalIgnoreCase))
            path = path[6..];

        // Only the first segment is a hive; a subkey that happens to contain "HKCU" stays as it is.
        var sep = path.IndexOf('\\');
        var hive = sep >= 0 ? path[..sep] : path;
        var rest = sep >= 0 ? path[sep..] : string.Empty;

        hive = hive.ToUpperInvariant() switch
        {
            "HKLM" => "HKEY_LOCAL_MACHINE",
            "HKCU" => "HKEY_CURRENT_USER",
            "HKCR" => "HKEY_CLASSES_ROOT",
            "HKU" => "HKEY_USERS",
            "HKCC" => "HKEY_CURRENT_CONFIG",
            _ => hive,
        };

        return hive + rest;
    }

    private static Microsoft.Win32.RegistryKey? OpenRegistryKey(string regPath)
    {
        var sep = regPath.IndexOf('\\');
        var hive = sep >= 0 ? regPath[..sep] : regPath;
        var subPath = sep >= 0 ? regPath[(sep + 1)..] : string.Empty;

        var rootKey = hive.ToUpperInvariant() switch
        {
            "HKEY_LOCAL_MACHINE" => Microsoft.Win32.Registry.LocalMachine,
            "HKEY_CURRENT_USER" => Microsoft.Win32.Registry.CurrentUser,
            "HKEY_CLASSES_ROOT" => Microsoft.Win32.Registry.ClassesRoot,
            "HKEY_USERS" => Microsoft.Win32.Registry.Users,
            "HKEY_CURRENT_CONFIG" => Microsoft.Win32.Registry.CurrentConfig,
            _ => null,
        };

        if (rootKey is null) return null;
        return string.IsNullOrEmpty(subPath) ? rootKey : rootKey.OpenSubKey(subPath);
    }
}

internal sealed record RegistryEntry(
    VfsPath Path,
    string Name,
    bool IsDirectory,
    long Size,
    DateTime LastModified,
    System.IO.FileAttributes Attributes) : IFileEntry;
