using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TCV.App.Localization;
using TCV.App.PluginHost;

namespace TCV.App.ViewModels.Dialogs;

public partial class PluginManagerDialogViewModel : ObservableObject
{
    /// <summary>Most sibling files copied along with a plugin DLL (its dependencies and manifest).</summary>
    private const int MaxCompanionFiles = 60;

    private readonly PluginHost.PluginHost _pluginHost;
    private readonly string _pluginsDirectory;
    private readonly Func<string, string, bool> _confirm;

    /// <param name="loader">Kept for the existing constructor shape; loading goes through the host.</param>
    /// <param name="confirm">Yes/no question (title, message); the default shows a message box.</param>
    /// <param name="pluginsDirectory">Where a loaded plugin is copied so it is still there next launch.</param>
    public PluginManagerDialogViewModel(
        PluginHost.PluginHost pluginHost,
        PluginLoader loader,
        Func<string, string, bool>? confirm = null,
        string? pluginsDirectory = null)
    {
        _ = loader;
        _pluginHost = pluginHost;
        _pluginsDirectory = pluginsDirectory ?? global::TCV.Core.Domain.AppPaths.Data("plugins");
        // The app's own confirm (UI-language buttons, owned by the active window), not MessageBox.
        _confirm = confirm ?? ((title, message) => Views.Dialogs.ConfirmDialog.Ask(title, message, isWarning: true));
        Refresh();
    }

    public ObservableCollection<PluginInfoViewModel> FileSystemPlugins { get; } = new();
    public ObservableCollection<PluginInfoViewModel> ToolPlugins { get; } = new();
    public ObservableCollection<PluginInfoViewModel> ColumnPlugins { get; } = new();

    [ObservableProperty] private PluginInfoViewModel? _selectedPlugin;
    [ObservableProperty] private string _statusMessage = string.Empty;

    [RelayCommand]
    private void Refresh()
    {
        FileSystemPlugins.Clear();
        ToolPlugins.Clear();
        ColumnPlugins.Clear();

        foreach (var p in _pluginHost.FileSystems)
        {
            FileSystemPlugins.Add(new PluginInfoViewModel(p.Id, p.Name, p.Version.ToString(),
                string.Join(", ", p.Schemes), "FileSystem"));
        }

        foreach (var p in _pluginHost.Tools)
        {
            ToolPlugins.Add(new PluginInfoViewModel(p.Id, p.Name, p.Version.ToString(),
                LocalizedMenuTitle(p.Id) ?? p.MenuTitle, "Tool"));
        }

        foreach (var p in _pluginHost.Columns)
        {
            ColumnPlugins.Add(new PluginInfoViewModel(p.Id, p.Name, p.Version.ToString(),
                string.Join(", ", p.Columns.Select(c => c.Id)), "Column"));
        }
    }

    /// <summary>
    /// The built-in tool plugins name their menu entry in one language (a plugin sees no string
    /// dictionary); the manager shows those in the UI language. A third-party plugin's title is its own.
    /// </summary>
    private static string? LocalizedMenuTitle(string pluginId) => pluginId switch
    {
        "tcv.multirename" => LocalizationManager.Get("Plugins_MenuMultiRename"),
        "tcv.search" => LocalizationManager.Get("Plugins_MenuSearch"),
        "tcv.tools.checksum" => LocalizationManager.Get("Plugins_MenuChecksum"),
        "tcv.tools.usertools" => LocalizationManager.Get("Plugins_MenuUserTools"),
        _ => null,
    };

    [RelayCommand]
    private void LoadPlugin()
    {
        // A plugin is arbitrary code running as this user: say so before the file is opened.
        if (!_confirm(
                LocalizationManager.Get("Plugins_LoadTitle"),
                LocalizationManager.Get("Plugins_LoadWarning")))
        {
            return;
        }

        var dlg = new OpenFileDialog
        {
            Title = LocalizationManager.Get("Plugins_LoadTitle"),
            Filter = LocalizationManager.Get("Plugins_Filter"),
            Multiselect = false,
        };

        if (dlg.ShowDialog() != true)
        {
            return;
        }

        LoadFrom(dlg.FileName);
    }

