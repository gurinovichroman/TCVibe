using TCV.Core.Interfaces;

namespace TCV.Core.PluginSdk;

public interface IFileSystemPlugin : IPlugin
{
    string[] Schemes { get; }
    IVirtualFileSystem Create(string rootPath, IReadOnlyDictionary<string, string>? options = null);
}
