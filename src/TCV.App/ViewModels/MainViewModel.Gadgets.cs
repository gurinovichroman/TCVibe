using CommunityToolkit.Mvvm.Input;
using TCV.App.Commands;
using TCV.App.FileSystem;
using TCV.App.Localization;
using TCV.App.Settings;
using TCV.App.ViewModels.Dialogs;
using TCV.Core.Domain;

namespace TCV.App.ViewModels;

/// <summary>
/// Gadgets (6.24): user-defined command lines reachable by name from a fuzzy picker, without
/// writing a plugin. The launch itself reuses <see cref="Services.ExternalToolService"/> — a gadget
/// is the same "start a program with macros" problem Ctrl+E already solved, aimed at a whole
/// command rather than at an editor.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Runs a gadget chosen from the picker; offers to create one when none exist.</summary>
    [RelayCommand]
    private async Task RunGadget()
    {
        var gadgets = _settings.Gadgets;
        if (gadgets.Count == 0)
        {
            await AddGadgetAsync();
            return;
        }

        var lang = LocalizationManager.CurrentLanguage;
        var items = gadgets
            .Select(g => new CommandPaletteItem(g.Name, g.Name, DescribeGadget(g), string.Empty))
            .ToList();

        var vm = new CommandPaletteViewModel(items, lang,
            LocalizationManager.Get("Gadget_Title"),
            LocalizationManager.Get("Gadget_Placeholder"))
        {
            // The picker only runs; this says where adding, editing and deleting live (6.50).
            Status = LocalizationManager.Get("Gadget_ManageHint"),
        };
        if (await _dialogs.PickFromPaletteAsync(vm) is not { } chosen)
        {
            return;
        }

        var gadget = gadgets.FirstOrDefault(g =>
            string.Equals(g.Name, chosen.Id, StringComparison.OrdinalIgnoreCase));
        if (gadget is not null) await LaunchGadgetAsync(gadget);
    }

    /// <summary>
    /// Defines the first gadget from a single line: <c>name = command args</c>. Adding more,
    /// editing and deleting is the Gadgets tab of the settings («Manage gadgets…»).
    /// </summary>
    private async Task AddGadgetAsync()
    {
        var line = await _dialogs.ShowInputAsync(
            LocalizationManager.Get("Gadget_Title"),
            LocalizationManager.Get("Gadget_AddPrompt"),
            "build = dotnet build");
        if (string.IsNullOrWhiteSpace(line)) return;

        // A program path may contain spaces; finding where it ends asks the disk, and a path on a
        // dead share can hang, so the split runs off the UI thread (INV-06).
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? string.Empty)
            .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var gadget = await Task.Run(() => GadgetQuickAdd.Parse(line, System.IO.File.Exists, extensions));
        if (gadget is null)
        {
            await _dialogs.ShowErrorAsync(
                LocalizationManager.Get("Gadget_Title"),
                LocalizationManager.Get("Gadget_BadSyntax"));
            return;
        }

        _settings.Gadgets.RemoveAll(g => string.Equals(g.Name, gadget.Name, StringComparison.OrdinalIgnoreCase));
        _settings.Gadgets.Add(gadget);
        if (!IsSecondaryWindow) _serializer.Save(_settings);   // TRAP-22

        await LaunchGadgetAsync(gadget);
    }

    /// <summary>
    /// Starts a gadget in the active panel's directory (or its own), passing the selection as
    /// <c>%F</c>. Non-blocking by construction: the process is started and forgotten.
    /// </summary>
    private async Task LaunchGadgetAsync(Gadget gadget)
    {
        // The macros expand to real Windows paths, which an archive or a server does not have
        // (TRAP-02); search hits do, and their list works in the folder it was opened over (TRAP-28).
        if (await RejectIfNotOnDiskAsync(LocalizationManager.Get("Gadget_Title")))
        {
            return;
        }

        var directory = string.IsNullOrWhiteSpace(gadget.WorkingDirectory)
            ? ActivePanel.FileList.WorkingFolder ?? ActivePanel.CurrentPath
            : gadget.WorkingDirectory;

        var selected = GetSelectedOrCurrent()
            .Select(e => WindowsFileSystem.ToWindowsPath(e.Path))
            .ToList();

        var tool = new ExternalTool
        {
            Name = gadget.Name,
            Command = gadget.Command,
            Arguments = gadget.Arguments,
            InTerminal = gadget.InTerminal,
        };

        // Resolving the command probes PATH, which can hang on a dead share: off the UI thread (INV-06).
        var (started, refused) = await Task.Run(() =>
        {
            var ok = _externalTools.Launch(tool, directory, selected, out var reason);
            return (ok, reason);
        });

        if (!started)
        {
            await _dialogs.ShowErrorAsync(
                LocalizationManager.Get("Gadget_Title"),
                refused is null
                    ? string.Format(LocalizationManager.Get("Gadget_LaunchFailed"), gadget.Name, gadget.Command)
                    : string.Format(LocalizationManager.Get("Tool_UnsafeArgs"), gadget.Name, refused));
        }
    }

    /// <summary>The command line as the picker shows it; a program path with spaces is quoted, so
    /// the reader can tell where it ends.</summary>
    private static string DescribeGadget(Gadget gadget)
    {
        var command = ShellQuoting.QuoteIfNeeded(gadget.Command);
        return string.IsNullOrEmpty(gadget.Arguments) ? command : $"{command} {gadget.Arguments}";
    }
}
