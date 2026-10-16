using TCV.Core.PluginSdk;

namespace TCV.App.PluginHost;

/// <summary>Registry of all loaded plugins.</summary>
public sealed class PluginRegistry
{
    private readonly List<IFileSystemPlugin> _fileSystems = new();
    private readonly List<IToolPlugin> _tools = new();
    private readonly List<IColumnPlugin> _columns = new();

    public void AddFileSystem(IFileSystemPlugin plugin) => _fileSystems.Add(plugin);
    public void AddTool(IToolPlugin plugin) => _tools.Add(plugin);
    public void AddColumn(IColumnPlugin plugin) => _columns.Add(plugin);

    /// <summary>True when a plugin with this id is already registered, of any kind.</summary>
    public bool Contains(string pluginId) =>
        _fileSystems.Any(p => IdEquals(p.Id, pluginId)) ||
        _tools.Any(p => IdEquals(p.Id, pluginId)) ||
        _columns.Any(p => IdEquals(p.Id, pluginId));

    /// <summary>Takes a plugin back out — used when its Initialize threw, so a half-started plugin is not left listed.</summary>
    public void Remove(IPlugin plugin)
    {
        _fileSystems.RemoveAll(p => ReferenceEquals(p, plugin));
        _tools.RemoveAll(p => ReferenceEquals(p, plugin));
        _columns.RemoveAll(p => ReferenceEquals(p, plugin));
    }

    private static bool IdEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<IFileSystemPlugin> FileSystems => _fileSystems;
    public IReadOnlyList<IToolPlugin> Tools => _tools;
    public IReadOnlyList<IColumnPlugin> Columns => _columns;
}
