using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Commands;
using TCV.App.Localization;
using TCV.App.Selection;
using TCV.App.Settings;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.ViewModels;

/// <summary>One selection template in the «Mark → Templates» submenu.</summary>
public sealed record SelectionTemplateMenuEntry(SelectionTemplate Template, string Header, string Gesture);

/// <summary>
/// Marking by mask and template (Num+, Num-), by the cursor's extension (Alt+Num+, Alt+Num-), and
/// bringing marks back (Num/). The templates live in <see cref="AppSettings.SelectionTemplates"/>; each
/// is also a palette command <c>SelectTemplate:&lt;key&gt;</c> with an optional gesture of its own.
/// </summary>
public sealed partial class FileCommands
{
    public const string SelectionTemplateCommandPrefix = "SelectTemplate:";

    private HotkeyManager? _hotkeys;
    private Action? _saveSettings;

    public ObservableCollection<SelectionTemplateMenuEntry> SelectionTemplateMenuEntries { get; } = new();

    /// <summary>
    /// Hooks the templates up to the gestures and to <c>settings.json</c>. A workspace window does not
    /// write it (TRAP-22), so there the dialog offers no editing and <paramref name="saveSettings"/> is
    /// never called; the shell is asked at the time, as a window can be downgraded to a primary one.
    /// </summary>
    public void AttachSelectionTemplates(HotkeyManager hotkeys, Action saveSettings)
    {
        _hotkeys = hotkeys;
        _saveSettings = saveSettings;
        hotkeys.BindingsChanged += (_, _) => RebuildSelectionTemplateMenu();
        LocalizationManager.LanguageChanged += (_, _) => RebuildSelectionTemplateMenu();
        RefreshSelectionTemplates();
    }

    // Num+ — Select by mask or template (TC's "Select group")
    [RelayCommand]
    private Task SelectByMask() => ShowSelectByMaskDialogAsync(select: true);

    // Num- — Unselect by mask or template (TC's "Unselect group")
    [RelayCommand]
    private Task UnselectByMask() => ShowSelectByMaskDialogAsync(select: false);

    private async Task ShowSelectByMaskDialogAsync(bool select)
    {
        var vm = new SelectByMaskDialogViewModel(
            select,
            _settings.SelectionTemplates,
            _settings.SelectionMaskHistory.ToList(),
            _settings.SelectionIncludeFolders,
            OnSelectionTemplatesEdited,
            canEditTemplates: !_shell.IsSecondaryWindow);
        if (!await _dialogs.ShowSelectByMaskAsync(vm))
        {
            return;
        }

        var template = vm.BuildTemplate();
        SelectionHistory.Push(_settings.SelectionMaskHistory, template.Mask);
        _settings.SelectionIncludeFolders = template.IncludeFolders;
        await ApplySelectionTemplateAsync(template, select);
    }

    /// <summary>Marks what a template from the menu or the palette names.</summary>
    [RelayCommand]
    private Task ApplySelectionTemplate(SelectionTemplate? template) =>
        template is null ? Task.CompletedTask : ApplySelectionTemplateAsync(template, select: true);

    private async Task ApplySelectionTemplateAsync(SelectionTemplate template, bool select)
    {
        var list = ActivePanel.FileList;

        // Who changed what is known only for the disk: in an archive or on a server the template would
        // silently mark nothing, which reads as "nothing changed".
        if (template.NeedsDisk && !list.EntriesOnDisk)
        {
            _shell.ShowNotice(LocalizationManager.Get("Sel_NeedsDisk"));
            return;
        }

        var named = await list.SelectByTemplateAsync(template, select);
        if (named == 0)
        {
            _shell.ShowNotice(LocalizationManager.Get("Sel_NothingMatched"));
        }
    }

    // Alt+Num+ — Mark every file with the cursor's extension (TC)
    [RelayCommand]
    private void SelectSameExtension() => SameExtension(select: true);

    // Alt+Num- — Unmark every file with the cursor's extension (TC)
    [RelayCommand]
    private void UnselectSameExtension() => SameExtension(select: false);

    private void SameExtension(bool select)
    {
        if (!ActivePanel.FileList.SelectSameExtension(select))
        {
            _shell.ShowNotice(LocalizationManager.Get("Sel_NoExtensionHere"));
        }
    }

    // Num/ — Mark again what was last unmarked in bulk (TC's "Restore selection")
    [RelayCommand]
    private void RestoreSelection()
    {
        var list = ActivePanel.FileList;
        if (!list.CanRestoreSelection)
        {
            _shell.ShowNotice(LocalizationManager.Get("Sel_NothingToRestore"));
            return;
        }

        if (list.RestoreSelection() == 0)
        {
            _shell.ShowNotice(LocalizationManager.Get("Sel_RestoreNoneHere"));
        }
    }

    /// <summary>Palette rows for the templates, after the static catalogue.</summary>
    public IReadOnlyList<CommandPaletteItem> SelectionTemplatePaletteItems()
    {
        var category = LocalizationManager.Get("Palette_SelectionTemplates");
        return _settings.SelectionTemplates
            .Where(t => t.Key.Length > 0)
            .Select(t => new CommandPaletteItem(
                CommandId(t),
                LocalizationManager.Format("Sel_PaletteTitle", SelectByMaskDialogViewModel.DisplayName(t)),
                category,
                GestureText.Format(_hotkeys?.GetBinding(CommandId(t)))))
            .ToList();
    }

    /// <summary>Resolves a <c>SelectTemplate:…</c> id and marks by it; false when no such template exists.</summary>
    public bool TryRunSelectionTemplate(string commandId)
    {
        if (!commandId.StartsWith(SelectionTemplateCommandPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var key = commandId[SelectionTemplateCommandPrefix.Length..];
        var template = _settings.SelectionTemplates.FirstOrDefault(
            t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));
        if (template is null)
        {
            return false;
        }

        ApplySelectionTemplateCommand.Execute(template);
        return true;
    }

    /// <summary>Rebinds the templates' gestures and rebuilds their submenu.</summary>
    public void RefreshSelectionTemplates()
    {
        _hotkeys?.SetSelectionTemplateBindings(
            _settings.SelectionTemplates
                .Where(t => t.Key.Length > 0 && !string.IsNullOrWhiteSpace(t.Hotkey))
                .Select(t => (CommandId(t), t.Hotkey!)));
        RebuildSelectionTemplateMenu();
    }

    private void OnSelectionTemplatesEdited()
    {
        if (!_shell.IsSecondaryWindow)
        {
            _saveSettings?.Invoke();
        }
        RefreshSelectionTemplates();
    }

    private void RebuildSelectionTemplateMenu()
    {
        SelectionTemplateMenuEntries.Clear();
        foreach (var t in _settings.SelectionTemplates)
        {
            if (t.Key.Length == 0)
            {
                continue;
            }

            SelectionTemplateMenuEntries.Add(new SelectionTemplateMenuEntry(
                t,
                Controls.MenuHeaderText.Literal(SelectByMaskDialogViewModel.DisplayName(t)),
                GestureText.Format(_hotkeys?.GetBinding(CommandId(t)))));
        }
    }

    private static string CommandId(SelectionTemplate template) => SelectionTemplateCommandPrefix + template.Key;
}
