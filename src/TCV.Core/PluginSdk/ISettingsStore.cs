namespace TCV.Core.PluginSdk;

public interface ISettingsStore
{
    T? Get<T>(string key);
    void Set<T>(string key, T value);
    void Save();
}
