using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Localization;
using TCV.Git;

namespace TCV.App.ViewModels.Dialogs;

/// <summary>Everything the "new worktree for this task" flow needs, as confirmed by the user.</summary>
/// <param name="Branch">Branch to create (or reuse) in the new worktree.</param>
/// <param name="CreateBranch">False when an existing branch was picked.</param>
/// <param name="BaseCommittish">Start point for a new branch; empty means current HEAD.</param>
/// <param name="TargetPath">Absolute directory for the worktree.</param>
/// <param name="CarryFiles">Copy the source checkout's gitignored configuration over.</param>
/// <param name="RunSetupCommand">Run the configured setup command in the new worktree.</param>
/// <param name="CreateWorkspace">Register the worktree as a workspace and switch to it.</param>
public sealed record NewWorktreeResult(
    string Branch,
    bool CreateBranch,
    string BaseCommittish,
    string TargetPath,
    bool CarryFiles,
    bool RunSetupCommand,
    bool CreateWorkspace);

/// <summary>
/// "New worktree under a task": type what you are about to do, get a branch, a directory, a
/// workspace and a project that actually runs. The two boxes that are normally filled by hand —
/// branch name and path — derive themselves from the task title as it is typed, which is the
/// difference between using worktrees and meaning to.
/// </summary>
public partial class NewWorktreeDialogViewModel : ObservableObject
{
    private readonly string _worktreeRoot;

    /// <summary>Existing branches another worktree already has checked out — git allows one checkout per branch.</summary>
    private readonly IReadOnlyCollection<string> _heldElsewhere;

    /// <summary>Set while a derived field is being written, so writing it does not look like the
    /// user typing over the derivation.</summary>
    private bool _updatingDerived;

    public NewWorktreeDialogViewModel(
        string repositoryRoot,
        string worktreeRoot,
        IReadOnlyList<string> existingBranches,
        string setupCommand,
        bool createWorkspaceByDefault,
        IReadOnlyCollection<string>? branchesHeldElsewhere = null)
    {
        _worktreeRoot = worktreeRoot;
        _heldElsewhere = branchesHeldElsewhere ?? Array.Empty<string>();
        RepositoryRoot = repositoryRoot;
        SetupCommand = setupCommand;
        ExistingBranches = existingBranches;

        _runSetupCommand = !string.IsNullOrWhiteSpace(setupCommand);
        _createWorkspace = createWorkspaceByDefault;

        WindowTitle = LocalizationManager.Get("Git_WtNewTitle");
    }

    public string WindowTitle { get; }
    public string RepositoryRoot { get; }
    public string SetupCommand { get; }
    public IReadOnlyList<string> ExistingBranches { get; }

    /// <summary>Free text: what the worktree is for. Everything else follows from it until touched.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValidationMessage))]
    private string _taskTitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    [NotifyPropertyChangedFor(nameof(ValidationMessage))]
    [NotifyPropertyChangedFor(nameof(InfoMessage))]
    [NotifyPropertyChangedFor(nameof(BranchExists))]
    [NotifyPropertyChangedFor(nameof(BranchHeldElsewhere))]
    private string _branch = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    [NotifyPropertyChangedFor(nameof(ValidationMessage))]
    [NotifyPropertyChangedFor(nameof(InfoMessage))]
    private string _targetPath = string.Empty;

    /// <summary>Start point for the new branch. Empty means current HEAD, which is what you want
    /// nine times out of ten and what git itself defaults to.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InfoMessage))]
    private string _baseCommittish = string.Empty;

    [ObservableProperty]
    private bool _carryFiles = true;

    [ObservableProperty]
    private bool _runSetupCommand;

    [ObservableProperty]
    private bool _createWorkspace;

    /// <summary>Whether the setup-command checkbox is worth offering at all.</summary>
    public bool HasSetupCommand => !string.IsNullOrWhiteSpace(SetupCommand);

    partial void OnTaskTitleChanged(string value)
    {
        // Deriving both fields from the title is the point of the dialog; the moment the user edits
        // one by hand, that field stops following (see OnBranchChanged / OnTargetPathChanged).
        if (!_branchEdited)
            SetDerived(() => Branch = GitRefName.FromTitle(value));

        if (!_pathEdited)
            SetDerived(() => TargetPath = ComposePath(Branch));
    }

    private bool _branchEdited;
    private bool _pathEdited;

    partial void OnBranchChanged(string value)
    {
        if (_updatingDerived) return;

        _branchEdited = true;
        if (!_pathEdited)
            SetDerived(() => TargetPath = ComposePath(value));
    }

    partial void OnTargetPathChanged(string value)
    {
        _ = ProbeTargetAsync();

        if (_updatingDerived) return;
        _pathEdited = true;
    }

    /// <summary>
    /// Whether the target already holds files, as last probed. The probe touches the disk — on a
    /// share, for seconds — so it runs off the UI thread (INV-06) rather than inside the properties
    /// the bindings read on every keystroke; the service refuses a non-empty folder again anyway.
    /// </summary>
    private bool _targetHasContent;
    private int _probeVersion;

