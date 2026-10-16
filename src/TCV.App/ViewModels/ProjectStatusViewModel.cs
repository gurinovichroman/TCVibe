using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using TCV.Ai.Agents;
using TCV.Ai.Llm;
using TCV.App.Agents;
using TCV.App.Localization;
using TCV.App.Services;
using TCV.Git;

namespace TCV.App.ViewModels;

/// <summary>
/// What the window's chrome says about the project under the active panel: the branch and how many
/// files changed (toolbar, status bar), what the latest agent run left to review (the strip under the
/// panels), who changed each file (the origin marks in the lists), whether operations are running and
/// whether the AI and MCP layers are on.
/// <para>
/// Everything is read off the UI thread (git status, the operation log, the session store: INV-06) and
/// published here as plain values. A request is debounced: a panel that walks through folders, or an
/// operation log that records ten moves in a row, costs one status pass.
/// </para>
/// </summary>
public sealed partial class ProjectStatusViewModel : ObservableObject
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(350);

    /// <summary>While an agent session is open its edits land with nobody telling us; look again this often.</summary>
    private static readonly TimeSpan WhileAgentRuns = TimeSpan.FromSeconds(15);

    private readonly GitWriteService _git;
    private readonly OperationLog _operationLog;
    private readonly AgentSessionStore _sessions;
    private readonly McpServerHost _mcp;
    private readonly LlmClient _llm;
    private readonly Func<int> _runningOperations;
    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _pulse;

    private string? _folder;
    private int _generation;
    private DateTime _nextAgentPoll = DateTime.MaxValue;
    private AgentRunSummary? _run;

    public ProjectStatusViewModel(
        GitWriteService git,
        OperationLog operationLog,
        AgentSessionStore sessions,
        McpServerHost mcp,
        LlmClient llm,
        Func<int> runningOperations)
    {
        _git = git;
        _operationLog = operationLog;
        _sessions = sessions;
        _mcp = mcp;
        _llm = llm;
        _runningOperations = runningOperations;

        _debounce = new DispatcherTimer { Interval = Debounce };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = RefreshAsync();
        };

        // The queue and the LLM settings have no events: a cheap look once a second keeps the status
        // bar honest without threading notifications through the operation worker.
        _pulse = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pulse.Tick += (_, _) => OnPulse();
        _pulse.Start();

        _operationLog.Changed += (_, _) => Post(Request);
        _mcp.StateChanged += (_, _) => Post(UpdateServices);
        LocalizationManager.LanguageChanged += (_, _) => UpdateTexts();
        UpdateServices();
        UpdateTexts();
    }

    /// <summary>Raised when the per-file origin marks changed; the panels re-mark their rows.</summary>
    public event EventHandler? OriginsChanged;

    /// <summary>Who changed what in the current repository; <see cref="FileOriginIndex.Empty"/> outside one.</summary>
    public FileOriginIndex Origins { get; private set; } = FileOriginIndex.Empty;

    // ── Repository ───────────────────────────────────────────────────────────

    [ObservableProperty] private bool _hasRepository;

    /// <summary>The working tree's folder name — the title bar's project when no workspace is active.</summary>
    [ObservableProperty] private string _repositoryName = string.Empty;
    [ObservableProperty] private string _branch = string.Empty;
    [ObservableProperty] private int _changeCount;
    [ObservableProperty] private string _changeCountText = string.Empty;
    [ObservableProperty] private int _agentChangeCount;

    /// <summary>The «agent changes» count is worth a badge: there is something to review.</summary>
    public bool HasAgentChanges => AgentChangeCount > 0;

    partial void OnAgentChangeCountChanged(int value) => OnPropertyChanged(nameof(HasAgentChanges));

    // ── Latest agent run ─────────────────────────────────────────────────────

    [ObservableProperty] private bool _hasAgentRun;
    [ObservableProperty] private string _agentTitle = string.Empty;
    [ObservableProperty] private string _agentTool = string.Empty;
    [ObservableProperty] private string _agentFiles = string.Empty;
    [ObservableProperty] private string _agentWhen = string.Empty;
    [ObservableProperty] private string _linesAdded = string.Empty;
    [ObservableProperty] private string _linesRemoved = string.Empty;
    [ObservableProperty] private bool _hasLineStats;
    [ObservableProperty] private bool _agentConfirmed;
    [ObservableProperty] private string _agentEvidence = string.Empty;
    [ObservableProperty] private string _agentEvidenceHint = string.Empty;

    // ── Operations and services ──────────────────────────────────────────────

    [ObservableProperty] private bool _operationsRunning;
    [ObservableProperty] private string _operationsText = string.Empty;
    [ObservableProperty] private string _aiText = string.Empty;
    [ObservableProperty] private string _mcpText = string.Empty;

    /// <summary>The folder the active panel works in (null when it is not on the disk).</summary>
    public void Track(string? folder)
    {
        if (string.Equals(folder, _folder, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _folder = folder;
        Request();
    }

    /// <summary>Look again soon: something may have changed (window activated, an operation finished).</summary>
    public void Request()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private async Task RefreshAsync()
    {
        var generation = ++_generation;
        var folder = _folder;

        if (string.IsNullOrEmpty(folder))
        {
            Apply(GitWorkingTreeStatus.Empty, Array.Empty<AgentChangeRow>(), null, GitLineStats.None);
            return;
        }

        try
        {
            var (status, rows, run, lines) = await Task.Run(async () =>
            {
                var gitStatus = await _git.GetStatusAsync(folder).ConfigureAwait(false);
                if (!gitStatus.IsRepository)
                {
                    return (gitStatus, (IReadOnlyList<AgentChangeRow>)Array.Empty<AgentChangeRow>(), (AgentRunSummary?)null, GitLineStats.None);
                }

                var sessions = _sessions.Load();
                var built = AgentChangeSet.Build(gitStatus, _operationLog.LoadAll(), sessions, DateTime.UtcNow);
                var summary = AgentRunSummary.Build(gitStatus.RepositoryRoot, built, sessions);
                var stats = summary is { FileCount: > 0 }
                    ? await _git.GetLineStatsAsync(gitStatus.RepositoryRoot, summary.RelativePaths).ConfigureAwait(false)
                    : GitLineStats.None;
                return (gitStatus, built, summary, stats);
            });

            if (generation != _generation)
            {
                return;   // a newer request already answered
            }

            Apply(status, rows, run, lines);
        }
        catch (Exception)
        {
            // A status strip must not take the window down: a repository we cannot read shows nothing.
            if (generation == _generation)
            {
                Apply(GitWorkingTreeStatus.Empty, Array.Empty<AgentChangeRow>(), null, GitLineStats.None);
            }
        }
    }

    private void Apply(GitWorkingTreeStatus status, IReadOnlyList<AgentChangeRow> rows, AgentRunSummary? run, GitLineStats lines)
    {
        HasRepository = status.IsRepository;
        RepositoryName = status.IsRepository
            ? System.IO.Path.GetFileName(status.RepositoryRoot.TrimEnd('\\', '/'))
            : string.Empty;
        Branch = status.Branch;
        ChangeCount = status.ChangedPathCount;
        AgentChangeCount = rows
            .Where(r => r.Origin is ChangeOrigin.Agent or ChangeOrigin.Supervised)
            .Select(r => r.RelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        _run = run;
        HasAgentRun = run is not null;
        LinesAdded = "+" + lines.Added.ToString("N0", LocalizationManager.Culture);
        LinesRemoved = "−" + lines.Removed.ToString("N0", LocalizationManager.Culture);
        HasLineStats = run is { FileCount: > 0 } && (lines.Added > 0 || lines.Removed > 0);
        AgentConfirmed = run?.Confirmed ?? false;
        _nextAgentPoll = run is { IsRunning: true } ? DateTime.UtcNow + WhileAgentRuns : DateTime.MaxValue;

        var origins = status.IsRepository ? FileOriginIndex.Build(status.RepositoryRoot, rows) : FileOriginIndex.Empty;
        if (!(origins.IsEmpty && Origins.IsEmpty))
        {
            Origins = origins;
            OriginsChanged?.Invoke(this, EventArgs.Empty);
        }

        UpdateTexts();
    }

    private void OnPulse()
    {
        var running = _runningOperations();
        if (OperationsRunning != running > 0 || running > 0)
        {
            OperationsRunning = running > 0;
            OperationsText = running > 0
                ? LocalizationManager.Format("Shell_OperationsRunning", running)
                : LocalizationManager.Get("Shell_OperationsIdle");
        }

        UpdateServices();
        if (DateTime.UtcNow >= _nextAgentPoll)
        {
            _nextAgentPoll = DateTime.MaxValue;
            Request();
        }

        // «2 minutes ago» ages while nobody refreshes.
        if (_run is not null)
        {
            AgentWhen = When(_run);
        }
    }

    private void UpdateServices()
    {
        AiText = LocalizationManager.Get(_llm.IsConfigured ? "Shell_AiOn" : "Shell_AiOff");
        McpText = LocalizationManager.Get(_mcp.IsRunning ? "Shell_McpOn" : "Shell_McpOff");
    }

    private void UpdateTexts()
    {
        ChangeCountText = LocalizationManager.Format("Shell_Changes", ChangeCount);
        OperationsText = OperationsRunning
            ? LocalizationManager.Format("Shell_OperationsRunning", _runningOperations())
            : LocalizationManager.Get("Shell_OperationsIdle");
        UpdateServices();

        if (_run is not { } run)
        {
            return;
        }

        AgentTitle = LocalizationManager.Get(run.IsRunning ? "Shell_AgentRunning" : "Shell_AgentDone");
        AgentTool = run.Tool;
        AgentFiles = LocalizationManager.Format("Shell_AgentFiles", run.FileCount);
        AgentWhen = When(run);
        AgentEvidence = LocalizationManager.Get(run.Confirmed ? "Shell_AgentConfirmed" : "Shell_AgentGuessed");
        AgentEvidenceHint = LocalizationManager.Get(run.Confirmed ? "Shell_AgentConfirmedHint" : "Shell_AgentGuessedHint");
    }

    private static string When(AgentRunSummary run)
    {
        var (unit, count) = RelativeTime.Describe(DateTime.UtcNow - run.AtUtc);
        return unit switch
        {
            TimeAgoUnit.Minutes => LocalizationManager.Format("Time_MinutesAgo", count),
            TimeAgoUnit.Hours => LocalizationManager.Format("Time_HoursAgo", count),
            TimeAgoUnit.Days => LocalizationManager.Format("Time_DaysAgo", count),
            _ => LocalizationManager.Get("Time_JustNow"),
        };
    }

    private static void Post(Action action) =>
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(action);
}
