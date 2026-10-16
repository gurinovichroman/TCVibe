using System.IO;
using Microsoft.Extensions.Logging;
using TCV.App.FileSystem;
using TCV.Archives;
using TCV.Core.Interfaces;
using TCV.Core.PluginSdk;
using TCV.MultiRename;
using TCV.Network;
using TCV.Search;
using TCV.Tools;

namespace TCV.App.PluginHost;

/// <summary>What loading one plugin DLL achieved: how many plugins came up, and why the others did not.</summary>
public sealed record PluginLoadResult(int Registered, IReadOnlyList<string> Errors);

public sealed class PluginHost : IPluginHost, IDisposable
{
    private readonly PluginRegistry _registry = new();
    private readonly PluginLoader _loader;
    private readonly ILogger<PluginHost> _logger;
    private readonly WindowsFileSystem _localFs;
    private readonly string _settingsBaseDir;

    public PluginHost(
        ILogger<PluginHost> logger,
        PluginLoader loader,
        WindowsFileSystem localFs)
    {
        _logger = logger;
        _loader = loader;
        _localFs = localFs;
        _settingsBaseDir = global::TCV.Core.Domain.AppPaths.Data("plugin-settings");
    }

    public void RegisterFileSystem(IFileSystemPlugin plugin)
    {
        if (!Register(plugin, _registry.AddFileSystem))
        {
            return;
        }

        _logger.LogInformation("Registered FS plugin: {Name} ({Schemes})",
            plugin.Name, string.Join(", ", plugin.Schemes));
    }

    public void RegisterTool(IToolPlugin plugin)
    {
        if (Register(plugin, _registry.AddTool))
        {
            _logger.LogInformation("Registered tool plugin: {Name}", plugin.Name);
        }
    }

    public void RegisterColumn(IColumnPlugin plugin) => Register(plugin, _registry.AddColumn);

    /// <summary>
    /// Adds a plugin to the registry and initialises it. A plugin id registers once: the second
    /// attempt (the same DLL loaded twice, or a plugin whose <c>Initialize</c> registers itself again)
    /// is ignored instead of listed twice or recursing. A plugin whose <c>Initialize</c> throws is
    /// taken back out and the exception goes to the caller.
    /// </summary>
    private bool Register<T>(T plugin, Action<T> add) where T : IPlugin
    {
        if (_registry.Contains(plugin.Id))
        {
            _logger.LogWarning("Plugin {Id} is already registered; ignoring the duplicate", plugin.Id);
            return false;
        }

        add(plugin);
        try
        {
            plugin.Initialize(this);
        }
        catch
        {
            _registry.Remove(plugin);
            throw;
        }

        return true;
    }

    public ISettingsStore GetSettings(string pluginId)
    {
        var path = Path.Combine(_settingsBaseDir, pluginId, "settings.json");
        return new JsonSettingsStore(path);
    }

    public IVirtualFileSystem Resolve(string path)
    {
        // Local paths: C:\..., /path, file://...
        if (path.Length >= 2 && path[1] == ':') return _localFs;
        if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return _localFs;
        if (path.StartsWith("/") && !path.Contains("://")) return _localFs;

        var schemeSep = path.IndexOf("://", StringComparison.Ordinal);
        if (schemeSep > 0)
        {
            var scheme = path[..schemeSep];
            var plugin = _registry.FileSystems.FirstOrDefault(p =>
                p.Schemes.Contains(scheme, StringComparer.OrdinalIgnoreCase));
            if (plugin is not null)
                return plugin.Create(path);
        }

        return _localFs;
    }

    /// <summary>Registers all built-in plugins (Archives, Network, Search, MultiRename, Tools, Git).</summary>
    public void LoadBuiltinPlugins()
    {
        RegisterFileSystem(new ArchivesPlugin());
        RegisterFileSystem(new NetworkPlugin());
        RegisterTool(new SearchPlugin());
        RegisterTool(new MultiRenamePlugin());
        RegisterTool(new ChecksumPlugin());
        RegisterTool(new UserToolsPlugin());
        // Git status is not a plugin: it is a built-in column (ColumnCatalog.Git) fed by GitStatusProvider.
        _logger.LogInformation("Built-in plugins loaded");
    }

    public void ScanExternalPlugins(string pluginsDir)
    {
        _logger.LogInformation("Scanning external plugins in {Dir}", pluginsDir);
        if (!Directory.Exists(pluginsDir))
        {
            return;
        }

        foreach (var dll in Directory.GetFiles(pluginsDir, "*.dll", SearchOption.AllDirectories))
        {
            // One broken plugin must never keep the file manager from starting: there would be no
            // window to remove the DLL from.
            var result = LoadPluginFile(dll);
            foreach (var error in result.Errors)
            {
                _logger.LogWarning("External plugin {Dll} skipped: {Error}", dll, error);
            }
        }
    }

    /// <summary>
    /// Loads one plugin DLL and registers what it contains. Never throws: what went wrong comes
    /// back in <see cref="PluginLoadResult.Errors"/>.
    /// </summary>
    public PluginLoadResult LoadPluginFile(string dllPath)
    {
        var errors = new List<string>();
        var registered = 0;

        var loaded = _loader.TryLoadFromFile(dllPath, out var loadError);
        if (loadError is not null)
        {
            errors.Add(loadError);
        }

        foreach (var plugin in loaded)
        {
            try
            {
                var before = _registry.Contains(plugin.Id);
                switch (plugin)
                {
                    case IFileSystemPlugin fs: RegisterFileSystem(fs); break;
                    case IToolPlugin tool: RegisterTool(tool); break;
                    case IColumnPlugin col: RegisterColumn(col); break;
                }

                if (before)
                {
                    errors.Add($"{plugin.Name}: already loaded");
                }
                else
                {
                    registered++;
                    _logger.LogWarning("External plugin {Name} {Version} loaded from {Dll} — third-party code, not verified",
                        plugin.Name, plugin.Version, dllPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Plugin {Name} failed to initialise", plugin.Name);
                errors.Add($"{plugin.Name}: {ex.Message}");
            }
        }

        return new PluginLoadResult(registered, errors);
    }

    public IReadOnlyList<IToolPlugin> Tools => _registry.Tools;
    public IReadOnlyList<IFileSystemPlugin> FileSystems => _registry.FileSystems;
    public IReadOnlyList<IColumnPlugin> Columns => _registry.Columns;
    public PluginRegistry Registry => _registry;

    public void Dispose() { }
}
