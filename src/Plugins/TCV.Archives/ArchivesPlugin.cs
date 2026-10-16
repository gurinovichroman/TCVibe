using TCV.Core.Interfaces;
using TCV.Core.PluginSdk;
using TCV.Archives.Engines;

namespace TCV.Archives;

/// <summary>Plugin for navigating ZIP, 7z, RAR, TAR archives as virtual file systems.</summary>
public sealed class ArchivesPlugin : IArchivePlugin
{
    private readonly IArchiveEngineFactory _engineFactory;

    // Passwords the user typed this session, by archive path. Memory only: nothing about a
    // password is ever written next to the exe (INV-03 keeps state there, secrets stay out of it).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _passwords =
        new(StringComparer.OrdinalIgnoreCase);

    public ArchivesPlugin() : this(new ArchiveEngineFactory()) { }

    public ArchivesPlugin(IArchiveEngineFactory engineFactory)
    {
        _engineFactory = engineFactory;
    }

    public string Id => "tcv.archives";
    public string Name => "Archives";
    public Version Version => new(2, 0, 0);
    public string[] Schemes => ["zip", "7z", "rar", "tar", "iso", "cab"];

    public IArchiveEngineFactory EngineFactory => _engineFactory;

    // Initialize is called by PluginHost AFTER registration.
    // No setup needed for this plugin.
    public void Initialize(IPluginHost host) { }

    /// <summary>
    /// Creates an <see cref="ArchiveFileSystem"/> for the given archive file path.
    /// </summary>
    /// <param name="rootPath">Absolute path to the archive file (e.g. "C:\files\archive.zip").</param>
    /// <param name="options">Optional options. Supported key: "password".</param>
    /// <exception cref="ArchivePasswordRequiredException">
    /// Thrown when the archive is encrypted and no (or wrong) password was supplied.
    /// </exception>
    public IVirtualFileSystem Create(string rootPath, IReadOnlyDictionary<string, string>? options = null)
    {
        if (!File.Exists(rootPath))
            throw new FileNotFoundException($"Archive not found: {rootPath}", rootPath);

        string? password = null;
        options?.TryGetValue("password", out password);
        password ??= RememberedPassword(rootPath);

        var format = DetectFormat(rootPath);
        if (format is null)
        {
            throw new NotSupportedException($"Unsupported archive format: {rootPath}");
        }

        var engine = _engineFactory.Create(format, password);

        // Eagerly list entries so that ArchivePasswordRequiredException is thrown here
        // rather than silently inside FileListViewModel.LoadAsync later.
        IReadOnlyList<ArchiveEntry> entries;
        try
        {
            entries = engine.ListAll(rootPath);
        }
        catch (ArchivePasswordRequiredException)
        {
            // A remembered password that no longer opens the archive must not be offered again,
            // or the user would never be asked for the right one this session.
            ForgetPassword(rootPath);
            throw;
        }

        if (!string.IsNullOrEmpty(password))
        {
            RememberPassword(rootPath, password);
        }

        return new ArchiveFileSystem(rootPath, engine, entries);
    }

    public TCV.Core.PluginSdk.IArchiveEngine CreateEngine(string archivePath, string? password = null)
    {
        var format = DetectFormat(archivePath)
            ?? throw new NotSupportedException($"Unsupported archive format: {archivePath}");
        return _engineFactory.Create(format, password ?? RememberedPassword(archivePath));
    }

    public void RememberPassword(string archivePath, string password) =>
        _passwords[Path.GetFullPath(archivePath)] = password;

    public void ForgetPassword(string archivePath) =>
        _passwords.TryRemove(Path.GetFullPath(archivePath), out _);

    private string? RememberedPassword(string archivePath) =>
        _passwords.TryGetValue(Path.GetFullPath(archivePath), out var known) ? known : null;

    public string? DetectFormat(string filePath)
    {
        var format = ArchiveDetector.DetectFormat(filePath);
        return format switch
        {
            ArchiveDetector.ArchiveFormat.Zip => "zip",
            ArchiveDetector.ArchiveFormat.SevenZip => "7z",
            ArchiveDetector.ArchiveFormat.Rar => "rar",
            ArchiveDetector.ArchiveFormat.Tar => "tar",
            ArchiveDetector.ArchiveFormat.Iso => "iso",
            ArchiveDetector.ArchiveFormat.Cab => "cab",
            _ => null,
        };
    }

    public bool IsArchive(string filePath) => ArchiveDetector.IsArchive(filePath);
}
