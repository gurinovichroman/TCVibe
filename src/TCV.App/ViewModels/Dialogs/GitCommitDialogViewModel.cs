using System.Collections;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.Ai.Llm;
using TCV.App.Localization;
using TCV.App.Services;
using TCV.Git;

namespace TCV.App.ViewModels.Dialogs;

/// <summary>
/// Stage, unstage, review and commit without leaving the panel. Not a git client — the point is that
/// the review loop (see what changed → look at the diff → stage it → commit) stays where the files
/// are, so the panel keeps being where the work happens instead of a launcher for another window.
/// </summary>
public partial class GitCommitDialogViewModel : ObservableObject
{
    private readonly GitWriteService _git;
    private readonly string _anyPathInRepository;
    private readonly IDialogService? _dialogs;
    private readonly Func<string, Task>? _showDiff;
    private string _repositoryRoot = string.Empty;

    /// <summary>Optional: without it the draft button simply is not offered (6.14, ADR-0007).</summary>
    private readonly LlmClient? _llm;

    /// <param name="dialogs">Used for confirmations (discard, staging a conflict, replacing a typed
    /// message). Without one those confirmations count as "no": a destructive step is never taken
    /// on the strength of a missing prompt (INV-11).</param>
    /// <param name="showDiff">Opens the diff of a repository-relative absolute path; null hides the button.</param>
    public GitCommitDialogViewModel(
        GitWriteService git,
        string anyPathInRepository,
        LlmClient? llm = null,
        IDialogService? dialogs = null,
        Func<string, Task>? showDiff = null)
    {
        _git = git;
        _anyPathInRepository = anyPathInRepository;
        _llm = llm;
        _dialogs = dialogs;
        _showDiff = showDiff;

        WindowTitle = LocalizationManager.Get("Git_CommitTitle");
    }

    public string WindowTitle { get; }

    /// <summary>Whether a model is configured at all — drives the button's visibility.</summary>
    public bool LlmAvailable => _llm?.IsConfigured == true;

    /// <summary>
    /// Hover text of the draft button. For an endpoint off this machine it names the host the diff
    /// goes to before the button is pressed — the same disclosure the AI rename gives.
    /// </summary>
    public string DraftHint
    {
        get
        {
            var hint = LocalizationManager.Get("Git_DraftMessageHint");
            if (_llm?.Options is not { IsConfigured: true, IsLocalEndpoint: false } options) return hint;

            var host = options.EndpointHost.Length > 0 ? options.EndpointHost : options.Endpoint;
            return hint + Environment.NewLine + string.Format(LocalizationManager.Get("Git_DraftSentTo"), host);
        }
    }

    /// <summary>Whether the diff button is offered at all.</summary>
    public bool CanShowDiffs => _showDiff is not null;

    /// <summary>
    /// A draft needs something to describe. Requiring a change here is what stops the button from
    /// inventing a message for a clean tree.
    /// </summary>
    public bool CanDraft => LlmAvailable && !IsBusy && (Staged.Count > 0 || Unstaged.Count > 0);

