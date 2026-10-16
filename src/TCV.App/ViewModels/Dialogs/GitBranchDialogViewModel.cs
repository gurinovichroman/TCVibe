using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Localization;
using TCV.App.Services;
using TCV.Git;

namespace TCV.App.ViewModels.Dialogs;

/// <summary>A branch in the list, with the worktree that holds it when one does.</summary>
public sealed record GitBranchRow(GitBranchInfo Info)
{
    public string Name => Info.Name;

    /// <summary>Marker column: ● for the branch in use here, ⧉ for one another worktree holds.</summary>
    public string Marker => Info.IsCurrent ? "●" : Info.IsBlocked ? "⧉" : string.Empty;

    /// <summary>Hover text for the marker; empty when the row is an ordinary branch.</summary>
    public string ToolTip => Info.IsCurrent
        ? LocalizationManager.Get("Git_BranchCurrentTip")
        : Info.IsBlocked ? string.Format(LocalizationManager.Get("Git_BranchElsewhereTip"), Info.CheckedOutAt) : string.Empty;

    public override string ToString() => Name;
}

/// <summary>
/// Switch to a branch, start one, or delete a local one — one dialog because they are the same
/// decision at the same moment: you look at the list to find out whether the branch you want exists.
/// The dialog performs the operations itself (like the commit dialog) so a refusal — dirty tree,
/// branch held by another worktree, unmerged commits — can be answered in place instead of by an
/// error box after the window is gone. Local only: no remotes, no push, no pull (ADR-0015).
/// </summary>
public partial class GitBranchDialogViewModel : ObservableObject
{
    private readonly GitWriteService _git;
    private readonly string _anyPath;
    private readonly IDialogService _dialogs;

    public GitBranchDialogViewModel(GitWriteService git, string anyPathInRepository, IDialogService dialogs)
    {
        _git = git;
        _anyPath = anyPathInRepository;
        _dialogs = dialogs;
        WindowTitle = LocalizationManager.Get("Git_BranchTitle");
    }

    public string WindowTitle { get; }

