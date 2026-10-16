using CommunityToolkit.Mvvm.ComponentModel;
using TCV.App.Settings;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.ViewModels;

/// <summary>
/// Renaming in place, as Explorer and Total Commander do it: the name turns into a box in the list
/// itself (<see cref="Controls.NameEditBox"/>). The panel only runs the edit; what the typed name
/// becomes — the queued rename and its errors — is the command's (<see cref="FileCommands"/>).
/// </summary>
public partial class FileListViewModel
{
    private TaskCompletionSource<string?>? _nameEdit;

    // The watcher saw the folder change while a name was being typed. A reload rebuilds the rows and
    // would close the box under the user's fingers, so it waits for the edit to end.
    private bool _reloadAfterNameEdit;

    /// <summary>The entry whose name is being edited in the list, or null.</summary>
    [ObservableProperty]
    private FileEntryViewModel? _nameEditTarget;

    /// <summary>The Tree view shows folder nodes, not the list's rows: there the name is asked in a dialog.</summary>
    public bool CanEditNameInPlace => ViewMode != ViewMode.Tree;

    /// <summary>
    /// Opens the name of <paramref name="item"/> for editing in the list, with <paramref name="text"/>
    /// in the box, and puts the cursor on it. Completes with the name typed (Enter, focus moving away)
    /// or null: Esc, an empty box, a reload, another edit opened.
    /// </summary>
    public Task<string?> EditNameInPlaceAsync(FileEntryViewModel item, string text)
    {
        // A row the list no longer has (a reload replaced it while the shell menu was open) has no box.
        if (!Files.Contains(item))
        {
            EndNameEdit(commit: false);
            return Task.FromResult<string?>(null);
        }

        // A reload put off for the edit this one replaces waits for this one instead: run now, it would
        // land a moment later and close the box just opened.
        var reloadPending = _reloadAfterNameEdit;
        _reloadAfterNameEdit = false;
        EndNameEdit(commit: false);
        _reloadAfterNameEdit = reloadPending;

        // Asynchronous continuations: the command goes on to rename (and to show its errors) after
        // the key that ended the edit has been handled, not inside the box's key handler.
        var edit = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _nameEdit = edit;

        // The row being renamed is the cursor, as in Explorer. The shell menu's Rename can come from
        // a marked file the cursor is not on.
        if (!ReferenceEquals(SelectedItem, item))
        {
            SelectedItem = item;
        }

        item.EditedName = text;
        item.NameEditError = null;
        item.SelectBaseNameOnEdit = true;
        item.IsEditingName = true;
        NameEditTarget = item;
        return edit.Task;
    }

    /// <summary>
    /// Ends the edit, taking the typed name when <paramref name="commit"/> is set. A name Windows refuses
    /// is not taken: the box stays open, says why (<see cref="FileEntryViewModel.NameEditError"/>), and
    /// this returns false. An empty box ends the edit as Esc does.
    /// </summary>
    public bool EndNameEdit(bool commit)
    {
        if (_nameEdit is not { } edit || NameEditTarget is not { } item)
        {
            return true;
        }

        string? typed = null;
        if (commit && !string.IsNullOrWhiteSpace(item.EditedName))
        {
            if (FileNameRules.GetErrorKey(item.EditedName) is { } errorKey)
            {
                item.NameEditError = Localization.LocalizationManager.Get(errorKey);
                return false;
            }

            typed = item.EditedName;
        }

        _nameEdit = null;
        item.IsEditingName = false;
        item.NameEditError = null;
        NameEditTarget = null;
        edit.TrySetResult(typed);

        // A new name is renamed and reloaded by the command; a dropped or unchanged one (Enter on the
        // name as it was) makes the command stop without a reload, so the folder catches up here.
        if (_reloadAfterNameEdit && (typed is null || typed == item.Name))
        {
            _reloadAfterNameEdit = false;
            PrepareForRefresh();
            _ = LoadAsync(CurrentPath);
        }

        return true;
    }

    /// <summary>
    /// A reload the watcher asks for while a name is being edited: true when it was put off until the
    /// edit ends, false when there is no edit and it should run now.
    /// </summary>
    public bool PostponeReloadForNameEdit()
    {
        if (_nameEdit is null)
        {
            return false;
        }

        _reloadAfterNameEdit = true;
        return true;
    }

    /// <summary>
    /// Any other load replaces the rows the edit is on: the edit is dropped, as Explorer drops it on F5.
    /// On the UI thread, where <see cref="LoadAsync"/> starts (it raises <see cref="BeforeDirectoryLoad"/>).
    /// </summary>
    private void DropNameEditForLoad()
    {
        _reloadAfterNameEdit = false;
        EndNameEdit(commit: false);
    }

    /// <summary>
    /// A load's listing has arrived and is about to replace the rows. An edit opened while it was being
    /// read (F2 a moment after a reload began) is dropped here: otherwise its box would vanish with the
    /// old row, and the box reads losing the keys as "taken" — renaming the file to a half-typed name.
    /// A reload put off meanwhile still runs (the listing may predate the change it waited for).
    /// </summary>
    private void DropNameEditBeforeRowsChange()
    {
        if (_nameEdit is not null)
        {
            EndNameEdit(commit: false);
        }
    }
}