    private async Task ProbeTargetAsync()
    {
        var version = ++_probeVersion;
        var path = TargetPath.Trim();
        var hasContent = CheckPathShape(path) == PathProblem.None
                         && await Task.Run(() => DirectoryHasContent(path));
        if (version != _probeVersion) return;   // the box changed while the disk answered

        _targetHasContent = hasContent;
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(ValidationMessage));
    }

    /// <summary><see cref="CheckPath"/> as the live form sees it: the shape rules now, the
    /// "not empty" verdict from the last background probe.</summary>
    private PathProblem LivePathProblem() =>
        CheckPathShape(TargetPath) is var shape and not PathProblem.None ? shape
        : _targetHasContent ? PathProblem.NotEmpty
        : PathProblem.None;

    private void SetDerived(Action assign)
    {
        _updatingDerived = true;
        try { assign(); }
        finally { _updatingDerived = false; }
    }

    private string ComposePath(string branch)
    {
        if (_worktreeRoot.Length == 0) return string.Empty;

        // No branch, no path. GitRefName.DirectoryName falls back to "worktree" for its own callers,
        // and offering that here would put a directory under a name the user never typed while the
        // branch box sits empty.
        if (branch.Trim().Length == 0) return string.Empty;

        var folder = GitRefName.DirectoryName(branch);
        return folder.Length == 0 ? string.Empty : Path.Combine(_worktreeRoot, folder);
    }

    /// <summary>True when the branch already exists — then the worktree checks it out instead of
    /// creating it, which is how you pick up a branch a colleague pushed.</summary>
    public bool BranchExists =>
        ExistingBranches.Any(b => string.Equals(b, Branch, StringComparison.Ordinal));

    /// <summary>The branch exists but another worktree holds it, so a second checkout is impossible.</summary>
    public bool BranchHeldElsewhere =>
        _heldElsewhere.Any(b => string.Equals(b, Branch.Trim(), StringComparison.Ordinal));

    public bool CanConfirm =>
        GitRefName.IsValidBranch(Branch)
        && !BranchHeldElsewhere
        && LivePathProblem() == PathProblem.None;

    /// <summary>What is wrong with a worktree directory, if anything. Pure, so the rules are testable.</summary>
    public enum PathProblem
    {
        None,
        Empty,
        /// <summary>Relative paths would resolve against the app's own folder.</summary>
        NotAbsolute,
        InvalidCharacters,
        NotEmpty,
    }

    public static PathProblem CheckPath(string? path)
    {
        var shape = CheckPathShape(path);
        if (shape != PathProblem.None) return shape;
        return DirectoryHasContent(path!.Trim()) ? PathProblem.NotEmpty : PathProblem.None;
    }

    /// <summary>The rules of <see cref="CheckPath"/> that need no disk access.</summary>
    private static PathProblem CheckPathShape(string? path)
    {
        var trimmed = path?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return PathProblem.Empty;
        if (trimmed.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return PathProblem.InvalidCharacters;
        if (!Path.IsPathRooted(trimmed) || Path.GetPathRoot(trimmed) is not { Length: > 1 })
            return PathProblem.NotAbsolute;
        return PathProblem.None;
    }

    /// <summary>Why the OK button is off, in one line. Silent while the form is still empty — a
    /// dialog that opens shouting at you is noise, not validation.</summary>
    public string ValidationMessage
    {
        get
        {
            if (Branch.Length == 0 && TargetPath.Length == 0)
            {
                // A title with no letters at all (an emoji, say) slugs to an empty string, so both
                // derived fields stay blank and OK stays off. Saying nothing here would leave the
                // user with a dead dialog and no idea why.
                return TaskTitle.Trim().Length > 0
                    ? LocalizationManager.Get("Git_WtTitleNoSlug")
                    : string.Empty;
            }
            if (!GitRefName.IsValidBranch(Branch))
                return LocalizationManager.Get("Git_WtInvalidBranch");
            if (BranchHeldElsewhere)
                return string.Format(LocalizationManager.Get("Git_WtBranchHeld"), Branch.Trim());

            return LivePathProblem() switch
            {
                PathProblem.Empty => LocalizationManager.Get("Git_WtNoPath"),
                PathProblem.NotAbsolute => LocalizationManager.Get("Git_WtPathRelative"),
                PathProblem.InvalidCharacters => LocalizationManager.Get("Git_WtPathInvalid"),
                PathProblem.NotEmpty => string.Format(LocalizationManager.Get("Git_WtPathNotEmpty"), TargetPath.Trim()),
                _ => string.Empty,
            };
        }
    }

    /// <summary>A non-blocking heads-up under the form: the branch already exists (so it will be
    /// checked out rather than created), or the folder sits inside the repository.</summary>
    public string InfoMessage
    {
        get
        {
            if (!GitRefName.IsValidBranch(Branch)) return string.Empty;

            if (BranchExists && !BranchHeldElsewhere)
                return BaseCommittish.Trim().Length > 0
                    ? LocalizationManager.Get("Git_WtBranchExistsBase")
                    : LocalizationManager.Get("Git_WtBranchExists");

            if (TargetPath.Trim().Length > 0 && RepositoryRoot.Length > 0
                && IsInside(TargetPath.Trim(), RepositoryRoot))
                return LocalizationManager.Get("Git_WtInsideRepo");

            return string.Empty;
        }
    }

    private static bool IsInside(string path, string root)
    {
        try
        {
            var relative = Path.GetRelativePath(root, path);
            return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool DirectoryHasContent(string path)
    {
        try
        {
            return path.Length > 0
                   && Directory.Exists(path)
                   && Directory.EnumerateFileSystemEntries(path).Any();
        }
        catch
        {
            // An unreadable path is a problem, but not this dialog's to diagnose — git will say so.
            return false;
        }
    }

    public NewWorktreeResult? Result { get; private set; }
    public bool DialogResult { get; private set; }
    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void Confirm()
    {
        if (!CanConfirm) return;

        Result = new NewWorktreeResult(
            Branch.Trim(),
            CreateBranch: !BranchExists,
            BaseCommittish: BaseCommittish.Trim(),
            TargetPath: Path.GetFullPath(TargetPath.Trim()),
            CarryFiles: CarryFiles,
            RunSetupCommand: RunSetupCommand && HasSetupCommand,
            CreateWorkspace: CreateWorkspace);

        DialogResult = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        DialogResult = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
