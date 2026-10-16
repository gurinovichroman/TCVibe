using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Localization;
using TCV.App.Navigation;
using TCV.App.Settings;
using TCV.Git;

namespace TCV.App.ViewModels.Dialogs;

/// <summary>What the agent launcher was asked to set up.</summary>
/// <param name="Task">Free-text description; recorded with the session and shown in the dashboard.</param>
/// <param name="Tool">Agent CLI to start.</param>
/// <param name="CreateWorktree">Isolate the run in a fresh worktree instead of the current directory.</param>
/// <param name="Branch">Branch for the new worktree; empty when running in place.</param>
/// <param name="TargetPath">Directory the agent will run in.</param>
/// <param name="CarryFiles">Copy gitignored configuration into the new worktree.</param>
/// <param name="RunSetupCommand">Run the configured setup command before the agent starts.</param>
/// <param name="CreateWorkspace">Register the new worktree as a workspace and switch to it; false
/// when running in place.</param>
public sealed record AgentLaunchResult(
    string Task,
    ExternalTool Tool,
    bool CreateWorktree,
    string Branch,
    string TargetPath,
    bool CarryFiles,
    bool RunSetupCommand,
    bool CreateWorkspace);

/// <summary>
/// The agent launcher (6.13a): describe the task, get an isolated worktree, an agent running in it,
/// and a recorded session so its edits are attributable afterwards.
/// <para>
/// This is the step that makes the rest of the layer worth having. «Run agent here» (Ctrl+Shift+A)
/// already starts a CLI in the current directory — which is exactly what you should not do when the
/// agent might be wrong, because its output is then mixed into the checkout you were working in. Here
/// the run gets its own worktree and branch (столп II), the session is recorded so
/// <c>ChangeAttribution</c> can name it later, and the review surface is one keystroke away when it
/// finishes.
/// </para>
/// </summary>
public partial class AgentLaunchDialogViewModel : ObservableObject
{
    private readonly string _worktreeRoot;
    private readonly string _currentDirectory;
    private readonly IReadOnlyList<string> _existingBranches;

    /// <summary>Branch → the checkout that has it (this one included): git allows one per branch.</summary>
    private readonly IReadOnlyDictionary<string, string> _heldBranches;

    private readonly Func<string, bool> _folderHasContent;

    private bool _updatingDerived;
    private bool _branchEdited;
    private bool _pathEdited;

    /// <param name="agents">Agent CLIs on offer.</param>
    /// <param name="agentChoice">Which one to preselect — the configured default, or a stand-in the
    /// dialog then says it is.</param>
    /// <param name="currentDirectory">The panel's directory; where the agent runs without a worktree.</param>
    /// <param name="worktreeRoot">Folder new worktrees go under; empty outside a repository.</param>
    /// <param name="existingBranches">Local branch names, so an existing one is checked out, not created.</param>
    /// <param name="heldBranches">Branches some checkout already has, with that checkout's path
    /// (see <see cref="HeldBranches"/>).</param>
    /// <param name="setupCommand">The configured worktree setup command.</param>
    /// <param name="createWorkspaceByDefault">The «worktree creates a workspace» setting.</param>
    /// <param name="insideRepository">False outside a git working copy.</param>
    /// <param name="folderHasContent">Whether a folder already holds anything. Touches the disk, so
    /// it is only ever called off the UI thread (see <see cref="TargetProbe"/>); null asks the disk.</param>
    public AgentLaunchDialogViewModel(
        IReadOnlyList<ExternalTool> agents,
        ExternalToolChoice agentChoice,
        string currentDirectory,
        string worktreeRoot,
        IReadOnlyList<string> existingBranches,
        IReadOnlyDictionary<string, string> heldBranches,
        string setupCommand,
        bool createWorkspaceByDefault,
        bool insideRepository,
        Func<string, bool>? folderHasContent = null)
    {
        ArgumentNullException.ThrowIfNull(agentChoice);

        _folderHasContent = folderHasContent ?? DirectoryHasContent;

        Agents = agents;
        _currentDirectory = currentDirectory;
        _worktreeRoot = worktreeRoot;
        _existingBranches = existingBranches;
        _heldBranches = heldBranches;
        SetupCommand = setupCommand;
        InsideRepository = insideRepository;

        // The configured default, not simply the first agent found (ROADMAP 6.53); a stand-in is
        // announced under the box rather than swapped in silently.
        _selectedAgent = agentChoice.Tool is { } chosen && agents.Contains(chosen) ? chosen : agents.FirstOrDefault();
        AgentNotice = agentChoice.Kind == ExternalToolChoiceKind.DefaultUnavailable && _selectedAgent is not null
            ? LocalizationManager.Format("AgentLaunch_DefaultUnavailable", agentChoice.PreferredName, _selectedAgent.Name)
            : string.Empty;

        _runSetupCommand = !string.IsNullOrWhiteSpace(setupCommand);

        // Outside a repository there is nothing to branch from, so the checkbox starts off and the
        // dialog degrades into "start this agent here with a recorded session".
        _createWorktree = insideRepository;

        // The same switch the «new worktree» dialog starts from: this launcher used to register a
        // workspace for every run whatever the setting said.
        _createWorkspace = createWorkspaceByDefault;

        WindowTitle = LocalizationManager.Get("AgentLaunch_Title");
    }

