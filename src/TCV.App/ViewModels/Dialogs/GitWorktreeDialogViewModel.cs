using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Localization;
using TCV.Git;

namespace TCV.App.ViewModels.Dialogs;

/// <summary>What the dashboard was closed for. The dialog itself never navigates or creates
/// anything — it reports an intent and lets <c>MainViewModel</c> act, so panel state stays owned by
/// one place.</summary>
public enum GitWorktreeAction
{
    None,
    /// <summary>Show <see cref="GitWorktreeDialogViewModel.ChosenPath"/> in the active panel.</summary>
    Open,
    /// <summary>Run the "new worktree for this task" flow.</summary>
    Create,
}

/// <summary>
/// The worktree dashboard: every checkout of this repository, what it is on, and whether it is safe
/// to leave or remove. Worktrees are the load-bearing piece under running several agents at once —
/// without a list like this, the second checkout is invisible and gets abandoned in a folder
/// nobody remembers creating.
/// </summary>
public partial class GitWorktreeDialogViewModel : ObservableObject
{
    private readonly GitWorktreeService _worktrees;
    private readonly string _anyPathInRepository;

    /// <summary>Confirms a destructive removal. Injected so the model stays free of message boxes —
    /// the dialog supplies the real prompt.</summary>
    private readonly Func<GitWorktreeInfo, bool> _confirmRemove;

    public GitWorktreeDialogViewModel(
        GitWorktreeService worktrees,
        string anyPathInRepository,
        Func<GitWorktreeInfo, bool> confirmRemove)
    {
        _worktrees = worktrees;
        _anyPathInRepository = anyPathInRepository;
        _confirmRemove = confirmRemove;

        WindowTitle = LocalizationManager.Get("Git_WtTitle");
    }

    public string WindowTitle { get; }

    public ObservableCollection<GitWorktreeInfo> Worktrees { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenSelected))]
    [NotifyPropertyChangedFor(nameof(CanRemoveSelected))]
    private GitWorktreeInfo? _selected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemoveSelected))]
    private bool _isBusy;

    /// <summary>True once a worktree was removed, so the caller can move a panel out of a folder
    /// that no longer exists and reload the listings.</summary>
    public bool RemovedAny { get; private set; }

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>Red status text: the last removal failed.</summary>
    [ObservableProperty]
    private bool _isError;

    /// <summary>A missing worktree has no directory to show, so opening it would land nowhere.</summary>
    public bool CanOpenSelected => Selected is { Missing: false, Path.Length: > 0 };

    /// <summary>
    /// The main checkout is the repository — removing it is not a worktree operation. The current one
    /// is refused too: git will not prune a worktree that is in use, and the panel would be left
    /// standing in a directory that is being deleted underneath it. Nothing is offered while a
    /// removal runs: a second one of the same row would only fail confusingly.
    /// </summary>
    public bool CanRemoveSelected => !IsBusy && Selected is { IsMain: false, IsCurrent: false };

    public GitWorktreeAction Action { get; private set; } = GitWorktreeAction.None;
    public string ChosenPath { get; private set; } = string.Empty;

    public bool DialogResult { get; private set; }
    public event EventHandler? CloseRequested;

    /// <summary>Fills the list. Kept off the constructor so the window is up before the status walk
    /// of every worktree runs — on a large repository that is not instant.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        StatusMessage = string.Empty;
        IsError = false;
        try
        {
            var previous = Selected?.Path;
            var list = await _worktrees.ListAsync(_anyPathInRepository);

            Worktrees.Clear();
            foreach (var info in list) Worktrees.Add(info);

            // Keep the cursor where it was across a refresh: after removing one worktree the list
            // shifts, and re-selecting by index would land on an unrelated row.
            Selected = Worktrees.FirstOrDefault(w => w.Path == previous)
                       ?? Worktrees.FirstOrDefault(w => w.IsCurrent)
                       ?? Worktrees.FirstOrDefault();

            if (Worktrees.Count == 0)
                StatusMessage = LocalizationManager.Get("Git_NoRepo");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenSelected()
    {
        if (!CanOpenSelected) return;

        Action = GitWorktreeAction.Open;
        ChosenPath = Selected!.Path;
        Close(true);
    }

    /// <summary>Hands over to the "new worktree" flow, which lives in <c>MainViewModel</c> because it
    /// ends by creating a workspace and moving the panel.</summary>
    [RelayCommand]
    private void CreateWorktree()
    {
        Action = GitWorktreeAction.Create;
        Close(true);
    }

    /// <summary>
    /// Removes the selected worktree after confirmation. Destructive (INV-11): the confirmation
    /// spells out the uncommitted count, because that is what removal actually destroys. That count
    /// was read when the list was loaded; the service reads it again, and if more has changed since
    /// (an agent kept writing) the user is asked again with the fresh number before anything goes.
    /// </summary>
    [RelayCommand]
    private async Task RemoveSelectedAsync()
    {
        if (!CanRemoveSelected || Selected is not { } target) return;
        if (!_confirmRemove(target)) return;

        IsBusy = true;
        try
        {
            // A locked worktree is only removed when the user has just said yes to that in the
            // prompt — force is the answer to the confirmation, not a default.
            var result = await _worktrees.RemoveAsync(
                _anyPathInRepository, target.Name, force: target.IsLocked, acceptedDirtyCount: Accepted(target.DirtyCount));

            if (!result.Success && result.Kind == GitFailureKind.DirtyWorkingTree)
            {
                var fresh = int.TryParse(result.Detail, out var count) ? count : GitWorktreeInfo.UnknownDirtyCount;
                var updated = target with { DirtyCount = fresh };
                if (!_confirmRemove(updated)) return;

                result = await _worktrees.RemoveAsync(
                    _anyPathInRepository, target.Name, force: target.IsLocked, acceptedDirtyCount: Accepted(fresh));
            }

            if (result.Success) RemovedAny = true;

            // Removed from git with its folder left behind is still something the user has to deal with.
            IsError = !result.Success || result.Kind != GitFailureKind.None;
            StatusMessage = result switch
            {
                { Success: false } =>
                    string.Format(LocalizationManager.Get("Git_WtRemoveFailed"), Services.GitResultText.Describe(result)),
                { Kind: GitFailureKind.DirectoryNotDeleted } => Services.GitResultText.Describe(result),
                _ => string.Format(LocalizationManager.Get("Git_WtRemoved"), target.Name),
            };
        }
        finally
        {
            IsBusy = false;
        }

        // The refresh clears the status line, which would swallow the very message (removed / why it
        // failed) the user needs to read — so it is put back afterwards.
        var message = StatusMessage;
        var failed = IsError;
        await RefreshAsync();
        StatusMessage = message;
        IsError = failed;
    }

    /// <summary>What the confirmed count means to the service: an unknown count was confirmed as
    /// "whatever is there", since the prompt already said it could not be counted.</summary>
    private static int Accepted(int confirmedDirtyCount) =>
        confirmedDirtyCount == GitWorktreeInfo.UnknownDirtyCount ? int.MaxValue : confirmedDirtyCount;

    [RelayCommand]
    private void Cancel() => Close(false);

    private void Close(bool result)
    {
        DialogResult = result;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
