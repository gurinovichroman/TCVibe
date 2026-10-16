using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using TCV.Core.PluginSdk;

namespace TCV.App.PluginHost;

public sealed class PluginLoader
{
    private readonly ILogger<PluginLoader> _logger;

    public PluginLoader(ILogger<PluginLoader> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<IPlugin> LoadFromDirectory(string pluginsDir)
    {
        var plugins = new List<IPlugin>();
        if (!Directory.Exists(pluginsDir))
        {
            return plugins;
        }

        foreach (var dll in Directory.GetFiles(pluginsDir, "*.dll", SearchOption.AllDirectories))
        {
            plugins.AddRange(LoadFromFile(dll));
        }

        return plugins;
    }

    public IReadOnlyList<IPlugin> LoadFromFile(string dllPath) => TryLoadFromFile(dllPath, out _);

    /// <summary>
    /// Loads the plugin types of one assembly. A file that is not a plugin (a dependency DLL, an
    /// assembly the host already provides) yields nothing and no error; a file that cannot be
    /// loaded yields nothing and <paramref name="error"/> says why.
    /// </summary>
    public IReadOnlyList<IPlugin> TryLoadFromFile(string dllPath, out string? error)
    {
        error = null;
        var plugins = new List<IPlugin>();
        if (!File.Exists(dllPath))
        {
            error = $"File not found: {dllPath}";
            return plugins;
        }

        try
        {
            // Everything the host itself ships (TCV.Core, the framework, shared libraries) must come
            // from the host: a second copy of TCV.Core would make the plugin's IPlugin a different
            // type from ours, and it would silently register nothing.
            var simpleName = AssemblyName.GetAssemblyName(dllPath).Name;
            if (simpleName is not null && IsProvidedByHost(simpleName))
            {
                return plugins;
            }

            var ctx = new PluginLoadContext(dllPath);
            var asm = ctx.LoadFromAssemblyPath(Path.GetFullPath(dllPath));

            foreach (var type in asm.GetExportedTypes())
            {
                if (type.IsAbstract || !typeof(IPlugin).IsAssignableFrom(type))
                {
                    continue;
                }

                if (Activator.CreateInstance(type) is IPlugin plugin)
                {
                    plugins.Add(plugin);
                    _logger.LogInformation("Loaded plugin {Name} from {Dll}", plugin.Name, dllPath);
                }
            }
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException
                                       or ReflectionTypeLoadException or TypeLoadException or MissingMethodException
                                       or TargetInvocationException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Failed to load plugins from {Dll}", dllPath);
            error = ex is BadImageFormatException
                ? $"{Path.GetFileName(dllPath)} is not a .NET assembly"
                : $"{Path.GetFileName(dllPath)}: {ex.Message}";
        }

        return plugins;
    }

    private static bool IsProvidedByHost(string simpleName) =>
        simpleName.StartsWith("System.", StringComparison.Ordinal)
        || simpleName.StartsWith("Microsoft.", StringComparison.Ordinal)
        || AppDomain.CurrentDomain.GetAssemblies().Any(a =>
            string.Equals(a.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// One load context per plugin DLL. Its own dependencies are resolved from beside it
    /// (<see cref="AssemblyDependencyResolver"/>); anything the host already has is shared, so the
    /// plugin and the host agree on the SDK types.
    /// </summary>
    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        private readonly string _directory;

        public PluginLoadContext(string pluginPath)
            : base(Path.GetFileNameWithoutExtension(pluginPath), isCollectible: false)
        {
            var full = Path.GetFullPath(pluginPath);
            _resolver = new AssemblyDependencyResolver(full);
            _directory = Path.GetDirectoryName(full)!;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is { } name && IsProvidedByHost(name))
            {
                return null; // default context supplies it
            }

            // Without a .deps.json the resolver knows nothing; a plain sibling DLL is still found.
            var path = _resolver.ResolveAssemblyToPath(assemblyName)
                       ?? Path.Combine(_directory, assemblyName.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}