    /// <summary>
    /// Which checkout holds each branch: the one this dialog was opened from holds its current
    /// branch, and <see cref="GitBranchInfo.CheckedOutAt"/> names the others (found through the main
    /// repository's <c>.git/worktrees</c>, TRAP-25/TRAP-42).
    /// </summary>
    public static IReadOnlyDictionary<string, string> HeldBranches(
        IEnumerable<GitBranchInfo> branches, string currentCheckout)
    {
        var held = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var branch in branches)
        {
            if (branch.CheckedOutAt is { Length: > 0 } elsewhere)
            {
                held.TryAdd(branch.Name, elsewhere);
            }
            else if (branch.IsCurrent)
            {
                held.TryAdd(branch.Name, currentCheckout);
            }
        }

        return held;
    }

    public string WindowTitle { get; }

    public IReadOnlyList<ExternalTool> Agents { get; }

    /// <summary>Set when the configured default agent could not be preselected: which one stands in.</summary>
    public string AgentNotice { get; }

    public bool HasAgentNotice => AgentNotice.Length > 0;

    public string SetupCommand { get; }

    /// <summary>False outside a git working copy — then no worktree can be created.</summary>
    public bool InsideRepository { get; }

    public bool HasSetupCommand => !string.IsNullOrWhiteSpace(SetupCommand);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    [NotifyPropertyChangedFor(nameof(ValidationMessage))]
    private string _task = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private ExternalTool? _selectedAgent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    [NotifyPropertyChangedFor(nameof(ValidationMessage))]
    private bool _createWorktree;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    [NotifyPropertyChangedFor(nameof(ValidationMessage))]
    private string _branch = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    [NotifyPropertyChangedFor(nameof(ValidationMessage))]
    private string _targetPath = string.Empty;

    [ObservableProperty]
    private bool _carryFiles = true;

    [ObservableProperty]
    private bool _runSetupCommand;

    /// <summary>Register the new worktree as a workspace and switch to it. Only meaningful with
    /// <see cref="CreateWorktree"/>; starts from the setting.</summary>
    [ObservableProperty]
    private bool _createWorkspace;

    partial void OnTaskChanged(string value)
    {
        // Same derivation as the "new worktree" dialog: the branch and the directory follow the task
        // title until the user overrides one, because a worktree flow nobody names is a worktree flow
        // nobody uses.
        if (!_branchEdited) SetDerived(() => Branch = GitRefName.FromTitle(value));
        if (!_pathEdited) SetDerived(() => TargetPath = ComposePath(Branch));
    }

    partial void OnBranchChanged(string value)
    {
        if (_updatingDerived) return;

        _branchEdited = true;
        if (!_pathEdited) SetDerived(() => TargetPath = ComposePath(value));
    }

    partial void OnTargetPathChanged(string value)
    {
        TargetProbe = ProbeTargetAsync();

        if (_updatingDerived) return;
        _pathEdited = true;
    }

    /// <summary>
    /// The latest check of whether <see cref="TargetPath"/> already holds files. It touches the disk —
    /// on a share, for seconds — so it runs off the UI thread (INV-06) instead of inside
    /// <see cref="CanConfirm"/> and <see cref="ValidationMessage"/>, which the bindings read on every
    /// keystroke. The worktree service refuses a non-empty folder again anyway.
    /// </summary>
    public System.Threading.Tasks.Task TargetProbe { get; private set; } = System.Threading.Tasks.Task.CompletedTask;

    /// <summary>The path the last finished probe was about, and its verdict.</summary>
    private string _probedPath = string.Empty;
    private bool _probedHasContent;
    private int _probeVersion;

    private async System.Threading.Tasks.Task ProbeTargetAsync()
    {
        var version = ++_probeVersion;
        var path = TargetPath.Trim();
        var hasContent = path.Length > 0 && await System.Threading.Tasks.Task.Run(() => _folderHasContent(path));
        if (version != _probeVersion)
        {
            return;   // the box changed while the disk answered
        }

        _probedPath = path;
        _probedHasContent = hasContent;
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(ValidationMessage));
    }

    /// <summary>
    /// The verdict of the last probe, if it was about the path in the box now. Until the probe of a new
    /// path answers, the folder counts as empty: the button does not flicker while typing, and no
    /// message names a folder the verdict was not about.
    /// </summary>
    private bool TargetHasContent =>
        _probedHasContent && string.Equals(_probedPath, TargetPath.Trim(), StringComparison.Ordinal);

    private void SetDerived(Action assign)
    {
        _updatingDerived = true;
        try { assign(); }
        finally { _updatingDerived = false; }
    }

    private string ComposePath(string branch)
    {
        if (_worktreeRoot.Length == 0 || branch.Trim().Length == 0) return string.Empty;

        var folder = GitRefName.DirectoryName(branch);
        return folder.Length == 0 ? string.Empty : Path.Combine(_worktreeRoot, folder);
    }

    /// <summary>True when the branch already exists, so the worktree checks it out rather than creating it.</summary>
    public bool BranchExists =>
        _existingBranches.Any(b => string.Equals(b, Branch, StringComparison.Ordinal));

    /// <summary>The checkout that already has the branch, or null. A second checkout of it is
    /// impossible, and the launcher used to find that out only from git's error afterwards.</summary>
    public string? BranchHolder =>
        _heldBranches.TryGetValue(Branch.Trim(), out var holder) ? holder : null;

    public bool CanConfirm
    {
        get
        {
            if (SelectedAgent is null || Task.Trim().Length == 0) return false;
            if (!CreateWorktree) return true;

            return GitRefName.IsValidBranch(Branch)
                   && BranchHolder is null
                   && TargetPath.Length > 0
                   && !TargetHasContent;
        }
    }

    /// <summary>Why the button is off, in one line — and silent while the form is still empty.</summary>
    public string ValidationMessage
    {
        get
        {
            if (Task.Trim().Length == 0) return string.Empty;
            if (!CreateWorktree) return string.Empty;

            if (Branch.Length == 0 && TargetPath.Length == 0)
                return LocalizationManager.Get("Git_WtTitleNoSlug");
            if (!GitRefName.IsValidBranch(Branch))
                return LocalizationManager.Get("Git_WtInvalidBranch");
            if (BranchHolder is { } holder)
            {
                return LocalizationManager.Format("AgentLaunch_BranchHeld", Branch.Trim(), holder);
            }

            if (TargetPath.Length == 0)
                return LocalizationManager.Get("Git_WtNoPath");
            if (TargetHasContent)
                return string.Format(LocalizationManager.Get("Git_WtPathNotEmpty"), TargetPath);

            return string.Empty;
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
            return false;
        }
    }

    public AgentLaunchResult? Result { get; private set; }
    public bool DialogResult { get; private set; }
    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void Confirm()
    {
        if (!CanConfirm || SelectedAgent is null) return;

        Result = new AgentLaunchResult(
            Task.Trim(),
            SelectedAgent,
            CreateWorktree,
            CreateWorktree ? Branch.Trim() : string.Empty,
            CreateWorktree ? Path.GetFullPath(TargetPath.Trim()) : _currentDirectory,
            CarryFiles && CreateWorktree,
            RunSetupCommand && HasSetupCommand && CreateWorktree,
            CreateWorkspace && CreateWorktree);

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