    /// <summary>
    /// Fills the message box from the diff (6.14). Deliberately only that: it writes into an editable
    /// field and never commits, which is the ADR-0008 rule and the difference between
    /// a useful assist and a tool that authors your history for you. A message the user has already
    /// typed is replaced only after they say so.
    /// </summary>
    [RelayCommand]
    private async Task DraftMessageAsync()
    {
        if (_llm is null || !CanDraft) return;

        IsBusy = true;
        SetInfo(LocalizationManager.Get("Git_Drafting"));
        try
        {
            // Staged content when there is any — that is what the commit will actually contain.
            var stagedOnly = Staged.Count > 0;
            var diff = await _git.GetDiffAsync(
                _anyPathInRepository, stagedOnly, CommitMessagePrompt.MaxDiffChars);

            var files = (stagedOnly ? Staged : Unstaged)
                .Select(f => (f.RelativePath, f.StatusLabel))
                .ToList();

            var result = await _llm.CompleteAsync(CommitMessagePrompt.Build(Branch, files, diff));
            if (!result.Success)
            {
                SetError(Agents.LlmErrorText.Localize(result.Error));
                return;
            }

            // The model takes seconds; the user may have typed in the meantime. Ask against the
            // message as it is *now*, not as it was when the button was pressed — once. Text the user
            // wrote is never replaced without asking; with nobody to ask it is simply kept.
            if (!string.IsNullOrWhiteSpace(Message)
                && !await ConfirmAsync("Git_DraftOverwriteTitle", "Git_DraftOverwrite"))
            {
                SetInfo(string.Empty);
                return;
            }

            Message = CommitMessagePrompt.Clean(result.Text);
            SetInfo(LocalizationManager.Get("Git_Drafted"));
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanDraft));
        }
    }

    /// <summary>Staged entries — exactly what the next commit will contain.</summary>
    public ObservableCollection<GitChangedFile> Staged { get; } = new();

    /// <summary>Working-tree changes that are not in the index yet.</summary>
    public ObservableCollection<GitChangedFile> Unstaged { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCommit))]
    private string _message = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BranchDisplay))]
    private string _branch = string.Empty;

    /// <summary>The header: the branch, or "Detached HEAD at …" — committing there makes a commit
    /// no branch points at, and a bare hash reads like a branch name.</summary>
    public string BranchDisplay => GitHeadReader.LooksLikeCommitId(Branch)
        ? string.Format(LocalizationManager.Get("Git_TabDetachedTip"), Branch)
        : Branch;

    /// <summary>Set by the view from the lists' selections, so the per-selection buttons are only
    /// lit when there is something for them to act on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanActOnUnstagedSelection))]
    private bool _hasUnstagedSelection;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanActOnStagedSelection))]
    private bool _hasStagedSelection;

    public bool CanActOnUnstagedSelection => !IsBusy && HasUnstagedSelection;
    public bool CanActOnStagedSelection => !IsBusy && HasStagedSelection;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDraft))]
    [NotifyPropertyChangedFor(nameof(CanCommit))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(CanActOnUnstagedSelection))]
    [NotifyPropertyChangedFor(nameof(CanActOnStagedSelection))]
    private bool _isBusy;

    /// <summary>Inverse of <see cref="IsBusy"/>, for enabling the buttons.</summary>
    public bool IsIdle => !IsBusy;

    /// <summary>Last outcome or hint; shown in the error colour when <see cref="IsError"/>.</summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCommit))]
    private bool _hasStagedChanges;

    /// <summary>Something in the working tree that <c>commit -a</c> would pick up (untracked files never count).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCommit))]
    private bool _hasTrackedUnstaged;

    /// <summary>
    /// Commit staged content, or all tracked modifications when nothing is staged — the
    /// <c>git commit -a</c> shape, which is what "I just want to commit this" means in practice.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCommit))]
    private bool _stageAll;

    /// <summary>Set once the user has touched the checkbox: from then on it is their decision, and a
    /// refresh (which runs after every stage/unstage) must not put it back.</summary>
    private bool _stageAllChosenByUser;

    /// <summary>Guards the one write the dialog itself makes, so it does not read as a user choice.</summary>
    private bool _settingStageAll;

    partial void OnStageAllChanged(bool value)
    {
        if (!_settingStageAll) _stageAllChosenByUser = true;
    }

    /// <summary>
    /// A commit is offered only when it can succeed: a message, and something to commit — staged
    /// changes, or (with the checkbox) tracked modifications. Untracked files alone never qualify,
    /// so the button is never lit for a commit git would refuse as empty.
    /// </summary>
    public bool CanCommit =>
        !IsBusy
        && !string.IsNullOrWhiteSpace(Message)
        && (HasStagedChanges || (StageAll && HasTrackedUnstaged));

    public bool DialogResult { get; private set; }

    /// <summary>True when a commit actually landed.</summary>
    public bool Committed { get; private set; }

    /// <summary>True when the repository changed in any way (stage, unstage, discard, commit), so the
    /// caller refreshes the panels and their git column.</summary>
    public bool Changed { get; private set; }

    public event EventHandler? CloseRequested;

    /// <summary>Re-reads the working tree. Runs after every stage/unstage: those change which side a
    /// file sits on, and a stale list is worse than no list.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var status = await _git.GetStatusAsync(_anyPathInRepository);
            Branch = status.Branch;
            _repositoryRoot = status.RepositoryRoot;
            HasStagedChanges = status.HasStagedChanges;

            Staged.Clear();
            foreach (var file in status.Staged) Staged.Add(file);

            Unstaged.Clear();
            foreach (var file in status.Unstaged) Unstaged.Add(file);

            HasTrackedUnstaged = Unstaged.Any(f => f.Status != GitFileStatus.Untracked);

            if (!status.IsRepository)
                SetInfo(LocalizationManager.Get("Git_NoRepo"));
            else if (Staged.Count == 0 && Unstaged.Count == 0)
                SetInfo(LocalizationManager.Get("Git_Clean"));

            // Nothing staged and nothing tracked to stage means the "-a" shortcut cannot help either.
            // This is an opening default, not a rule: once the user has had an opinion about the box,
            // a refresh that re-armed "commit -a" would quietly commit more than they staged.
            if (!_stageAllChosenByUser && !HasStagedChanges && HasTrackedUnstaged)
            {
                _settingStageAll = true;
                try { StageAll = true; }
                finally { _settingStageAll = false; }
            }
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanDraft));
        }
    }

    [RelayCommand]
    private async Task StageSelectedAsync(IList? selection)
    {
        var files = Cast(selection);
        if (files.Count == 0) return;

        if (!await ConfirmConflictStagingAsync(files)) return;

        await RunAsync(() => _git.StageAsync(_anyPathInRepository, Paths(files)));
    }

    [RelayCommand]
    private async Task UnstageSelectedAsync(IList? selection)
    {
        var files = Cast(selection);
        if (files.Count == 0) return;

        await RunAsync(() => _git.UnstageAsync(_anyPathInRepository, Paths(files)));
    }

    [RelayCommand]
    private async Task StageAllFilesAsync()
    {
        var files = Unstaged.ToList();
        if (files.Count == 0) return;

        if (!await ConfirmConflictStagingAsync(files)) return;

        await RunAsync(() => _git.StageAsync(_anyPathInRepository, Paths(files)));
    }

    [RelayCommand]
    private async Task UnstageAllFilesAsync()
    {
        var files = Staged.ToList();
        if (files.Count == 0) return;

        await RunAsync(() => _git.UnstageAsync(_anyPathInRepository, Paths(files)));
    }

    /// <summary>
    /// Opens the diff of the first selected file. Works on either side — for a staged file the diff
    /// is against HEAD like the panel's Ctrl+G.
    /// </summary>
    [RelayCommand]
    private async Task ShowDiffAsync(IList? selection)
    {
        if (_showDiff is null || Cast(selection).FirstOrDefault() is not { } file) return;

        var relative = file.RelativePath.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar);
        var full = _repositoryRoot.Length > 0 ? Path.Combine(_repositoryRoot, relative) : relative;
        await _showDiff(full);
    }

    /// <summary>
    /// Throws the selected working-tree changes away (INV-11: always confirmed, and the prompt says
    /// it cannot be undone). Only tracked files can be reverted here: an untracked file has nothing
    /// in HEAD to go back to, so removing it is a file deletion and belongs to F8 and the operation
    /// queue (INV-10). Note that restoring resets the index side of the path as well.
    /// </summary>
    [RelayCommand]
    private async Task DiscardSelectedAsync(IList? selection)
    {
        var files = Cast(selection);
        var tracked = files.Where(f => f.Status != GitFileStatus.Untracked && !f.IsStaged).ToList();
        if (tracked.Count == 0)
        {
            if (files.Count > 0) SetError(LocalizationManager.Get("Git_DiscardUntracked"));
            return;
        }

        var alsoStaged = tracked.Count(t => Staged.Any(s =>
            string.Equals(s.RelativePath, t.RelativePath, StringComparison.OrdinalIgnoreCase)));

        var names = string.Join(Environment.NewLine, tracked.Take(8).Select(f => "  " + f.RelativePath));
        if (tracked.Count > 8) names += Environment.NewLine + "  …";

        var text = LocalizationManager.Format("Git_DiscardConfirm", tracked.Count, names);
        if (alsoStaged > 0)
            text += Environment.NewLine + Environment.NewLine
                    + string.Format(LocalizationManager.Get("Git_DiscardAlsoStaged"), alsoStaged);
        if (tracked.Count < files.Count)
            text += Environment.NewLine + Environment.NewLine
                    + LocalizationManager.Format("Git_DiscardSkipped", files.Count - tracked.Count);

        if (_dialogs is null || !await _dialogs.ConfirmAsync(LocalizationManager.Get("Git_DiscardTitle"), text)) return;

        await RunAsync(() => _git.RestoreAsync(_anyPathInRepository, Paths(tracked)));
    }

    [RelayCommand]
    private async Task CommitAsync()
    {
        if (!CanCommit) return;

        // `commit -a` stages every tracked change, unresolved conflicts included. Say so first.
        if (StageAll && Unstaged.Any(f => f.Status == GitFileStatus.Conflict)
            && !await ConfirmConflictStagingAsync(Unstaged.Where(f => f.Status == GitFileStatus.Conflict).ToList()))
            return;

        GitOperationResult result;
        IsBusy = true;
        try
        {
            result = await _git.CommitAsync(_anyPathInRepository, Message.Trim(), StageAll);
        }
        finally
        {
            IsBusy = false;
        }

        if (result.Success)
        {
            Committed = true;
            Changed = true;
            DialogResult = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        // "-a" may already have staged the tracked changes before the commit itself failed, so the
        // lists are re-read (keeping the error visible) and the panels told the index moved.
        if (StageAll) Changed = true;
        var error = GitResultText.Describe(result);
        await RefreshAsync();
        SetError(error);
    }

    [RelayCommand]
    private void Cancel()
    {
        DialogResult = Changed;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunAsync(Func<Task<GitOperationResult>> action)
    {
        IsBusy = true;
        try
        {
            var result = await action();
            if (result.Success)
            {
                Changed = true;
                SetInfo(string.Empty);
            }
            else
            {
                SetError(GitResultText.Describe(result));
            }
        }
        finally
        {
            IsBusy = false;
        }

        // Keep an error visible across the refresh that follows it.
        var error = IsError ? StatusMessage : null;
        await RefreshAsync();
        if (error is not null) SetError(error);
    }

    /// <summary>
    /// Staging a conflicted file marks it resolved as it stands — conflict markers included — which
    /// is what git does too, but a GUI is expected to say so before it does it.
    /// </summary>
    private async Task<bool> ConfirmConflictStagingAsync(IReadOnlyCollection<GitChangedFile> files)
    {
        var conflicts = files.Count(f => f.Status == GitFileStatus.Conflict);
        if (conflicts == 0) return true;

        if (_dialogs is null) return false;
        return await _dialogs.ConfirmAsync(
            LocalizationManager.Get("Git_ConflictTitle"),
            LocalizationManager.Format("Git_ConflictStageWarn", conflicts));
    }

    private async Task<bool> ConfirmAsync(string titleKey, string messageKey) =>
        _dialogs is not null
        && await _dialogs.ConfirmAsync(LocalizationManager.Get(titleKey), LocalizationManager.Get(messageKey));

    private static List<GitChangedFile> Cast(IList? selection) =>
        selection?.OfType<GitChangedFile>().ToList() ?? new List<GitChangedFile>();

    private static List<string> Paths(IEnumerable<GitChangedFile> files) =>
        files.Select(f => f.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private void SetError(string message)
    {
        StatusMessage = message;
        IsError = true;
    }

    private void SetInfo(string message)
    {
        StatusMessage = message;
        IsError = false;
    }
}
