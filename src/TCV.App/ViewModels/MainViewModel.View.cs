using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TCV.App.Columns;
using TCV.App.Localization;
using TCV.App.Settings;
using TCV.App.ViewModels.Dialogs;
using TCV.App.Views.Dialogs;

namespace TCV.App.ViewModels;

/// <summary>
/// The view-and-configuration half of <see cref="MainViewModel"/>: the details-view columns
/// (6.19), the gitignore display filter and the settings bundle (6.20). Split out for the same
/// reason as the git half — the class is already the largest in the app
/// ([TRAP-17](../../../CLAUDE.md)).
/// </summary>
public partial class MainViewModel
{
    // ── Columns (6.19) ────────────────────────────────────────────────────────

    /// <summary>Ctrl+K Ctrl+C — choose which columns the active panel's details view shows.</summary>
    [RelayCommand]
    private void OpenColumnSettings()
    {
        var fileList = ActivePanel.FileList;
        var vm = new ColumnsDialogViewModel(fileList.Columns);
        var dlg = new ColumnsDialog(vm) { Owner = Application.Current.MainWindow };

        if (dlg.ShowDialog() != true) return;

        // Both panels share one layout: two file lists showing different columns reads as a bug,
        // and every other view setting in this app is per panel only because it has to be.
        LeftPanel.FileList.Columns = vm.Result;
        RightPanel.FileList.Columns = vm.Result;
    }

    // ── View modes ────────────────────────────────────────────────────────────

    /// <summary>
    /// Ctrl+F1 Brief, Ctrl+Shift+F2 Details, Ctrl+Shift+F1 Thumbnails, Ctrl+F8 Tree — how the active
    /// panel lists its files, the same switch as the buttons above the list. The keys are Total
    /// Commander's, except Details: its Ctrl+F2 already runs a gadget. The panel remembers the mode.
    /// </summary>
    [RelayCommand]
    private void SetViewMode(ViewMode mode) => ActivePanel.FileList.ViewMode = mode;

    // ── Settings dialog (live effects) ────────────────────────────────────────

    /// <summary>
    /// Puts what the Settings dialog just saved into effect in the running window: the theme
    /// (including "follow Windows") with the accent, panel font and chrome laid over it, the
    /// workspace sidebar, the colour rules, and the two file-list options — each of the latter only
    /// when the dialog changed it, so a panel toggled from its toolbar is not silently reset to a
    /// stale saved value.
    /// </summary>
    internal void ApplyChangedSettings(bool hiddenFilesChanged, bool foldersFirstChanged)
    {
        ApplyAppearance();
        Workspaces.ShowWorkspaceSidebar = _settings.ShowWorkspaceSidebar;

        foreach (var (panel, saved) in new[] { (LeftPanel, _settings.LeftPanel), (RightPanel, _settings.RightPanel) })
        {
            // The colour rules are a new list after every save; panels hold the old one.
            panel.FileList.ColorRules = _settings.ColorRules;
            panel.FileList.QuickSearchJumps = _settings.QuickSearchJumps;

            if (hiddenFilesChanged)
            {
                panel.FileList.ShowHiddenFiles = saved.ShowHiddenFiles;
            }

            if (foldersFirstChanged)
            {
                panel.FileList.FoldersFirst = saved.FoldersFirst;
            }

            // Rows are built with their colour at listing time.
            _ = panel.FileList.LoadAsync(panel.CurrentPath);
        }
    }

