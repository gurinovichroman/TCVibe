namespace TCV.Core.Exceptions;

public class PluginLoadException : Exception
{
    public string? PluginPath { get; }

    public PluginLoadException(string message, string? pluginPath = null)
        : base(message)
    {
        PluginPath = pluginPath;
    }

    public PluginLoadException(string message, Exception inner, string? pluginPath = null)
        : base(message, inner)
    {
        PluginPath = pluginPath;
    }
}