    /// <summary>
    /// Loads the plugins in one DLL and reports the outcome in <see cref="StatusMessage"/>. A plugin
    /// that came up is copied into the plugins folder — otherwise it would vanish at the next
    /// launch and the manager would have promised something it cannot keep.
    /// </summary>
    public void LoadFrom(string dllPath)
    {
        var result = _pluginHost.LoadPluginFile(dllPath);

        if (result.Registered > 0)
        {
            var persisted = TryPersist(dllPath);
            StatusMessage = string.Format(
                LocalizationManager.Get(persisted ? "Plugins_Loaded" : "Plugins_LoadedNotSaved"),
                result.Registered);
        }
        else if (result.Errors.Count > 0)
        {
            StatusMessage = string.Format(LocalizationManager.Get("Plugins_LoadFailed"), string.Join("; ", result.Errors));
        }
        else
        {
            StatusMessage = LocalizationManager.Get("Plugins_NoneFound");
        }

        Refresh();
    }

    private bool TryPersist(string dllPath)
    {
        try
        {
            var source = Path.GetFullPath(dllPath);
            var sourceDirectory = Path.GetDirectoryName(source)!;
            var root = Path.GetFullPath(_pluginsDirectory);

            // Already lives under plugins/: it will be scanned next launch as it is. The separator
            // matters: "plugins-old" next to "plugins" is not inside it.
            if ((sourceDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
                .StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var target = Path.Combine(root, Path.GetFileNameWithoutExtension(source));
            Directory.CreateDirectory(target);

            // The DLL, its manifest and the DLLs the manifest names — not every DLL that happens to
            // share its folder: loaded from Downloads, that would copy (and load at every start) all
            // of Downloads.
            var depsPath = Path.ChangeExtension(source, ".deps.json");
            var deps = File.Exists(depsPath) ? File.ReadAllText(depsPath) : null;
            foreach (var name in CompanionFileNames(Path.GetFileName(source), deps).Take(MaxCompanionFiles))
            {
                var file = Path.Combine(sourceDirectory, name);
                if (File.Exists(file))
                {
                    File.Copy(file, Path.Combine(target, name), overwrite: true);
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// File names to copy along with a plugin: the DLL itself, its <c>.deps.json</c>, and every runtime
    /// assembly the manifest lists (<c>targets → library → runtime</c>). Without a manifest only the
    /// DLL goes. Pure, so it is testable without a disk.
    /// </summary>
    public static IReadOnlyList<string> CompanionFileNames(string dllFileName, string? depsJson)
    {
        var names = new List<string> { dllFileName };
        if (string.IsNullOrWhiteSpace(depsJson))
        {
            return names;
        }

        names.Add(Path.ChangeExtension(dllFileName, ".deps.json"));
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(depsJson);
            if (!doc.RootElement.TryGetProperty("targets", out var targets))
            {
                return names;
            }

            foreach (var target in targets.EnumerateObject())
            foreach (var library in target.Value.EnumerateObject())
            {
                if (!library.Value.TryGetProperty("runtime", out var runtime) ||
                    runtime.ValueKind != System.Text.Json.JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var asset in runtime.EnumerateObject())
                {
                    var name = Path.GetFileName(asset.Name);
                    if (name.Length > 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        names.Add(name);
                    }
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // A broken manifest: the DLL and the manifest itself are still worth keeping.
        }

        return names;
    }
}

public sealed class PluginInfoViewModel
{
    public PluginInfoViewModel(string id, string name, string version, string details, string type)
    {
        Id = id;
        Name = name;
        Version = version;
        Details = details;
        Type = type;
    }

    public string Id { get; }
    public string Name { get; }
    public string Version { get; }
    public string Details { get; }
    public string Type { get; }
}