    /// <summary>
    /// Re-applies the theme with the appearance settings over it (6.33). Everything else follows
    /// through DynamicResource; the active panel's frame is a brush the view model looked up, so it
    /// is asked to look again.
    /// </summary>
    private void ApplyAppearance()
    {
        _themeManager.ApplyFromSettings();
        LeftPanel.RefreshFrame();
        RightPanel.RefreshFrame();
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(ToneBrush));
    }

    // ── Gitignore-aware display (6.20) ────────────────────────────────────────

    /// <summary>Ctrl+Shift+H — hide or show the files git ignores, in the active panel.</summary>
    [RelayCommand]
    private async Task ToggleIgnored()
    {
        var fileList = ActivePanel.FileList;
        // Awaited, not a plain property assignment: the rules are read off the UI thread, and
        // IsInRepository still describes the previous directory until that finishes.
        await fileList.ToggleHideIgnoredAsync();

        // Outside a working copy the switch has nothing to act on; say so rather than let the
        // user conclude the command is broken.
        if (fileList.HideIgnored && !fileList.IsInRepository)
        {
            await _dialogs.ShowInfoAsync(
                LocalizationManager.Get("Ignored_Title"),
                LocalizationManager.Get("Ignored_NoRepo"));
        }
    }

    // ── How the active panel lists its rows (6.77) ────────────────────────────
    // The details view also has these in its column-header menu; Brief and Thumbnails have no header,
    // so the commands (menu, palette, keys) are what reaches them in every view.

    /// <summary>Ctrl+H — show or hide hidden and system files in the active panel.</summary>
    [RelayCommand]
    private void ToggleHiddenFiles() => ActivePanel.FileList.ShowHiddenFiles = !ActivePanel.FileList.ShowHiddenFiles;

    /// <summary>Show file names with or without their extensions in the active panel.</summary>
    [RelayCommand]
    private void ToggleExtensions() => ActivePanel.FileList.ShowExtensions = !ActivePanel.FileList.ShowExtensions;

    /// <summary>Folders above files, or mixed in by the sort order, in the active panel.</summary>
    [RelayCommand]
    private void ToggleFoldersFirst() => ActivePanel.FileList.FoldersFirst = !ActivePanel.FileList.FoldersFirst;

    // ── Settings bundle (6.20) ────────────────────────────────────────────────

    private const string BundleExtension = ".tcvsettings";

    /// <summary>Ctrl+K Ctrl+E — write every setting, workspace and search preset to one file.</summary>
    [RelayCommand]
    private async Task ExportSettings()
    {
        var dialog = new SaveFileDialog
        {
            Title = LocalizationManager.Get("Bundle_ExportTitle"),
            Filter = LocalizationManager.Get("Bundle_Filter"),
            FileName = "tcv-settings" + BundleExtension,
            DefaultExt = BundleExtension,
            AddExtension = true,
        };
        if (dialog.ShowDialog() != true) return;

        // The in-memory settings are the source of truth mid-session: panel paths, columns and
        // the sidebar flag are only written to settings.json on exit. PersistState, not SaveState —
        // an export must not tear down the window's remote-edit sessions.
        PersistState();

        // Only the fields an import applies (CopySettings). The rest of AppSettings — the LLM API key,
        // approved agent folders, MCP flags — is machine-local and secret, and this file is meant
        // to be committed to a repository.
        var exported = new AppSettings();
        CopySettings(_settings, exported);
        // The tools live in the plugin's own store, not in AppSettings: copied from there, or the
        // bundle carried an empty list and an import restored none of them.
        exported.UserTools = Services.UserToolsForExport();

        var bundle = new SettingsBundle
        {
            ExportedUtc = DateTime.UtcNow,
            WrittenBy = typeof(MainViewModel).Assembly.GetName().Version?.ToString() ?? string.Empty,
            Settings = exported,
            Workspaces = Workspaces.Store.LoadAll().ToList(),
            SearchPresets = _searchPresets.LoadAll().ToList(),
        };

        if (bundle.Write(dialog.FileName))
        {
            await _dialogs.ShowInfoAsync(
                LocalizationManager.Get("Bundle_ExportTitle"),
                string.Format(LocalizationManager.Get("Bundle_Exported"), dialog.FileName));
        }
        else
        {
            await _dialogs.ShowErrorAsync(
                LocalizationManager.Get("Bundle_ExportTitle"),
                string.Format(LocalizationManager.Get("Bundle_WriteFailed"), dialog.FileName));
        }
    }

    /// <summary>Ctrl+K Ctrl+M — replace the current configuration with a previously exported one.</summary>
    [RelayCommand]
    private async Task ImportSettings()
    {
        var dialog = new OpenFileDialog
        {
            Title = LocalizationManager.Get("Bundle_ImportTitle"),
            Filter = LocalizationManager.Get("Bundle_Filter"),
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;

        var bundle = SettingsBundle.Read(dialog.FileName);
        if (bundle is null)
        {
            await _dialogs.ShowErrorAsync(
                LocalizationManager.Get("Bundle_ImportTitle"),
                string.Format(LocalizationManager.Get("Bundle_NotABundle"), Path.GetFileName(dialog.FileName)));
            return;
        }

        // Importing overwrites hotkeys, colour rules and tools wholesale — an unusually
        // destructive thing for a menu item to do, so it is confirmed (INV-11).
        var confirmed = await _dialogs.ConfirmAsync(
            LocalizationManager.Get("Bundle_ImportTitle"),
            string.Format(LocalizationManager.Get("Bundle_ImportConfirm"), DescribeBundle(bundle)));
        if (!confirmed) return;

        ApplyBundle(bundle);

        await _dialogs.ShowInfoAsync(
            LocalizationManager.Get("Bundle_ImportTitle"),
            LocalizationManager.Get("Bundle_Imported"));
    }

    /// <summary>
    /// Applies an imported bundle to the running session. Everything that can take effect without
    /// a restart does so immediately; the rest lands in <c>settings.json</c> for the next launch.
    /// </summary>
    private void ApplyBundle(SettingsBundle bundle)
    {
        if (bundle.Settings is { } imported)
        {
            CopySettings(imported, _settings);

            _hotkeyManager.Reload();
            ApplyAppearance();
            LocalizationManager.SetLanguage(_settings.Theme.Language);
            _ = RefreshShellAsync();

            SplitVertical = _settings.SplitVertical;
            Workspaces.ShowWorkspaceSidebar = _settings.ShowWorkspaceSidebar;

            Bookmarks.Clear();
            foreach (var bookmark in _settings.Bookmarks) Bookmarks.Add(bookmark);

            LeftPanel.FileList.ApplySettings(_settings.LeftPanel);
            RightPanel.FileList.ApplySettings(_settings.RightPanel);
            LeftPanel.FileList.ColorRules = _settings.ColorRules;
            RightPanel.FileList.ColorRules = _settings.ColorRules;
            LeftPanel.FileList.QuickSearchJumps = _settings.QuickSearchJumps;
            RightPanel.FileList.QuickSearchJumps = _settings.QuickSearchJumps;

            if (!IsSecondaryWindow) _serializer.Save(_settings);

            // A bundle written before the tools travelled has an empty list; that is "not in the
            // file", not "delete my tools". A guest window writes no shared settings (TRAP-22).
            if (imported.UserTools is { Count: > 0 } tools && !IsSecondaryWindow)
            {
                Services.ImportUserTools(tools);
            }
        }

        if (bundle.Workspaces is { } workspaces)
        {
            // Per-entry saves, not a whole-file rewrite: a second window may own the same file
            // (TRAP-22).
            Workspaces.Store.SaveImported(workspaces);
            Workspaces.Refresh();
            // The bundle may have given the active workspace another icon or colour: the title bar,
            // the caption and the tone are pushed state and do not follow the reload by themselves.
            Workspaces.ApplyAppearance();
        }

        if (bundle.SearchPresets is { } presets)
            foreach (var preset in presets) _searchPresets.Save(preset);
    }

    /// <summary>
    /// Copies a bundle's settings over the live instance field by field. The object itself cannot
    /// be swapped: every panel, the hotkey manager and the settings dialog already hold a
    /// reference to it, and replacing it would leave them writing to an orphan.
    /// </summary>
    private static void CopySettings(AppSettings source, AppSettings target)
    {
        target.LeftPanel = source.LeftPanel;
        target.RightPanel = source.RightPanel;
        target.Operations = source.Operations;
        target.Theme = source.Theme;
        target.Layout = source.Layout;
        target.Hotkeys = source.Hotkeys;
        target.ColorRules = source.ColorRules;
        target.SplitVertical = source.SplitVertical;
        target.ShowWorkspaceSidebar = source.ShowWorkspaceSidebar;
        target.QuickSearchJumps = source.QuickSearchJumps;
        target.Bookmarks = source.Bookmarks;
        target.Updates = source.Updates;
        target.ExternalTools = source.ExternalTools;
        target.Shells = source.Shells;
        target.Git = source.Git;
        target.Gadgets = source.Gadgets;
    }

    private static string DescribeBundle(SettingsBundle bundle)
    {
        var parts = new List<string>();
        if (bundle.Settings is not null) parts.Add(LocalizationManager.Get("Bundle_PartSettings"));
        if (bundle.Workspaces is { Count: > 0 } w) parts.Add($"{LocalizationManager.Get("Bundle_PartWorkspaces")}: {w.Count}");
        if (bundle.SearchPresets is { Count: > 0 } p) parts.Add($"{LocalizationManager.Get("Bundle_PartPresets")}: {p.Count}");
        return string.Join(", ", parts);
    }
}
