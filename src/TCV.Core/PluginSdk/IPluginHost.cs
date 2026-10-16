using TCV.Core.Interfaces;

namespace TCV.Core.PluginSdk;

public interface IPluginHost
{
    void RegisterFileSystem(IFileSystemPlugin plugin);
    void RegisterTool(IToolPlugin plugin);
    void RegisterColumn(IColumnPlugin plugin);
    ISettingsStore GetSettings(string pluginId);
    IVirtualFileSystem Resolve(string path);
}
