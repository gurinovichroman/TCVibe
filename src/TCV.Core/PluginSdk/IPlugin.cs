namespace TCV.Core.PluginSdk;

public interface IPlugin
{
    string Id { get; }
    string Name { get; }
    Version Version { get; }
    void Initialize(IPluginHost host);
}
