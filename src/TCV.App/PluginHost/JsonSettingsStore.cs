using System.IO;
using System.Text.Json;
using TCV.Core.PluginSdk;

namespace TCV.App.PluginHost;

internal sealed class JsonSettingsStore : ISettingsStore
{
    private readonly string _filePath;
    private Dictionary<string, JsonElement> _data = new();

    public JsonSettingsStore(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                _data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? new();
            }
        }
        catch { /* ignore corrupted file */ }
    }

    public T? Get<T>(string key)
    {
        if (!_data.TryGetValue(key, out var element)) return default;
        try { return element.Deserialize<T>(); }
        catch { return default; }
    }

    public void Set<T>(string key, T value)
    {
        _data[key] = JsonSerializer.SerializeToElement(value);
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
    }
}
