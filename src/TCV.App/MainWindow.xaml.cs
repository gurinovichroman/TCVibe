using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TCV.App.Commands;
using TCV.App.Controls;
using TCV.App.Help;
using TCV.App.Localization;
using TCV.App.ViewModels;
using TCV.App.ViewModels.Dialogs;
using TCV.App.Views.Dialogs;

namespace TCV.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly Settings.HotkeyManager _hotkeys;
    private readonly FileSystem.DriveManager _drives;
    private readonly Settings.AppSettings _settings;

    public MainWindow(MainViewModel viewModel, Settings.HotkeyManager hotkeys, FileSystem.DriveManager drives,
        Settings.AppSettings settings)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;
        _hotkeys = hotkeys;
        _drives = drives;
        _settings = settings;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.ExitRequested += (_, _) => Close();
        SourceInitialized += (_, _) => WatchDrives();
        InitializePlacement();
        InitializeChrome();
        InitializeSnapLayouts();
        Loaded += (_, _) =>
        {
            ApplySplitOrientation(_viewModel.SplitVertical);
            ApplyFolderTreeColumn();
        };

        // The shortcut text in the menus and on the F-key bar is read from the live bindings, now
        // and after every rebind (INV-05), instead of being typed into the XAML.
        RefreshGestureText();
        _hotkeys.BindingsChanged += (_, _) => RefreshGestureText();
        // A pending chord must not outlive the moment: leaving the window or clicking anywhere
        // abandons it, so the first key typed afterwards is not swallowed.
        Deactivated += (_, _) => AbandonChord();
        PreviewMouseDown += (_, _) => AbandonChord();
    }

    /// <summary>
    /// Keeps the drive lists current (6.52): Windows tells top-level windows when a volume arrives or
    /// leaves, and the drive manager also notices letters mapped from a console. Both only raise a
    /// notice; the drives are read again off the UI thread (INV-06).
    /// </summary>
    private void WatchDrives()
    {
        if (PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource source)
        {
            source.AddHook(OnWindowMessage);
        }

        _drives.StartMonitoring(TimeSpan.FromSeconds(2));
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmDeviceChange = 0x0219;
        if (msg != WmDeviceChange)
        {
            return IntPtr.Zero;
        }

        // Only arrival and removal carry a DEV_BROADCAST_HDR; its second field is the device type.
        var eventCode = wParam.ToInt64();
        int? deviceType = eventCode is 0x8000 or 0x8004 && lParam != IntPtr.Zero
            ? System.Runtime.InteropServices.Marshal.ReadInt32(lParam, 4)
            : null;
        if (FileSystem.DriveManager.IsVolumeEvent(msg, eventCode, deviceType))
        {
            _drives.NotifyDevicesChanged();
        }

        return IntPtr.Zero;
    }

    private void RefreshGestureText()
    {
        MenuGestures.Refresh(MainMenu, _hotkeys);
        FunctionKeyBar.Refresh(_hotkeys);
    }

    /// <summary>The live shortcut text for a command, for menus built in code (the mounted-panel context menu).</summary>
    public string GestureFor(string commandId) => Settings.GestureText.Format(_hotkeys.GetBinding(commandId));

    /// <summary>Runs a command by id on behalf of a button (the F-key bar). Explicit clicks ignore the typing guard.</summary>
    public void RunCommand(string commandId) => TryExecuteCommand(commandId, bypassFocusGuard: true);

    private void OnMenuPalette(object sender, System.Windows.RoutedEventArgs e) => OpenCommandPalette();

    private void OnMenuHelp(object sender, System.Windows.RoutedEventArgs e) => OpenHelp();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SplitVertical))
        {
            ApplySplitOrientation(_viewModel.SplitVertical);
        }
        else if (e.PropertyName == nameof(MainViewModel.ShowFolderTree))
        {
            ApplyFolderTreeColumn();
        }
    }

    private void ApplySplitOrientation(bool vertical)
    {
        PanelGrid.ColumnDefinitions.Clear();
        PanelGrid.RowDefinitions.Clear();

        if (vertical)
        {
            PanelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            PanelGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 100 });
            PanelGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(5) });
            PanelGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 100 });

            Grid.SetColumn(LeftPanelView, 0);  Grid.SetRow(LeftPanelView, 0);
            Grid.SetColumn(PanelSplitter, 0);  Grid.SetRow(PanelSplitter, 1);
            Grid.SetColumn(RightPanelView, 0); Grid.SetRow(RightPanelView, 2);

            PanelSplitter.Width = double.NaN;
            PanelSplitter.Height = 5;
            PanelSplitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            PanelSplitter.VerticalAlignment = VerticalAlignment.Stretch;
            PanelSplitter.ResizeDirection = GridResizeDirection.Rows;
            PanelSplitter.Cursor = Cursors.SizeNS;
        }
        else
        {
            PanelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 200 });
            PanelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
            PanelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 200 });
            PanelGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Grid.SetColumn(LeftPanelView, 0);  Grid.SetRow(LeftPanelView, 0);
            Grid.SetColumn(PanelSplitter, 1);  Grid.SetRow(PanelSplitter, 0);
            Grid.SetColumn(RightPanelView, 2); Grid.SetRow(RightPanelView, 0);

            PanelSplitter.Width = 5;
            PanelSplitter.Height = double.NaN;
            PanelSplitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            PanelSplitter.VerticalAlignment = VerticalAlignment.Stretch;
            PanelSplitter.ResizeDirection = GridResizeDirection.Columns;
            PanelSplitter.Cursor = Cursors.SizeWE;
        }
    }

    // Rebuild the dynamic bookmark items each time the menu opens
    private void OnBookmarksMenuOpened(object sender, System.Windows.RoutedEventArgs e)
    {
        // SubmenuOpened bubbles (TRAP-23): opening a group's submenu raised it here, and the rebuild
        // destroyed that submenu as it opened — grouped bookmarks could not be reached.
        if (!ReferenceEquals(e.OriginalSource, BookmarksMenu))
        {
            return;
        }

        // Remove old dynamic items (after the fixed Separator at index 2)
        while (BookmarksMenu.Items.Count > 3)
            BookmarksMenu.Items.RemoveAt(3);

        var bookmarks = _viewModel.Bookmarks;
        if (bookmarks.Count == 0) return;

        // Group bookmarks
        var grouped = bookmarks.GroupBy(b => b.Group ?? string.Empty).OrderBy(g => g.Key);
        foreach (var grp in grouped)
        {
            if (!string.IsNullOrEmpty(grp.Key))
            {
                var groupItem = new System.Windows.Controls.MenuItem
                {
                    Header     = MenuHeaderText.Literal(grp.Key),
                    IsEnabled  = true,
                    Background = System.Windows.Media.Brushes.Transparent,
                };

                foreach (var b in grp)
                {
                    var sub = CreateBookmarkMenuItem(b);
                    groupItem.Items.Add(sub);
                }

                BookmarksMenu.Items.Add(groupItem);
            }
            else
            {
                foreach (var b in grp)
                    BookmarksMenu.Items.Add(CreateBookmarkMenuItem(b));
            }
        }
    }

    private System.Windows.Controls.MenuItem CreateBookmarkMenuItem(Settings.Bookmark b)
    {
        var item = new System.Windows.Controls.MenuItem
        {
            Header      = MenuHeaderText.Literal(b.Name),
            ToolTip     = b.Path,
            Command     = _viewModel.Services.NavigateToBookmarkCommand,
            CommandParameter = b.Path,
        };
        return item;
    }

    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // A running copy or transfer is cut off by closing: say so, and let the user stay.
        if (_viewModel.HasRunningWork
            && !ConfirmDialog.Ask(
                LocalizationManager.Get("Close_BusyTitle"),
                LocalizationManager.Get("Close_BusyMessage"),
                LocalizationManager.Get("Close_BusyExit"),
                LocalizationManager.Get("Close_BusyStay"),
                isWarning: true,
                owner: this))
        {
            e.Cancel = true;
            return;
        }

        _viewModel.SaveState();

        // Connections parked in background tabs are closed politely, and shares this window opened
        // are released — after the state is saved, which keeps the disk folders under them.
        _viewModel.Mounts.CloseAll();
    }

    private void OnMenuQuit(object sender, System.Windows.RoutedEventArgs e) => Close();

    // The box belongs to the panel's view, so the menu item reaches it the way the key does.
    private void OnQuickFilterMenuClick(object sender, System.Windows.RoutedEventArgs e) => RunCommand("QuickFilter");

    private Views.Panels.PanelView ActivePanelView() => _viewModel.LeftPanel.IsActive ? LeftPanelView : RightPanelView;

    private void AbandonChord()
    {
        if (_chord.Take(DateTime.UtcNow) is null)
        {
            return;
        }
        _viewModel.ClearNotice();
    }

    /// <summary>
    /// First stroke of a chord (Ctrl+K …) that is waiting for its second key, or null.
    /// </summary>
    private readonly Settings.PendingChord _chord = new();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // A file name being renamed in the list owns the keyboard: Enter takes the name, Esc drops it,
        // and Backspace, Delete, Space, Ctrl+C or a letter are text in the box — not "up a folder",
        // F8, a mark, the panel's clipboard or a quick search. The file list counts as "the list has
        // focus" for the file-list commands below, so the usual text-box guard would not hold here.
        if (Keyboard.FocusedElement is Controls.NameEditBox)
        {
            return;
        }

        // Alt-chords arrive as Key.System with the real key in SystemKey
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // The panel's quick filter box keeps the keys that edit its text and is the list for the rest:
        // Enter opens the file the cursor found, F3 views it, F5 copies it (Total Commander's quick search).
        // Not the second stroke of a chord begun in the box: Ctrl+K Ctrl+C would copy the box's text, and
        // the chord left waiting would swallow the next Enter.
        var inQuickFilter = Keyboard.FocusedElement is Controls.QuickFilterBox;
        if (inQuickFilter
            && _chord.Peek(DateTime.UtcNow) is null
            && Controls.QuickFilterKeys.BelongsToBox(key, Keyboard.Modifiers))
        {
            return;
        }

        // AltGr reports as Ctrl+Alt on international layouts. While the caret is in a text
        // field that combination is a character being typed, not a chord — let it through, or
        // "ś" would open the settings dialog instead of appearing in the box.
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt) && IsFocusInTextEntry())
            return;

        // Delete (and Shift+Delete) erases a character in a rename or breadcrumb box. The command is
        // bound by id, not by key, so it cannot join _fileListCommands without also silencing F8.
        if (key == Key.Delete && IsFocusInTextEntry())
            return;

        // A modifier pressed on its own is not a stroke; swallowing it would cancel every chord
        // the moment the user reaches for the second combination.
        if (IsModifierKey(key)) return;

        // ── Second stroke of a chord (6.20) ───────────────────────────────────
        if (_chord.Take(DateTime.UtcNow) is { } pending)
        {
            _viewModel.ClearNotice();
            var chordId = _hotkeys.GetChordCommandId(pending.Key, pending.Modifiers, key, Keyboard.Modifiers);
            // A key that completes no chord ends the sequence without acting: Ctrl+K Esc is how
            // one backs out, and a stray key must not fall through to its own command either.
            e.Handled = true;
            if (chordId is not null)
            {
                TryExecuteCommand(chordId, bypassFocusGuard: inQuickFilter);
            }

            return;
        }

        // ── First stroke of a chord ───────────────────────────────────────────
        // Not while the caret is in a text box: opening a chord swallows the next keystroke too,
        // so a stray Ctrl+K in a rename or breadcrumb field would eat the character after it.
        if ((inQuickFilter || !IsFocusInTextEntry()) && _hotkeys.IsChordPrefix(key, Keyboard.Modifiers))
        {
            var first = new Settings.KeyStroke(key, Keyboard.Modifiers);
            _chord.Begin(first, DateTime.UtcNow);
            // Say so: the next key is about to be swallowed, and the user should know why.
            _viewModel.ShowNotice(string.Format(LocalizationManager.Get("Chord_Waiting"), first), Settings.PendingChord.DefaultTimeout);
            e.Handled = true;
            return;
        }

        var commandId = _hotkeys.GetCommandId(key, Keyboard.Modifiers);
        if (commandId is null) return;

        // Unknown or context-inapplicable commands fall through to the panel/control handlers
        if (TryExecuteCommand(commandId, bypassFocusGuard: inQuickFilter))
        {
            e.Handled = true;
        }
    }

    private static bool IsModifierKey(Key key) => key
        is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift
        or Key.LWin or Key.RWin
        or Key.System;

    /// <summary>Commands that act on the file list and must not fire while typing elsewhere.</summary>
    private static readonly HashSet<string> _fileListCommands = new(StringComparer.Ordinal)
    {
        "OpenFile", "View", "Edit", "Properties",
        "ClipboardCopy", "ClipboardCut", "ClipboardPaste", "CopyFullPath", "CopyNames", "CopyReadLink",
        "SelectToggle", "SelectToggleSize", "SelectAll", "DeselectAll", "InvertSelection", "CalculateFolderSizes",
        "SelectByMask", "UnselectByMask", "NavigateUp", "GoToRoot", "GoHome",
        // These read the panel's selection or path too, and their gestures (Ctrl+P, Ctrl+E) are
        // ordinary editing keys inside a rename or breadcrumb box.
        "FindFiles", "OpenInEditor", "OpenInEditorPicker",
        // Staging acts on what the panel has selected; Ctrl+Shift+S is "save as" everywhere else,
        // so it must not fire while the caret is in a rename or breadcrumb box.
        "GitStage", "GitUnstage",
    };

    /// <summary>True while the caret sits in a text field, where keystrokes are input, not chords.</summary>
    private static bool IsFocusInTextEntry() =>
        Keyboard.FocusedElement is System.Windows.Controls.TextBox
            or System.Windows.Controls.PasswordBox;

    /// <summary>
    /// Executes a hotkey command id. Returns false when this window does not own the command
    /// (or its context does not apply), leaving the key for FileListControl to handle.
    /// <paramref name="bypassFocusGuard"/> is set when the command was chosen explicitly from the
    /// command palette: it targets the active panel regardless of what holds focus, so the guard
    /// that stops file-list hotkeys from firing while typing elsewhere must not reject it.
    /// </summary>
    private bool TryExecuteCommand(string commandId, bool bypassFocusGuard = false)
    {
        // One check for every file-list command: returning false leaves the key to whatever
        // has focus (the breadcrumb bar, an inline rename box) instead of acting on the panel.
        if (!bypassFocusGuard && _fileListCommands.Contains(commandId) && !IsFocusInsideFileListControl())
            return false;

        var fileList = _viewModel.ActivePanel.FileList;

        switch (commandId)
        {
            // ── File operations ───────────────────────────────────────────────
            case "Copy":             _viewModel.Files.CopyCommand.Execute(null); return true;
            case "Move":             _viewModel.Files.MoveCommand.Execute(null); return true;
            case "NewFolder":        _viewModel.Files.NewFolderCommand.Execute(null); return true;
            case "NewFile":          _viewModel.Files.NewFileCommand.Execute(null); return true;
            case "Delete":           _viewModel.Files.DeleteCommand.Execute(null); return true;
            case "DeletePermanent":  _viewModel.Files.DeletePermanentCommand.Execute(null); return true;
            case "Rename":           _viewModel.Files.RenameCommand.Execute(null); return true;
            case "Quit":             Close(); return true;
            case "Menu":             OpenMenuBar(); return true;

            // ── Archives ──────────────────────────────────────────────────────
            case "Pack":             _viewModel.Files.PackFilesCommand.Execute(null); return true;
            // Guarded variant: a mounted panel on either side is declined before the dialog (INV-07)
            case "Unpack":           _viewModel.Files.UnpackCheckedCommand.Execute(null); return true;

            // ── Open / view / edit ────────────────────────────────────────────
            // Enter opens with the shell; F3 shows the file in the internal viewer (Lister).
            case "OpenFile":           _viewModel.Files.OpenFileCommand.Execute(null); return true;
            case "View":               _viewModel.Files.ViewFileCommand.Execute(null); return true;
            case "Edit":               _viewModel.Files.EditFileCommand.Execute(null); return true;

            // ── Navigation & panels ───────────────────────────────────────────
            case "SwitchPanel":
                if (!bypassFocusGuard && IsFocusInsideBreadcrumbBar()) return false;
                _viewModel.SwitchPanelCommand.Execute(null);
                return true;
            case "NavigateUp":
                // While a quick filter is being typed, Backspace corrects it instead of leaving the folder.
                if (fileList.QuickSearchText.Length > 0)
                {
                    fileList.QuickSearchText = fileList.QuickSearchText[..^1];
                    return true;
                }
                fileList.NavigateUpCommand.Execute(null);
                return true;
            case "NavigateBack":     _viewModel.ActivePanel.NavigateBackCommand.Execute(null); return true;
            case "NavigateForward":  _viewModel.ActivePanel.NavigateForwardCommand.Execute(null); return true;
            case "GoToRoot":         _viewModel.GoToRootCommand.Execute(null); return true;
            case "GoHome":           _viewModel.GoHomeCommand.Execute(null); return true;
            case "EditPath":
                // While typing (the path box itself, a rename box) Ctrl+L is left to the text box: it
                // would otherwise throw away what was typed and start over.
                if (!bypassFocusGuard && IsFocusInTextEntry())
                {
                    return false;
                }

                _viewModel.EditPathCommand.Execute(null);
                return true;
            case "ToggleSplit":
                _viewModel.SplitVertical = !_viewModel.SplitVertical;
                return true;
            case "SwapPanels":       _viewModel.SwapPanelsCommand.Execute(null); return true;
            case "ToggleTheme":      _viewModel.ToggleThemeCommand.Execute(null); return true;

            // ── Selection ─────────────────────────────────────────────────────
            // Dispatched here rather than from raw keys in FileListControl, so rebinding
            // these in the hotkey dialog actually takes effect.
            case "SelectToggle":
                // Marks the file under the cursor and moves down one row (Insert, as in TC)
                fileList.ToggleSelectAndAdvance();
                return true;
            case "SelectToggleSize":
                // Space: the same, and a folder it marks is measured (TC)
                fileList.ToggleSelectWithSizeAndAdvance();
                return true;
            case "CalculateFolderSizes": _viewModel.Files.CalculateFolderSizesCommand.Execute(null); return true;
            case "SelectAll":        fileList.SelectAll(); return true;
            case "DeselectAll":      fileList.DeselectAll(); return true;
            case "InvertSelection":  fileList.InvertSelection(); return true;
            case "SelectByMask":     _viewModel.Files.SelectByMaskCommand.Execute(null); return true;
            case "UnselectByMask":   _viewModel.Files.UnselectByMaskCommand.Execute(null); return true;

            // ── Tabs ──────────────────────────────────────────────────────────
            case "NewTab":           _viewModel.ActivePanel.NewTabCommand.Execute(null); return true;
            case "CloseTab":
                // The command refuses a locked tab and the last one; say so instead of doing nothing.
                if (_viewModel.ActivePanel.ActiveTab is { IsLocked: true })
                {
                    _viewModel.ShowNotice(LocalizationManager.Get("Tab_LockedNoClose"), TimeSpan.FromSeconds(4));
                }
                else if (_viewModel.ActivePanel.Tabs.Count <= 1)
                {
                    _viewModel.ShowNotice(LocalizationManager.Get("Tab_LastNoClose"), TimeSpan.FromSeconds(4));
                }
                else
                {
                    _viewModel.ActivePanel.CloseTabCommand.Execute(null);
                }
                return true;
            case "NextTab":          _viewModel.ActivePanel.NextTabCommand.Execute(null); return true;
            case "PreviousTab":      _viewModel.ActivePanel.PreviousTabCommand.Execute(null); return true;
            case "RefreshTab":
                _viewModel.ActivePanel.FileList.RefreshCommand.Execute(null);
                // A checkout made in a terminal or by an agent changes the branch without moving the
                // panel; refreshing is how the user asks to see it.
                _viewModel.ActivePanel.RefreshBranch();
                // The docked tree's open folders are not watched: this is how it catches up (6.52).
                if (_viewModel.ShowFolderTree)
                {
                    _ = _viewModel.RefreshFolderTreeAsync();
                }

                return true;

            // ── Clipboard (only while the file list has focus) ────────────────
            case "ClipboardCopy":    _viewModel.Files.CopyToClipboardCommand.Execute(null); return true;
            case "ClipboardCut":     _viewModel.Files.CutToClipboardCommand.Execute(null); return true;
            case "ClipboardPaste":   _viewModel.Files.PasteFromClipboardCommand.Execute(null); return true;
            case "CopyFullPath":     _viewModel.Files.CopyFullPathToClipboardCommand.Execute(null); return true;
            case "CopyNames":        _viewModel.Files.CopyNamesToClipboardCommand.Execute(null); return true;
            case "CopyReadLink":     _viewModel.Files.CopyReadLinkCommand.Execute(null); return true;

            // ── Tools & dialogs ───────────────────────────────────────────────
            case "Search":           _viewModel.OpenSearchCommand.Execute(null); return true;
            case "MultiRename":      _viewModel.Files.OpenMultiRenameCommand.Execute(null); return true;
            case "Settings":         _viewModel.Services.OpenSettingsCommand.Execute(null); return true;
            case "ConnectionManager": _viewModel.Mounts.OpenConnectionManagerCommand.Execute(null); return true;
            case "Disconnect":       _viewModel.Mounts.DisconnectCommand.Execute(null); return true;
            case "Reconnect":        _viewModel.Mounts.ReconnectCommand.Execute(null); return true;
            case "QuickConnect":     _viewModel.Mounts.QuickConnectCommand.Execute(null); return true;
            case "QueueTransfer":    _viewModel.Mounts.QueueTransferCommand.Execute(null); return true;
            case "TransferQueue":    _viewModel.Mounts.OpenTransferQueueCommand.Execute(null); return true;
            case "Terminal":         _viewModel.Services.OpenTerminalCommand.Execute(null); return true;
            case "ShellsManager":    _viewModel.Services.OpenShellsManagerCommand.Execute(null); return true;
            case "OpenInEditor":     _viewModel.Services.OpenInEditorCommand.Execute(null); return true;
            case "OpenInEditorPicker": _viewModel.Services.OpenInEditorPickerCommand.Execute(null); return true;
            case "RunAgentHere":     _viewModel.Services.RunAgentHereCommand.Execute(null); return true;
            case "RunAgentPicker":   _viewModel.Services.RunAgentPickerCommand.Execute(null); return true;
            case "AddBookmark":      _viewModel.Services.AddBookmarkCommand.Execute(null); return true;
            case "OperationLog":     _viewModel.Services.OpenOperationLogCommand.Execute(null); return true;
            case "BackgroundOperations": _viewModel.Services.ShowBackgroundOperationsCommand.Execute(null); return true;
            case "Properties":       _viewModel.Files.ShowWindowsPropertiesCommand.Execute(null); return true;
            case "CommandPalette":   OpenCommandPalette(); return true;
            case "Help":             OpenHelp(); return true;
            case "FindFiles":        _viewModel.OpenFileFinderCommand.Execute(null); return true;
            case "QuickFilter":      ActivePanelView().OpenQuickFilter(); return true;

            // ── Saved operations, drop stack, gadgets (6.22 / 6.24) ───────────
            case "SavedOperations":  _viewModel.OpenSavedOperationsCommand.Execute(null); return true;
            case "SavedOperationsManager": _viewModel.ManageSavedOperationsCommand.Execute(null); return true;
            case "DropStackAdd":     _viewModel.DropStackAddCommand.Execute(null); return true;
            case "DropStackShow":    _viewModel.DropStackShowCommand.Execute(null); return true;
            case "RunGadget":        _viewModel.RunGadgetCommand.Execute(null); return true;
            case "GadgetsManager":   _viewModel.Services.OpenGadgetsManagerCommand.Execute(null); return true;

            // ── Dev-remote (6.23) ─────────────────────────────────────────────
            case "CompareDirectories": _viewModel.CompareDirectoriesCommand.Execute(null); return true;
            case "EditRemoteFile":     _viewModel.EditRemoteFileCommand.Execute(null); return true;

            // ── Navigation (6.21) ─────────────────────────────────────────────
            case "NameSearch":       _viewModel.OpenNameSearchCommand.Execute(null); return true;
            case "RebuildNameIndex": _viewModel.RebuildNameIndexCommand.Execute(null); return true;
            case "FolderTree":       _viewModel.ToggleFolderTreeCommand.Execute(null); return true;

            // ── View & configuration (6.19 / 6.20) ────────────────────────────
            case "ColumnSettings":   _viewModel.OpenColumnSettingsCommand.Execute(null); return true;
            case "ViewModeDetails":  _viewModel.SetViewModeCommand.Execute(Settings.ViewMode.Details); return true;
            case "ViewModeBrief":    _viewModel.SetViewModeCommand.Execute(Settings.ViewMode.Brief); return true;
            case "ViewModeThumbnails": _viewModel.SetViewModeCommand.Execute(Settings.ViewMode.Thumbnails); return true;
            case "ViewModeTree":     _viewModel.SetViewModeCommand.Execute(Settings.ViewMode.Tree); return true;
            case "ToggleIgnored":    _viewModel.ToggleIgnoredCommand.Execute(null); return true;
            case "ToggleHiddenFiles": _viewModel.ToggleHiddenFilesCommand.Execute(null); return true;
            case "ToggleExtensions": _viewModel.ToggleExtensionsCommand.Execute(null); return true;
            case "ToggleFoldersFirst": _viewModel.ToggleFoldersFirstCommand.Execute(null); return true;
            case "ExportSettings":   _viewModel.ExportSettingsCommand.Execute(null); return true;
            case "ImportSettings":   _viewModel.ImportSettingsCommand.Execute(null); return true;

            // ── Git as a process ──────────────────────────────────────────────
            case "GitDiff":          _viewModel.OpenGitDiffCommand.Execute(null); return true;
            case "GitWorktrees":     _viewModel.OpenWorktreeDashboardCommand.Execute(null); return true;
            case "GitNewWorktree":   _viewModel.NewWorktreeCommand.Execute(null); return true;
            case "GitCommit":        _viewModel.OpenGitCommitCommand.Execute(null); return true;
            case "GitBranch":        _viewModel.OpenGitBranchCommand.Execute(null); return true;
            case "GitStage":         _viewModel.GitStageSelectionCommand.Execute(null); return true;
            case "GitUnstage":       _viewModel.GitUnstageSelectionCommand.Execute(null); return true;
            case "GitStash":         _viewModel.GitStashCommand.Execute(null); return true;
            case "GitStashPop":      _viewModel.GitStashPopCommand.Execute(null); return true;
            case "GitClone":         _viewModel.GitCloneCommand.Execute(null); return true;

            // ── Agent-aware layer (6D) ────────────────────────────────────────
            case "AgentChanges":     _viewModel.OpenAgentChangesCommand.Execute(null); return true;
            case "AgentLaunch":      _viewModel.LaunchAgentCommand.Execute(null); return true;
            case "AiRename":         _viewModel.AiRenameSelectionCommand.Execute(null); return true;
            case "SemanticSearch":   _viewModel.OpenSemanticSearchCommand.Execute(null); return true;

            // ── Workspaces (projects) ─────────────────────────────────────────
            case "WorkspaceSwitcher": OpenWorkspaceSwitcher(); return true;
            case "NewWorkspace":     _viewModel.Workspaces.NewWorkspaceCommand.Execute(null); return true;
            case "SaveWorkspace":    _viewModel.Workspaces.SaveWorkspaceCommand.Execute(null); return true;
            case "WorkspaceNotes":   _viewModel.Workspaces.OpenWorkspaceNotesCommand.Execute(null); return true;
            case "WorkspaceTerminal":_viewModel.Workspaces.RunWorkspaceTerminalCommand.Execute(null); return true;
            case "WorkspaceNewWindow":_viewModel.Workspaces.OpenWorkspaceInNewWindowCommand.Execute(null); return true;
            case "WorkspaceSidebar": _viewModel.Workspaces.ToggleWorkspaceSidebarCommand.Execute(null); return true;
            case "WorkspaceOpenDefinition":   _viewModel.Workspaces.OpenWorkspaceDefinitionCommand.Execute(null); return true;
            case "WorkspaceExportDefinition": _viewModel.Workspaces.ExportWorkspaceDefinitionCommand.Execute(null); return true;
            case "Workspace1":       _viewModel.Workspaces.SwitchToWorkspaceIndex(0); return true;
            case "Workspace2":       _viewModel.Workspaces.SwitchToWorkspaceIndex(1); return true;
            case "Workspace3":       _viewModel.Workspaces.SwitchToWorkspaceIndex(2); return true;
            case "Workspace4":       _viewModel.Workspaces.SwitchToWorkspaceIndex(3); return true;
            case "Workspace5":       _viewModel.Workspaces.SwitchToWorkspaceIndex(4); return true;
            case "Workspace6":       _viewModel.Workspaces.SwitchToWorkspaceIndex(5); return true;
            case "Workspace7":       _viewModel.Workspaces.SwitchToWorkspaceIndex(6); return true;
            case "Workspace8":       _viewModel.Workspaces.SwitchToWorkspaceIndex(7); return true;
            case "Workspace9":       _viewModel.Workspaces.SwitchToWorkspaceIndex(8); return true;
            case "WorkspaceProperties": _viewModel.Workspaces.EditWorkspacePropertiesCommand.Execute(null); return true;
            case "WorkspacePin":     _viewModel.Workspaces.ToggleWorkspacePinnedCommand.Execute(null); return true;
            case "WorkspaceDelete":  _viewModel.Workspaces.DeleteWorkspaceCommand.Execute(null); return true;
            case "DuplicateTab":     _viewModel.ActivePanel.DuplicateTabCommand.Execute(null); return true;
            case "CloseOtherTabs":   _viewModel.ActivePanel.CloseOtherTabsCommand.Execute(null); return true;
            case "ToggleLockTab":    _viewModel.ActivePanel.ToggleLockTabCommand.Execute(null); return true;
            case "RenameTab":        _viewModel.ActivePanel.StartRenameTabCommand.Execute(null); return true;
            case "BookmarksManager": _viewModel.Services.OpenBookmarksManagerCommand.Execute(null); return true;
            case "GoToClipboardPath": _viewModel.GoToClipboardPathCommand.Execute(null); return true;
            case "OpenInExplorer":   _viewModel.OpenInExplorerCommand.Execute(null); return true;
            case "Checksums":        _viewModel.Services.OpenChecksumCommand.Execute(null); return true;
            case "UserToolsManager": _viewModel.Services.OpenUserToolsManagerCommand.Execute(null); return true;
            case "HotkeySettings":   _viewModel.Services.OpenHotkeySettingsCommand.Execute(null); return true;
            case "PluginManager":    _viewModel.Services.OpenPluginManagerCommand.Execute(null); return true;
            case "CheckForUpdates":  _viewModel.Services.CheckForUpdateCommand.Execute(null); return true;
            case "ReportBug":        _viewModel.Services.ReportBugCommand.Execute(null); return true;
            case "Discussions":      _viewModel.Services.OpenDiscussionsCommand.Execute(null); return true;
            case "OpenLogFolder":    _viewModel.Services.OpenLogFolderCommand.Execute(null); return true;

            // Quick search stays with FileListControl: it starts by typing, not by a gesture
            default:
                // The user's own tools are not in the static registry — their ids are built from
                // the tool names (6.29), so they are resolved here rather than as a case above. So are
                // the palette's recent connections (Connection:<id>, 6.82).
                return _viewModel.Services.TryRunUserTool(commandId)
                    || _viewModel.Mounts.TryOpenRecentConnection(commandId);
        }
    }

    /// <summary>F9: opens the first menu, so the whole menu bar is reachable from the keyboard with the arrow keys.</summary>
    private void OpenMenuBar()
    {
        if (MainMenu.Items.Count > 0 && MainMenu.Items[0] is MenuItem first)
        {
            first.Focus();
            first.IsSubmenuOpen = true;
        }
    }

    /// <summary>
    /// Opens the command palette (F1) and, on accept, runs the chosen command through the same
    /// dispatcher as a hotkey. Execution is deferred with <see cref="System.Windows.Threading.Dispatcher"/>
    /// so the palette has fully closed and focus is back on the file list — otherwise focus-guarded
    /// commands (clipboard, selection, open/view) would be rejected by <see cref="TryExecuteCommand"/>.
    /// </summary>
    private void OpenCommandPalette()
    {
        var lang = LocalizationManager.CurrentLanguage;
        // The user's own tools sit at the end: the static catalogue first, then whatever this
        // particular installation added (6.29).
        var items = CommandCatalog
            .BuildItems(_hotkeys, lang)
            .Concat(_viewModel.Services.UserToolPaletteItems(lang))
            .Concat(_viewModel.Mounts.RecentConnectionPaletteItems())
            .ToList();
        var vm = new CommandPaletteViewModel(items, lang);
        var dlg = new CommandPaletteDialog(vm) { Owner = this };

        if (dlg.ShowDialog() == true && vm.SelectedItem is { } chosen)
            Dispatcher.BeginInvoke((Action)(() => TryExecuteCommand(chosen.Id, bypassFocusGuard: true)));
    }

    private HelpDialog? _help;

    /// <summary>
    /// Shift+F1 — the help window: the guide plus every command with its live shortcut, menu path
    /// and description. Modeless, one at a time: asking again brings the open one forward.
    /// </summary>
    private void OpenHelp()
    {
        if (_help is not null)
        {
            _help.Activate();
            return;
        }

        var locations = MenuLocations();
        var commands = HelpCatalog.BuildCommands(
            CommandCatalog.BuildItems(_hotkeys, LocalizationManager.CurrentLanguage),
            HelpString,
            id => locations.TryGetValue(id, out var path)
                ? string.Format(LocalizationManager.Get("Help_InMenu"), path)
                : string.Empty);
        var vm = new HelpDialogViewModel(HelpCatalog.BuildTopics(HelpString), commands, RunFromHelp);

        _help = new HelpDialog(vm) { Owner = this };
        _help.Closed += (_, _) =>
        {
            _help = null;
            // Back to the panels: otherwise the keys typed next can go nowhere until a click.
            Activate();
        };
        _help.Show();
    }

    /// <summary>A help string, or null when the dictionary has no such key (instead of "[key]").</summary>
    private static string? HelpString(string key)
    {
        var text = LocalizationManager.Get(key);
        return text == $"[{key}]" ? null : text;
    }

    /// <summary>"Run" in the help: the command acts on this window, as if picked from the palette.</summary>
    private void RunFromHelp(string commandId)
    {
        Activate();
        // Deferred so focus is back in the file list first (TRAP-16).
        Dispatcher.BeginInvoke(
            (Action)(() => TryExecuteCommand(commandId, bypassFocusGuard: true)),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>
    /// Command id → its place in the menu bar ("Files › Copy"), read from the menus themselves, so
    /// the help names the path the user will actually see, in the current language.
    /// </summary>
    private Dictionary<string, string> MenuLocations()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        Walk(MainMenu, string.Empty);
        return map;

        void Walk(ItemsControl parent, string path)
        {
            foreach (var item in parent.Items.OfType<MenuItem>())
            {
                var header = MenuHeaderText.Plain(item.Header);
                var here = path.Length == 0 ? header : $"{path} › {header}";
                if (MenuGestures.GetCommandId(item) is { } id)
                {
                    map.TryAdd(id, here);
                }

                Walk(item, here);
            }
        }
    }

    /// <summary>
    /// Opens the fuzzy workspace switcher (Alt+W), reusing the command-palette control over a
    /// workspace-derived item list. With nothing saved yet, offers to save the current context.
    /// </summary>
    private void OpenWorkspaceSwitcher()
    {
        if (_viewModel.Workspaces.Items.Count == 0)
        {
            _viewModel.Workspaces.SaveWorkspaceCommand.Execute(null);
            return;
        }

        var lang = LocalizationManager.CurrentLanguage;
        var items = _viewModel.Workspaces.Items.Select((w, i) => new CommandPaletteItem(
            w.Name,
            w.DisplayName,
            WorkspaceSwitcherLabel(w),
            WorkspaceGesture(i))).ToList();

        var title = LocalizationManager.Get("Ws_SwitcherTitle");
        var placeholder = LocalizationManager.Get("Ws_SwitcherPlaceholder");
        var vm = new CommandPaletteViewModel(items, lang, title, placeholder);
        var dlg = new CommandPaletteDialog(vm) { Owner = this };

        if (dlg.ShowDialog() == true && vm.SelectedItem is { } chosen)
            _viewModel.Workspaces.SwitchToWorkspaceByName(chosen.Id);
    }

    /// <summary>The switcher's right-hand label: the group, then "pinned" — the two things that
    /// explain where a workspace sits in the list.</summary>
    private static string WorkspaceSwitcherLabel(Workspaces.Workspace ws)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(ws.Group))
        {
            parts.Add(ws.Group.Trim());
        }

        if (ws.Pinned)
        {
            parts.Add(LocalizationManager.Get("Ws_Pinned"));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>Fixed entries at the top of the Workspaces menu (new / save / switch / notes / terminal +
    /// separator). Everything past them is rebuilt per workspace, so this has to track the XAML.</summary>
    private const int StaticWorkspaceMenuItems = 11;

    // Rebuild the dynamic workspace items each time the menu opens (mirrors the bookmarks menu).
    // Each workspace is a submenu so its management actions (pin, icon, delete) have a home; the
    // Alt+1..9 gesture on the parent still switches to it directly.
    private void OnWorkspacesMenuOpened(object sender, System.Windows.RoutedEventArgs e)
    {
        // SubmenuOpened bubbles: opening a per-workspace submenu raises it on this menu too, and
        // rebuilding then destroys the very MenuItem whose popup is being shown — the pin/icon/
        // colour/notes/delete actions would be unreachable.
        if (!ReferenceEquals(e.OriginalSource, WorkspacesMenu)) return;

        while (WorkspacesMenu.Items.Count > StaticWorkspaceMenuItems)
            WorkspacesMenu.Items.RemoveAt(StaticWorkspaceMenuItems);

        var list = _viewModel.Workspaces.Items;
        for (int i = 0; i < list.Count; i++)
        {
            var ws = list[i];
            var parent = new MenuItem
            {
                Header           = MenuHeaderText.Literal(ws.DisplayName),
                InputGestureText = WorkspaceGesture(i),
            };

            AddWorkspaceActions(parent.Items, ws);
            WorkspacesMenu.Items.Add(parent);
        }
    }

    /// <summary>
    /// One workspace's actions — switch, pin, properties, notes, new window, project file, delete. The
    /// Workspaces menu nests them under the workspace; the rail opens them on a right-click, so both
    /// places offer the same list.
    /// </summary>
    private void AddWorkspaceActions(ItemCollection items, Workspaces.Workspace ws)
    {
        var commands = _viewModel.Workspaces;

        items.Add(new MenuItem
        {
            Header           = LocalizationManager.Get("Menu_Ws_SwitchTo"),
            Command          = commands.SwitchWorkspaceCommand,
            CommandParameter = ws,
        });
        // A verb that names the place, as the taskbar's "Pin to taskbar" does: a "Pinned" checkmark did
        // not say what pinning changes or where to look for it.
        items.Add(new MenuItem
        {
            Header           = LocalizationManager.Get(ws.Pinned ? "Menu_Ws_Unpin" : "Menu_Ws_Pin"),
            ToolTip          = LocalizationManager.Get("WsProps_PinnedHint"),
            Command          = commands.ToggleWorkspacePinnedCommand,
            CommandParameter = ws,
        });
        // Name, icon, colour and group: one window instead of a prompt each.
        items.Add(new MenuItem
        {
            Header           = LocalizationManager.Get("Menu_Ws_Properties"),
            InputGestureText = ws.Name.Equals(commands.ActiveWorkspaceName, StringComparison.OrdinalIgnoreCase)
                               ? GestureFor("WorkspaceProperties") : string.Empty,
            Command          = commands.EditWorkspacePropertiesCommand,
            CommandParameter = ws,
        });
        items.Add(new MenuItem
        {
            // A dot marks workspaces that already have notes — otherwise the only way to find
            // out is to open each one.
            Header           = LocalizationManager.Get("Menu_Ws_NotesItem")
                               + (commands.HasNotes(ws) ? "  •" : string.Empty),
            Command          = commands.OpenWorkspaceNotesCommand,
            CommandParameter = ws,
        });
        items.Add(new MenuItem
        {
            Header           = LocalizationManager.Get("Menu_Ws_NewWindow"),
            Command          = commands.OpenWorkspaceInNewWindowCommand,
            CommandParameter = ws,
        });
        items.Add(new MenuItem
        {
            Header           = LocalizationManager.Get("Menu_Ws_SaveProject"),
            Command          = commands.ExportWorkspaceDefinitionCommand,
            CommandParameter = ws,
        });
        items.Add(new Separator());
        items.Add(new MenuItem
        {
            Header           = LocalizationManager.Get("Menu_Ws_Delete"),
            Command          = commands.DeleteWorkspaceCommand,
            CommandParameter = ws,
        });
    }

    /// <summary>The live shortcut of the n-th workspace (Workspace1..9), empty past the ninth or when unbound.</summary>
    private string WorkspaceGesture(int index) =>
        index < 9 ? TCV.App.Settings.GestureText.Format(_hotkeys.GetBinding($"Workspace{index + 1}")) : string.Empty;

    private void OnSwitchWorkspaceMenu(object sender, System.Windows.RoutedEventArgs e) =>
        OpenWorkspaceSwitcher();

    private static bool IsFocusInsideBreadcrumbBar()
    {
        var focused = Keyboard.FocusedElement as DependencyObject;
        while (focused is not null)
        {
            if (focused is Controls.BreadcrumbBar) return true;
            focused = VisualTreeHelper.GetParent(focused);
        }
        return false;
    }

    private static bool IsFocusInsideFileListControl()
    {
        var focused = Keyboard.FocusedElement as DependencyObject;
        while (focused is not null)
        {
            if (focused is FileListControl) return true;
            focused = VisualTreeHelper.GetParent(focused);
        }
        return false;
    }

    /// <summary>
    /// Docked folder tree (6.21): picking a node navigates the active panel. Expanding a node is
    /// handled by the node itself (lazy children), so only a real selection change lands here — and
    /// the view model tells a pick from the tree following the panel (6.52).
    /// </summary>
    private void OnFolderTreeSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is ViewModels.TreeNodeViewModel node)
        {
            _viewModel.OnFolderTreeNodeSelected(node);
        }
    }

    /// <summary>
    /// A node selected to follow the panel may be below the fold: bring it into view. Its header
    /// only — the whole item of an open folder includes its subtree, and scrolling to that would
    /// jump the tree on every click.
    /// </summary>
    private void OnFolderTreeItemSelected(object sender, RoutedEventArgs e) =>
        FileListControl.BringTreeItemHeaderIntoView(e);

    /// <summary>The node that was selected when the current click began (selection follows the press).</summary>
    private object? _folderTreeSelectedAtPress;

    private void OnFolderTreeMouseDown(object sender, MouseButtonEventArgs e) =>
        _folderTreeSelectedAtPress = FolderTree.SelectedItem;

    /// <summary>
    /// A click on the node that is already selected changes no selection, so the handler above never
    /// heard it: once the panel had moved on, the tree could not take it back to that folder.
    /// </summary>
    private void OnFolderTreeMouseUp(object sender, MouseButtonEventArgs e)
    {
        var item = (e.OriginalSource as DependencyObject) is { } source ? FindAncestor<TreeViewItem>(source) : null;

        // Only a node that was selected before this click: one the press just selected has already
        // navigated through SelectedItemChanged, however long the button was held.
        if (item?.DataContext is ViewModels.TreeNodeViewModel node
            && ReferenceEquals(node, FolderTree.SelectedItem)
            && ReferenceEquals(node, _folderTreeSelectedAtPress))
        {
            _viewModel.NavigateToTreeNode(node);
        }
    }

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null and not T)
        {
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        return element as T;
    }

    /// <summary>The docked tree's width, kept for the session while it is hidden.</summary>
    private double _folderTreeWidth = 194;

    private void ApplyFolderTreeColumn() =>
        FolderTreeColumn.Width = new GridLength(_viewModel.ShowFolderTree ? _folderTreeWidth : 0);

    private void OnFolderTreeSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (_viewModel.ShowFolderTree && FolderTreeColumn.ActualWidth >= 80)
        {
            _folderTreeWidth = FolderTreeColumn.ActualWidth;
        }

        ApplyFolderTreeColumn();
    }

    private void OnLeftPanelMouseDown(object sender, MouseButtonEventArgs e)
    {
        _viewModel.ActivateLeftPanelCommand.Execute(null);
    }

    private void OnRightPanelMouseDown(object sender, MouseButtonEventArgs e)
    {
        _viewModel.ActivateRightPanelCommand.Execute(null);
    }
}