    public ObservableCollection<GitBranchRow> Branches { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSwitch))]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    private GitBranchRow? _selected;

    /// <summary>Name for a branch that does not exist yet. Filling it is what turns the dialog from
    /// a switcher into a creator.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreate))]
    [NotifyPropertyChangedFor(nameof(ValidationMessage))]
    private string _newBranch = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSwitch))]
    [NotifyPropertyChangedFor(nameof(CanCreate))]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    private bool _isBusy;

    /// <summary>Outcome of the last operation. Shown in the error colour when <see cref="IsError"/>.</summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isError;

    /// <summary>Name of the checked-out branch, or empty when HEAD is detached.</summary>
    public string CurrentBranch => Branches.FirstOrDefault(b => b.Info.IsCurrent)?.Name ?? string.Empty;

    /// <summary>The header: the current branch, or "Detached HEAD at …" instead of a blank.</summary>
    [ObservableProperty]
    private string _headDisplay = string.Empty;

    /// <summary>True once anything in the repository changed, so the caller refreshes the panels.</summary>
    public bool Changed { get; private set; }

    /// <summary>Switching to the branch already checked out would do nothing but reload the panel,
    /// and a branch another worktree holds cannot be switched to at all.</summary>
    public bool CanSwitch => !IsBusy && Selected is { Info: { IsCurrent: false, IsBlocked: false } };

    public bool CanDelete => !IsBusy && Selected is { Info: { IsCurrent: false, IsBlocked: false } };

    public bool CanCreate =>
        !IsBusy
        && GitRefName.IsValidBranch(NewBranch)
        && !Branches.Any(b => string.Equals(b.Name, NewBranch.Trim(), StringComparison.Ordinal));

    public string ValidationMessage
    {
        get
        {
            if (NewBranch.Length == 0) return string.Empty;
            if (!GitRefName.IsValidBranch(NewBranch))
                return LocalizationManager.Get("Git_WtInvalidBranch");
            if (Branches.Any(b => string.Equals(b.Name, NewBranch.Trim(), StringComparison.Ordinal)))
                return LocalizationManager.Get("Git_BranchExists");
            return string.Empty;
        }
    }

    public bool DialogResult { get; private set; }
    public event EventHandler? CloseRequested;

    /// <summary>Reads the branch list off the UI thread; the window is already up when this runs.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        var previous = Selected?.Name;
        var list = await _git.ListBranchDetailsAsync(_anyPath);

        Branches.Clear();
        foreach (var info in list) Branches.Add(new GitBranchRow(info));

        Selected = Branches.FirstOrDefault(b => b.Name == previous)
                   ?? Branches.FirstOrDefault(b => !b.Info.IsCurrent && !b.Info.IsBlocked)
                   ?? Branches.FirstOrDefault();

        OnPropertyChanged(nameof(CurrentBranch));

        // No current branch in the list means HEAD is detached (or the repository is unborn);
        // the head file names the commit, and it is read off the UI thread like every disk touch.
        if (CurrentBranch.Length > 0)
        {
            HeadDisplay = CurrentBranch;
        }
        else
        {
            var path = _anyPath;
            var head = await Task.Run(() => GitHeadReader.ReadBranch(path));
            HeadDisplay = GitHeadReader.LooksLikeCommitId(head)
                ? string.Format(LocalizationManager.Get("Git_TabDetachedTip"), head)
                : head;
        }

        OnPropertyChanged(nameof(CanCreate));
        OnPropertyChanged(nameof(ValidationMessage));
    }

    [RelayCommand]
    private async Task SwitchAsync()
    {
        if (!CanSwitch || Selected is not { } row) return;

        if (await CheckoutAsync(row.Name, create: false)) Close(true);
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (!CanCreate) return;

        if (await CheckoutAsync(NewBranch.Trim(), create: true)) Close(true);
    }

    /// <summary>
    /// Runs the checkout. A dirty tree is the common refusal, and the useful answer is not an error
    /// box but an offer: stash everything (untracked files included) and go on. The stash is an
    /// ordinary entry the user can pop, so nothing is lost (INV-11).
    /// </summary>
    private async Task<bool> CheckoutAsync(string branch, bool create)
    {
        Begin();
        try
        {
            var result = await _git.CheckoutBranchAsync(_anyPath, branch, create);

            if (!result.Success && result.Kind == GitFailureKind.DirtyWorkingTree)
            {
                var offer = string.Format(LocalizationManager.Get("Git_BranchDirtyOffer"), branch);
                if (!await _dialogs.ConfirmAsync(WindowTitle, offer))
                {
                    Fail(GitResultText.Describe(result));
                    return false;
                }

                var stash = await _git.StashAsync(
                    _anyPath, string.Format(LocalizationManager.Get("Git_BranchStashMessage"), branch));
                if (!stash.Success)
                {
                    Fail(GitResultText.Describe(stash));
                    return false;
                }

                Changed = true;
                result = await _git.CheckoutBranchAsync(_anyPath, branch, create);
            }

            if (!result.Success)
            {
                Fail(GitResultText.Describe(result));
                return false;
            }

            Changed = true;
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Deletes the selected local branch after confirmation. A branch whose commits exist nowhere
    /// else needs a second, plainer confirmation: that is the one case where the delete loses work.
    /// </summary>
    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (!CanDelete || Selected is not { } row) return;

        if (!await _dialogs.ConfirmAsync(
                LocalizationManager.Get("Git_BranchDeleteTitle"),
                string.Format(LocalizationManager.Get("Git_BranchDeleteConfirm"), row.Name)))
            return;

        Begin();
        try
        {
            var result = await _git.DeleteBranchAsync(_anyPath, row.Name, force: false);

            if (!result.Success && result.Kind == GitFailureKind.Unmerged)
            {
                if (!await _dialogs.ConfirmAsync(
                        LocalizationManager.Get("Git_BranchDeleteTitle"),
                        string.Format(LocalizationManager.Get("Git_BranchDeleteUnmerged"), row.Name)))
                {
                    Fail(GitResultText.Describe(result));
                    return;
                }

                result = await _git.DeleteBranchAsync(_anyPath, row.Name, force: true);
            }

            if (!result.Success)
            {
                Fail(GitResultText.Describe(result));
                return;
            }

            Changed = true;
            StatusMessage = string.Format(LocalizationManager.Get("Git_BranchDeleted"), row.Name);
            IsError = false;
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
    }

    [RelayCommand]
    private void Cancel() => Close(Changed);

    private void Begin()
    {
        IsBusy = true;
        IsError = false;
        StatusMessage = string.Empty;
    }

    private void Fail(string message)
    {
        StatusMessage = message;
        IsError = true;
    }

    private void Close(bool result)
    {
        DialogResult = result;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
