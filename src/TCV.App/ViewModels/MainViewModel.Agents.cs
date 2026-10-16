using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TCV.Ai.Agents;
using TCV.Ai.Mcp;
using TCV.Ai.Rename;
using TCV.App.Agents;
using TCV.App.Localization;
using TCV.App.Navigation;
using TCV.App.Operations;
using TCV.App.Operations.Conflict;
using TCV.App.Services;
using TCV.App.Settings;
using TCV.App.ViewModels.Dialogs;
using TCV.Core.Domain;
using TCV.Core.Interfaces;
using TCV.Git;
using TCV.Search;

namespace TCV.App.ViewModels;

/// <summary>
/// The agent-aware layer's window half (Этап 6D): the review dashboard, the agent launcher, the
/// supervised-write plumbing, and the AI features that fill an editable field.
/// <para>
/// In its own file for the same reason as <c>MainViewModel.Git.cs</c> — this is a self-contained group
/// that task 6.26 will lift out wholesale (TRAP-17). Everything substantial lives in
/// <see cref="AgentServices"/> and <c>TCV.Ai</c>; what stays here is genuinely the window's business:
/// which panel to move, which dialog to raise, which operation to enqueue.
/// </para>
/// </summary>
public partial class MainViewModel : IAgentHost
{
    // ─────────────────────────── 6.12 — the review surface ───────────────────────────

    /// <summary>
    /// Ctrl+Shift+D — «Agent changes»: what changed in the working tree, who wrote it, and one-click
    /// revert. Opens anywhere: the same window holds the MCP server, its registration line and the
    /// approved folders, and refusing it outside a repository left no way to set those up. The
    /// change list then simply says there is no repository. A mounted panel has no real path to give
    /// git (INV-07), so the disk folder it was opened from stands in for it.
    /// </summary>
    [RelayCommand]
    private async Task OpenAgentChanges()
    {
        var path = ActivePanel.FileList.IsMounted
            ? ActivePanel.FileList.RootLocalPath
            : ActivePanel.CurrentPath;

        var vm = new AgentChangesDialogViewModel(
            _git.Write,
            path,
            _agents.Sessions,
            _operationLog,
            _agents.Gate,
            _agents.Mcp,
            ConfirmRevert,
            DiscardUntrackedAsync,
            ShowGitDiffFor,
            PickApprovedFolder,
            ConfirmBroadRoot,
            reachableMounts: DescribeAgentReachableMounts);

        var (action, chosen) = await _dialogs.ShowAgentChangesAsync(vm);

        if (action == AgentChangesAction.ShowInPanel && chosen.Length > 0)
            NavigateActiveToDisk(Path.GetDirectoryName(chosen) ?? chosen, Path.GetFileName(chosen));
        else
            RefreshAfterGitWrite();
    }

    /// <summary>
    /// Names what a revert destroys. Reverting is the one action in the dashboard that loses work, so
    /// it is spelled out rather than summarised (INV-11) — and the two cases really are different, so
    /// the prompt says which one is about to happen.
    /// </summary>
    private bool ConfirmRevert(IReadOnlyList<AgentChangeRow> rows)
    {
        if (rows.Count == 0) return false;

        var row = rows[0];

        // A rename is reverted on both names, so the prompt names both.
        var subject = row.OldRelativePath.Length > 0
            ? $"{row.OldRelativePath} → {row.RelativePath}"
            : row.RelativePath;

        // The delete goes through DeleteOperation with the recycle-bin setting, so the prompt says what
        // that setting will actually do rather than promising the recycle bin unconditionally.
        var message = row.RevertMeansDelete
            ? string.Format(LocalizationManager.Get("Agent_ConfirmDelete"), subject) + " "
              + LocalizationManager.Get(
                  _settings.Operations.MoveToRecycleBin ? "Agent_DeleteRecycle" : "Agent_DeletePermanent")
            : string.Format(LocalizationManager.Get("Agent_ConfirmRevert"), subject);

        // Who the dashboard thinks wrote it, and how sure it is. The attribution is a guess for every
        // level below «Supervised», so the file may hold the user's own uncommitted work — the prompt
        // says so rather than letting «Agent» read as a fact.
        message += "\n\n" + string.Format(
            LocalizationManager.Get("Agent_ConfirmOrigin"), row.OriginText, row.OriginHint);

        // The button names the action that happens: a revert, or a delete for an untracked file.
        return Views.Dialogs.ConfirmDialog.Ask(
            LocalizationManager.Get("Agent_Revert"),
            message,
            LocalizationManager.Get(row.RevertMeansDelete ? "Agent_DeleteButton" : "Agent_RevertButton"),
            LocalizationManager.Get("Cancel"),
            isWarning: true);
    }

    /// <summary>
    /// Removes untracked files through the operation queue instead of through git. There is nothing in
    /// HEAD to restore them to, so "revert" here means "delete" — and a deletion the manager performs
    /// belongs in the queue and the log like any other (INV-10), which also makes it undoable.
    /// </summary>
    private async Task<bool> DiscardUntrackedAsync(IReadOnlyList<AgentChangeRow> rows)
    {
        var entries = new List<IFileEntry>();
        foreach (var row in rows)
        {
            if (await _fs.GetEntryAsync(new VfsPath(row.FullPath)) is { } entry) entries.Add(entry);
        }

        if (entries.Count == 0) return false;

        var op = new DeleteOperation(_fs, entries, _settings.Operations.MoveToRecycleBin, _deleteLogger);
        await _operationQueue.EnqueueAsync(op);

        if (op.State != OperationState.Completed)
        {
            await _dialogs.ShowErrorAsync(
                LocalizationManager.Get("Agent_Revert"),
                op.FailureException?.Message ?? LocalizationManager.Get("Agent_RevertFailed"));
            return false;
        }

        return true;
    }

    /// <summary>Opens the git diff window for one file, from the dashboard. The repository is read
    /// on a background thread by the dialog, never here.</summary>
    private void ShowGitDiffFor(string fullPath) =>
        _ = _dialogs.ShowGitDiffAsync(_gitStatusProvider, fullPath);

    /// <summary>
    /// Second confirmation for a directory that is too wide to hand to an agent (a drive root, the user
    /// profile). «No» is the default button: a click-through must not approve a whole disk.
    /// </summary>
    private static bool ConfirmBroadRoot(string folder) =>
        Views.Dialogs.ConfirmDialog.Ask(
            LocalizationManager.Get("Agent_ApproveFolder"),
            string.Format(LocalizationManager.Get("Agent_BroadRootConfirm"), folder),
            isWarning: true);

    /// <summary>Asks for a directory to expose to agents. Starts in the active panel, the likely answer.</summary>
    private string? PickApprovedFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = LocalizationManager.Get("Agent_ApproveFolder"),
            InitialDirectory = Directory.Exists(ActivePanel.CurrentPath) ? ActivePanel.CurrentPath : string.Empty,
        };

        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    // ─────────────────────────── 6.13a — the agent launcher ───────────────────────────

    /// <summary>
    /// Ctrl+Shift+R — run an agent on a task: a worktree of its own, a recorded session, and the
    /// review surface waiting for it. This is the difference between «start claude here» and actually
    /// being able to check what it did afterwards.
    /// </summary>
    [RelayCommand]
    private async Task LaunchAgent()
    {
        var title = LocalizationManager.Get("AgentLaunch_Title");
        if (await RejectIfNotOnDiskAsync(title))
        {
            return;
        }

        // Detection walks PATH × PATHEXT, and a PATH entry on a dead share hangs it: off the UI
        // thread (INV-06), together with the probe that decides whether the default is installed.
        var (tools, agentChoice) = await Task.Run(() =>
        {
            var available = _externalTools.AvailableAgents();
            return (available, _externalTools.ChooseAgent(available));
        });
        if (tools.Count == 0)
        {
            await _dialogs.ShowErrorAsync(title, LocalizationManager.Get("Agent_NoneFound"));
            return;
        }

        // Search results work in the folder they were opened over (TRAP-28).
        var current = ActivePanel.FileList.WorkingFolder ?? ActivePanel.CurrentPath;
        var insideRepository = await Task.Run(() => GitHeadReader.FindGitDirectory(current) is not null);
        var repositoryRoot = insideRepository ? await RepositoryRootOfAsync(current) : current;

        // With their checkout state: a branch another worktree holds (found through the main
        // repository, TRAP-25/TRAP-42) is refused in the form instead of by git afterwards.
        var branches = insideRepository
            ? await _git.Write.ListBranchDetailsAsync(current)
            : Array.Empty<GitBranchInfo>();

        var vm = new AgentLaunchDialogViewModel(
            tools,
            agentChoice,
            current,
            insideRepository
                ? WorktreeSetupService.SuggestWorktreeRoot(repositoryRoot, _settings.Git.WorktreeRoot)
                : string.Empty,
            branches.Select(b => b.Name).ToList(),
            AgentLaunchDialogViewModel.HeldBranches(branches, repositoryRoot),
            _settings.Git.WorktreeSetupCommand,
            _settings.Git.WorktreeCreatesWorkspace,
            insideRepository);

        var request = await _dialogs.ShowAgentLaunchAsync(vm);
        if (request is null) return;

        var directory = request.TargetPath;

        if (request.CreateWorktree)
        {
            var created = await _git.Worktrees.AddAsync(new GitWorktreeRequest(
                current, request.Branch, !vm.BranchExists, string.Empty, request.TargetPath));

            if (!created.Success)
            {
                // The service re-checks who holds the branch (another window or agent may have
                // taken it since the form was filled) and classifies the rest: in the user's language.
                await _dialogs.ShowErrorAsync(
                    title, LocalizationManager.Format("Git_WtCreateFailed", GitResultText.Describe(created)));
                return;
            }

            var setup = await _git.Setup.RunAsync(
                repositoryRoot, request.TargetPath, _settings.Git,
                request.CarryFiles, request.RunSetupCommand);

            _logger.LogInformation(
                "Agent worktree {Path} ready: {Copied} file(s) carried", request.TargetPath, setup.CopiedFiles);

            // The panel moves before the workspace is captured — a workspace snapshots the panels.
            // The workspace being left keeps its latest layout first, as a switch would (TRAP-51).
            // Both only when a workspace is wanted: the «new worktree» setting decides, as it does there.
            if (request.CreateWorkspace)
            {
                Workspaces.SaveActiveSnapshot();
            }

            NavigateActiveToDisk(request.TargetPath);

            if (request.CreateWorkspace)
            {
                CreateWorktreeWorkspace(
                    new NewWorktreeResult(request.Branch, !vm.BranchExists, string.Empty,
                        request.TargetPath, request.CarryFiles, request.RunSetupCommand, CreateWorkspace: true),
                    repositoryRoot);
            }
        }

        if (!StartAgentSession(request.Tool, directory, request.Task))
        {
            await _dialogs.ShowErrorAsync(
                title,
                string.Format(
                    LocalizationManager.Get("Tool_LaunchFailed"), request.Tool.Name, request.Tool.Command));
            return;
        }

        // Agent CLIs take their first prompt in incompatible ways (positional, --message, -i, stdin), so
        // the task goes where every one of them can get it: the clipboard. Saying nothing would leave a
        // dialog called «launch an agent on a task» that starts the agent with no task at all.
        var onClipboard = TryPutOnClipboard(request.Task);

        await _dialogs.ShowInfoAsync(
            title,
            string.Format(LocalizationManager.Get("AgentLaunch_Started"), request.Tool.Name, directory)
            + (onClipboard ? "\n\n" + LocalizationManager.Get("AgentLaunch_TaskOnClipboard") : string.Empty));
    }

    /// <summary>Copies text to the clipboard; false when another program holds it (a COMException is routine there).</summary>
    private static bool TryPutOnClipboard(string text)
    {
        if (text.Length == 0) return false;

        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Records the session and starts the agent. The record comes first on purpose: if the launch
    /// fails the session is closed again, whereas starting first would leave a window in which the
    /// agent is writing and nothing knows it exists.
    /// </summary>
    internal bool StartAgentSession(ExternalTool tool, string directory, string task)
    {
        var session = _agents.Sessions.Start(tool.Name, directory, task);

        if (_externalTools.Launch(tool, directory, Array.Empty<string>())) return true;

        _agents.Sessions.Close(session.Id);
        return false;
    }

    /// <summary>
    /// Puts the MCP switch and port from Settings into effect: they were read only at startup, so
    /// unticking "Enable the MCP server" left the socket listening until the next launch. Acts on
    /// changes only — saving unrelated settings must not start a server the user left stopped.
    /// </summary>
    internal async Task ApplyMcpSettingsAsync(bool wasEnabled, int oldPort)
    {
        var mcp = _agents.Mcp;
        var enabled = _settings.Agent.McpEnabled;
        if (!mcp.CanConfigure || (enabled == wasEnabled && _settings.Agent.McpPort == oldPort))
        {
            return;
        }

        if (!enabled)
        {
            await mcp.StopAsync();
            return;
        }

        // A new port on a server the user left stopped is for its next start, not a reason to start it.
        if (enabled == wasEnabled && !mcp.IsRunning)
        {
            return;
        }

        // A new port means a new listener; an unchanged one with the switch just turned on, a start.
        if (mcp.IsRunning)
        {
            await mcp.StopAsync();
        }

        if (!mcp.Start())
        {
            await _dialogs.ShowErrorAsync(LocalizationManager.Get("Settings_AgentMcp"), mcp.LastErrorText);
        }
    }

    // ─────────────────────────── 6.15 — content-aware rename ───────────────────────────

    /// <summary>
    /// Ctrl+K Ctrl+N — propose names from content for the selected files. Guarded to the junk corner:
    /// this is for scans and downloads, and <see cref="JunkCornerGuard"/> refuses a source tree
    /// outright rather than offering to rename code (ADR-0008).
    /// </summary>
    [RelayCommand]
    private async Task AiRenameSelection()
    {
        var title = LocalizationManager.Get("AiRename_Title");
        if (await RejectIfNotOnDiskAsync(title))
        {
            return;
        }

        var selected = GetSelectedOrCurrent().Where(e => !e.IsDirectory).ToList();
        if (selected.Count == 0)
        {
            await _dialogs.ShowErrorAsync(title, LocalizationManager.Get("AiRename_NoSelection"));
            return;
        }

        if (!_agents.Llm.IsConfigured)
        {
            await _dialogs.ShowErrorAsync(title, LocalizationManager.Get("Llm_NotConfigured"));
            return;
        }

        // The folder the files are renamed in. Search hits come from many folders and their list is
        // none (TRAP-28): there it is the one folder the selected files share.
        var fileList = ActivePanel.FileList;
        var directory = fileList.DiskFolder
            ?? (FileSystem.SearchResultsFileSystem.SharedFolder(selected) is { } shared
                ? FileSystem.WindowsFileSystem.ToWindowsPath(shared)
                : null);
        if (directory is null)
        {
            await _dialogs.ShowErrorAsync(title, LocalizationManager.Get("AiRename_OneFolder"));
            return;
        }

        var verdict = JunkCornerGuard.Evaluate(
            Path.GetFileName(directory.TrimEnd('\\', '/')),
            selected.Select(e => e.Name).ToList(),
            GitHeadReader.FindGitDirectory(directory) is not null);

        if (!verdict.Allowed)
        {
            await _dialogs.ShowErrorAsync(title, JunkCornerMessage(verdict));
            return;
        }

        // The guard judges the folder; a stray script or config file in an allowed folder is still code
        // and is left out, never offered for renaming (ADR-0008).
        var renamable = JunkCornerGuard.RenamableNames(selected.Select(e => e.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = selected.Where(e => renamable.Contains(e.Name)).ToList();
        if (candidates.Count == 0)
        {
            await _dialogs.ShowErrorAsync(title, string.Format(
                LocalizationManager.Get("AiRename_GuardSource"), selected.Count, selected.Count));
            return;
        }

        var batch = candidates.Take(AiRenamePlan.MaxBatch).ToList();

        // The excerpts leave the machine unless the endpoint is local: asked once per host, before
        // anything is read or sent — the dialog starts proposing the moment it opens.
        if (!await ConfirmRenameUploadAsync(batch.Count)) return;

        var files = new List<(string Name, string Excerpt)>();
        foreach (var entry in batch)
            files.Add((entry.Name, await ReadExcerptAsync(entry)));

        // What was left out, said in the window rather than silently: code files, and whatever did not
        // fit in one batch.
        var notices = new List<string>(2);
        if (selected.Count > candidates.Count)
            notices.Add(LocalizationManager.Format("AiRename_SkippedCode", selected.Count - candidates.Count));
        if (candidates.Count > batch.Count)
            notices.Add(string.Format(LocalizationManager.Get("AiRename_Capped"), batch.Count, candidates.Count));

        // Every name in the folder, not only the selection: a proposal must not land on a file that is
        // sitting next to the selected ones. The search-results list shows hits, not that folder, so
        // there the folder is listed itself.
        var directoryNames = fileList.DiskFolder is not null
            ? fileList.Files.Select(f => f.Name).ToList()
            : await ListNamesAsync(directory, selected);

        // Through the panel's own file system, so a renamed search hit stays in the list (TRAP-28).
        var renameFs = fileList.ActiveVfs;

        // The batch that was applied last, kept so the dialog can take it back.
        RenameOperation? applied = null;

        var vm = new AiRenameDialogViewModel(
            _agents.Llm,
            files,
            directoryNames,
            async pairs =>
            {
                applied = await ApplyAiRenamesAsync(renameFs, directory, pairs);
                return (applied.Performed.Count, applied.Failures.Count == 0
                    ? string.Empty
                    : LocalizationManager.Format("AiRename_PartialFailure", applied.Failures.Count, applied.Failures[0]));
            },
            async () =>
            {
                if (applied is null) return 0;

                // Through the queue and the log like the rename itself (INV-10).
                var undo = applied.CreateUndo();
                applied = null;
                await _operationQueue.EnqueueAsync(undo);
                return undo.NotPerformedCount;
            });

        vm.Notice = string.Join(" ", notices);

        await _dialogs.ShowAiRenameAsync(vm);

        // Unconditional: an undo inside the dialog changes the folder as much as an apply does.
        await ActivePanel.FileList.LoadAsync(ActivePanel.CurrentPath);
    }

    /// <summary>The localised sentence for a refusal from <see cref="JunkCornerGuard"/>.</summary>
    private static string JunkCornerMessage(JunkCornerVerdict verdict) => verdict.Code switch
    {
        JunkCornerReason.NothingSelected => LocalizationManager.Get("AiRename_NoSelection"),
        JunkCornerReason.InsideRepository => LocalizationManager.Get("AiRename_GuardRepository"),
        JunkCornerReason.ProjectDirectory =>
            string.Format(LocalizationManager.Get("AiRename_GuardProjectDir"), verdict.Subject),
        JunkCornerReason.SourceFiles =>
            string.Format(LocalizationManager.Get("AiRename_GuardSource"), verdict.CodeFiles, verdict.Total),
        _ => verdict.Reason,
    };

    /// <summary>
    /// A text excerpt for the model to name the file from. Binary formats yield nothing and are sent
    /// as "no readable text": guessing a name for a JPEG from its bytes is not something to pretend at.
    /// </summary>
    private async Task<string> ReadExcerptAsync(IFileEntry entry)
    {
        try
        {
            await using var stream = await _fs.OpenReadAsync(entry.Path);
            var buffer = new byte[AiRenamePlan.MaxExcerptChars * 2];
            var read = await stream.ReadAsync(buffer);
            if (read == 0) return string.Empty;

            var payload = buffer.AsSpan(0, read).ToArray();

            // Trimmed before detection, not after: DetectEncoding validates UTF-8 strictly, and a
            // buffer cut mid-sequence would be mis-detected as Latin-1 (see AgentBridge.ReadAsync).
            var safe = TextProbe.Utf8SafeLength(payload);
            if (safe < payload.Length) payload = payload[..safe];

            if (TextProbe.LooksBinary(payload)) return string.Empty;

            var (encoding, _) = TextProbe.DetectEncoding(payload);
            return TextProbe.DecodeText(payload, encoding);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read an excerpt of {Path}", entry.Path.Value);
            return string.Empty;
        }
    }

    /// <summary>
    /// Performs the accepted renames as one <see cref="RenameOperation"/> through the queue (INV-10):
    /// it lands in the operation log, a failure on one file does not stop the rest (a batch that
    /// stopped at the first failure would leave the preview table lying about what happened), and the
    /// returned operation can undo the whole batch.
    /// </summary>
    private async Task<RenameOperation> ApplyAiRenamesAsync(
        IVirtualFileSystem fs, string directory, IReadOnlyList<(string From, string To)> pairs)
    {
        var operation = new RenameOperation(fs, new VfsPath(directory), pairs, _logger);
        await _operationQueue.EnqueueAsync(operation);
        return operation;
    }

    /// <summary>
    /// Takes a rename batch recorded in the operation log back — the AI rename's «undo after» once its
    /// window is closed (ADR-0008, 6.55). The folder may have changed since, so what can be restored is
    /// worked out against it first (<see cref="RenameUndoPlan"/>), the user is asked with the exact
    /// count (INV-11), and the renames go through the queue and into the log like the batch did (INV-10).
    /// </summary>
    internal async Task UndoLoggedRenameAsync(RenameBatchRecord batch)
    {
        var title = LocalizationManager.Get("Log_UndoRename");
        var directory = new VfsPath(batch.Directory);
        var shown = directory.Value.Replace('/', '\\');

        // The AI rename refuses a mounted panel, so its batches are disk folders; a hand-edited line
        // that is anything else is not something to rename in.
        var names = Path.IsPathFullyQualified(shown)
            ? await ListNamesForUndoAsync(directory)
            : Array.Empty<string>();

        var plan = RenameUndoPlan.Build(batch.Pairs, names);
        if (plan.Pairs.Count == 0)
        {
            await _dialogs.ShowErrorAsync(title, string.Format(LocalizationManager.Get("Log_UndoRenameNothing"), shown));
            return;
        }

        var question = LocalizationManager.Format("Log_UndoRenameConfirm", plan.Pairs.Count, shown);
        if (plan.SkippedCount > 0)
        {
            question += "\n\n" + LocalizationManager.Format("Log_UndoRenameSkipped", plan.SkippedCount);
        }

        if (!await _dialogs.ConfirmAsync(title, question))
        {
            return;
        }

        // Through an open search-results list when there is one (TRAP-28): it forwards to the disk and
        // follows each hit to its restored name. Behind its back the hits read as deleted and leave it.
        var renameFs = new[] { ActivePanel, OppositePanel }
            .Select(p => p.FileList)
            .FirstOrDefault(f => f.IsSearchResults && f.EntriesOnDisk)?.ActiveVfs ?? _fs;

        var operation = new RenameOperation(renameFs, directory, plan.Pairs, _logger);
        using (LeasePanelFileSystems())
        {
            await _operationQueue.EnqueueAsync(operation);
        }

        // Either panel may be showing that folder.
        await LeftPanel.FileList.LoadAsync(LeftPanel.CurrentPath);
        await RightPanel.FileList.LoadAsync(RightPanel.CurrentPath);

        if (operation.Failures.Count > 0)
        {
            var failed = LocalizationManager.Format("AiRename_PartialFailure", operation.Failures.Count, operation.Failures[0]);
            await _dialogs.ShowErrorAsync(title, failed);
        }
    }

    /// <summary>Every name in <paramref name="directory"/> now; empty when it cannot be read.</summary>
    private async Task<IReadOnlyList<string>> ListNamesForUndoAsync(VfsPath directory)
    {
        try
        {
            var entries = await _fs.ListAsync(directory);
            return entries.Select(e => e.Name).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not list {Directory} to undo a rename", directory.Value);
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// The names in a disk folder, for a rename that must not land on one of them. When the folder
    /// cannot be read, the selection's own names: a proposal that collides with an unseen file then
    /// fails on rename (nothing is overwritten) instead of the whole command failing here.
    /// </summary>
    private async Task<List<string>> ListNamesAsync(string directory, IEnumerable<IFileEntry> fallback)
    {
        try
        {
            return (await _fs.ListAsync(new VfsPath(directory))).Select(e => e.Name).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not list {Directory} for AI rename", directory);
            return fallback.Select(e => e.Name).ToList();
        }
    }

    // ─────────────────────────── 6.16 — semantic search ───────────────────────────

    /// <summary>Ctrl+K Ctrl+F — ask in words over the local embedding index.</summary>
    [RelayCommand]
    private async Task OpenSemanticSearch()
    {
        var title = LocalizationManager.Get("Semantic_Title");
        if (await RejectIfNotOnDiskAsync(title))
        {
            return;
        }

        // The project root, not the current directory: an index scoped to whatever folder you happen
        // to be standing in would be rebuilt every time you navigate. Search results stand in the
        // folder they were opened over (TRAP-28).
        var root = await ProjectRootLocator.FindAsync(
            _fs, ActivePanel.FileList.WorkingFolder ?? ActivePanel.CurrentPath);

        var vm = new SemanticSearchDialogViewModel(
            _agents.Semantic,
            root,
            ct => CollectIndexableFilesAsync(root, ct),
            ConfirmEmbeddingUploadAsync,
            () => _dialogs.ConfirmAsync(title, LocalizationManager.Get("Semantic_ClearConfirm")));
        var action = await _dialogs.ShowSemanticSearchAsync(vm);

        switch (action)
        {
            case SemanticSearchAction.GoToFile when vm.ChosenPath.Length > 0:
                NavigateActiveToDisk(
                    Path.GetDirectoryName(vm.ChosenPath) ?? vm.ChosenPath,
                    Path.GetFileName(vm.ChosenPath));
                break;

            case SemanticSearchAction.ShowAllInPanel:
                await FeedSemanticHitsToPanelAsync(vm.HitPaths);
                break;
        }
    }

    /// <summary>
    /// The files semantic indexing will consider: the same gitignore-aware walk the Ctrl+P index uses
    /// (ADR-0016 — one implementation of "which files matter in this project", not two).
    /// </summary>
    private async Task<(IReadOnlyList<string> Files, bool Complete)> CollectIndexableFilesAsync(
        string root, CancellationToken ct)
    {
        var walker = new FileIndexWalker(_fs, new FileIndexOptions
        {
            RespectGitignore = true,
            IncludeDirectories = false,
            MaxEntries = 20_000,

            // A junction or symlink inside the project can lead anywhere; what it reaches would be
            // embedded and later served to agents as if it were part of the project.
            SkipReparsePoints = true,
        });

        var files = new List<string>();
        await foreach (var entry in walker.EnumerateAsync(new VfsPath(root), ct))
            files.Add(FileSystem.WindowsFileSystem.ToWindowsPath(new VfsPath(entry.FullPath)));

        // A walk that hit its cap saw only part of the tree; the indexer must not read the rest as deleted.
        return (files, !walker.WasTruncated);
    }

    /// <summary>
    /// The one thing «the index stays on this machine» does not cover: indexing sends the text of every
    /// file to the embedding endpoint. A remote endpoint is asked about once (and remembered per host);
    /// one on this machine needs no question.
    /// </summary>
    private async Task<bool> ConfirmEmbeddingUploadAsync(int fileCount)
    {
        var options = _agents.Llm.Options;
        if (options.IsLocalEndpoint) return true;

        var host = options.EndpointHost;
        if (string.Equals(_settings.Agent.EmbeddingsConsentHost, host, StringComparison.OrdinalIgnoreCase))
            return true;

        var accepted = await _dialogs.ConfirmAsync(
            LocalizationManager.Get("Semantic_Title"),
            string.Format(LocalizationManager.Get("Semantic_UploadConfirm"), fileCount, host));

        if (!accepted) return false;

        _settings.Agent.EmbeddingsConsentHost = host;
        PersistState();
        return true;
    }

    /// <summary>
    /// AI rename sends a text excerpt of every file in the batch to the chat endpoint. A local one needs
    /// no question; a remote host is asked about once and remembered, as for semantic indexing.
    /// </summary>
    private async Task<bool> ConfirmRenameUploadAsync(int fileCount)
    {
        var options = _agents.Llm.Options;
        if (options.IsLocalEndpoint) return true;

        var host = options.EndpointHost;
        if (string.Equals(_settings.Agent.AiRenameConsentHost, host, StringComparison.OrdinalIgnoreCase))
            return true;

        var accepted = await _dialogs.ConfirmAsync(
            LocalizationManager.Get("AiRename_Title"),
            string.Format(LocalizationManager.Get("AiRename_UploadConfirm"), fileCount, host));

        if (!accepted) return false;

        _settings.Agent.AiRenameConsentHost = host;
        PersistState();
        return true;
    }

    /// <summary>Feeds semantic hits into the panel, reusing the search-results listing (TRAP-28).</summary>
    private async Task FeedSemanticHitsToPanelAsync(IReadOnlyList<string> paths)
    {
        var entries = new List<IFileEntry>();
        foreach (var path in paths)
        {
            var windowsPath = path.Replace('/', '\\');
            if (await _fs.GetEntryAsync(new VfsPath(windowsPath)) is { } entry) entries.Add(entry);
        }

        if (entries.Count > 0) await FeedSearchResultsToPanelAsync(entries);
    }

    // ─────────────────────────── IAgentHost (6.13 / 6.13b) ───────────────────────────

    /// <inheritdoc />
    public AgentPanelSnapshot SnapshotPanels() => OnUi(() =>
    {
        var fileList = ActivePanel.FileList;

        // A search-results listing is a mount for the panel but not for an agent: its entries are
        // ordinary disk files, addressed by their Windows paths like any other (and scope-checked).
        var activeMount = AgentMountOf(fileList);

        // The hits of a search on a server or in an archive are neither: no mount the agent may address,
        // and no Windows paths — "/var/www/a.php" would name C:\var\www\a.php (INV-07).
        var selection = activeMount is null && !fileList.EntriesOnDisk
            ? []
            : GetSelectedOrCurrent()
                .Select(e => activeMount is not null
                    ? e.Path.Value
                    : FileSystem.WindowsFileSystem.ToWindowsPath(e.Path))
                .ToList();

        return new AgentPanelSnapshot(
            AgentVisiblePath(fileList, ActivePanel.CurrentPath),
            AgentVisiblePath(OppositePanel.FileList, OppositePanel.CurrentPath),
            selection,
            Workspaces.ActiveWorkspace?.Name ?? string.Empty,
            ActivePanel.ActiveTab?.Branch ?? string.Empty,
            fileList.ActiveVfs.Scheme,
            activeMount,
            AgentMountOf(OppositePanel.FileList));
    });

    /// <summary>
    /// The mounts an agent can read through <c>panel://</c> right now, for the dashboard's «what is
    /// exposed» line — by the same rule the bridge applies (ADR-0023). Called on the UI thread, like
    /// the panels it reads.
    /// </summary>
    private IReadOnlyList<string> DescribeAgentReachableMounts()
    {
        var scope = _agents.Bridge.CurrentScope();
        var mounts = new List<string>(2);
        foreach (var (panel, name) in new[] { (ActivePanel, AgentPathRef.ActivePanel), (OppositePanel, AgentPathRef.OppositePanel) })
        {
            if (_agents.Bridge.MountReadable(AgentMountOf(panel.FileList), scope, name))
            {
                mounts.Add($"{AgentPathRef.PanelScheme}://{name} — {panel.FileList.ActiveVfs.DisplayName}");
            }
        }

        return mounts;
    }

    /// <summary>
    /// What a panel has mounted, as the agent's access rule sees it; null for the disk and for a
    /// search-results listing, which <c>panel://</c> does not address. The kind comes from the panel's
    /// mount, not from the file system's class (TRAP-02).
    /// </summary>
    private static AgentMountInfo? AgentMountOf(FileListViewModel fileList)
    {
        // Search results of a server or an archive search too: their view is neither, and the access
        // rule for the mount under it is not to be guessed from it.
        if (fileList.EntriesOnDisk || fileList.IsSearchResults)
        {
            return null;
        }

        return fileList.IsInsideArchive
            ? new AgentMountInfo(AgentMountKind.Archive, AgentArchiveFile(fileList.ActiveVfs))
            : new AgentMountInfo(AgentMountKind.Remote);
    }

    /// <summary>
    /// The archive's own file, or empty when the panel shows a scratch copy: an archive opened from
    /// inside another archive or from a server lives in <see cref="TemporaryFiles"/>, and checking that
    /// copy against the approved directories would let a root that holds the temp folder (the user
    /// profile) open an archive from anywhere — and a server with the server switch off (ADR-0023).
    /// </summary>
    private static string AgentArchiveFile(IVirtualFileSystem vfs) =>
        vfs is TCV.Archives.ArchiveFileSystem archive && !TemporaryFiles.Holds(archive.ArchivePath)
            ? archive.ArchivePath
            : string.Empty;

    /// <summary>
    /// The directory of a panel as an agent should see it. A search-results listing has no directory
    /// (its «/» is not a folder on any disk), so it is reported as empty rather than as a path that
    /// would resolve to the root of the current drive; a hit's folder opened from it is a disk folder
    /// and reported as one (scope-checked like any other).
    /// </summary>
    private static string AgentVisiblePath(FileListViewModel fileList, string currentPath) =>
        fileList.IsSearchResults
            ? fileList.DiskFolder ?? string.Empty
            : currentPath;

    /// <inheritdoc />
    public AgentPanelMount? PanelMount(bool active) => OnUi(() =>
    {
        var panel = active ? ActivePanel : OppositePanel;
        var fileList = panel.FileList;

        // Search results forward every call to the local disk, so exposing them through panel:// would
        // bypass the approved directories entirely.
        if (AgentMountOf(fileList) is not { } info)
        {
            return null;
        }

        // The lease is taken here, on the UI thread, together with the read of ActiveVfs: taking it
        // afterwards from the agent's thread would leave a gap in which the panel is navigated away
        // and the mount disposed under the request (INV-09, TRAP-05).
        return new AgentPanelMount(
            fileList.ActiveVfs, panel.CurrentPath, info, new UiLease(fileList.LeaseActiveVfs()));
    });

    /// <summary>
    /// A panel lease that is released back on the UI thread, wherever it is disposed. Agent requests
    /// finish on a background thread, and leases are taken and released on the dispatcher.
    /// </summary>
    private sealed class UiLease : IDisposable
    {
        private IDisposable? _inner;

        public UiLease(IDisposable inner) => _inner = inner;

        public void Dispose()
        {
            var inner = Interlocked.Exchange(ref _inner, null);
            if (inner is null) return;

            OnUi(() => { inner.Dispose(); return true; });
        }
    }

    /// <inheritdoc />
    public async Task<bool> ConfirmAgentWriteAsync(AgentWriteProposal proposal, CancellationToken ct)
    {
        // The countdown and the delete's consequence are shown so the user is not guessing: the prompt
        // does not wait forever (the same timeout the gate uses), and a permanent delete says so.
        var vm = new AgentApprovalDialogViewModel(
            proposal,
            AgentWriteGate.EffectiveTimeout(_settings),
            proposal.Kind == AgentWriteKind.Delete ? _settings.Operations.MoveToRecycleBin : null);

        // The gate stops waiting after ApprovalTimeoutSeconds and answers the agent with a refusal.
        // Nothing else would then close this modal window: the application would stay input-blocked,
        // and a later «Allow» would approve a write that no longer happens.
        await using var withdrawal = ct.Register(() => OnUi(() => { vm.Withdraw(); return true; }));

        return await _dialogs.ShowAgentApprovalAsync(vm);
    }

    /// <inheritdoc />
    public async Task<string?> RunApprovedWriteAsync(
        AgentWriteProposal proposal, CancellationToken ct)
    {
        // Supervised writes are local-disk only. An agent asking to move files inside an archive or
        // over SFTP would need the panel's mount held open for the whole operation (INV-09), and
        // refusing plainly beats a half-supported path that fails halfway through a transfer.
        // (The gate refuses this before it asks; the check stays as the last line of defence for a
        // caller that reaches the host directly.)
        if (!AgentWriteGate.IsLocalDiskOnly(proposal))
            return AgentWriteGate.LocalOnlyReason;

        // The scope that counts is the one held now, not the one at proposal time: the prompt can have
        // been open for minutes, and a directory revoked in between must not be written to.
        var scope = _agents.Bridge.CurrentScope();
        foreach (var path in proposal.Sources.Concat([proposal.Destination]).Where(p => p.Length > 0))
        {
            if (!scope.Check(path).IsAllowed)
                return $"Outside the directories approved for agents any more: {path}";
        }

        var entries = new List<IFileEntry>();
        foreach (var source in proposal.Sources)
        {
            if (await _fs.GetEntryAsync(new VfsPath(source), ct) is { } entry) entries.Add(entry);
            else return $"Not found: {source}";
        }

        if (entries.Count == 0) return "None of the given paths exist.";

        // Every kind is named. A default arm here mapped everything unrecognised onto DeleteOperation,
        // which turned an approved «create a directory» — the prompt says exactly that — into a
        // deletion of the paths it named.
        FileOperationBase op;
        switch (proposal.Kind)
        {
            case AgentWriteKind.Copy:
                op = new CopyOperation(
                    _fs, _fs, entries, new VfsPath(proposal.Destination),
                    // Never overwrite on an agent's behalf: a rename keeps both files and stays
                    // visible, whereas an overwrite is the one outcome the operation log cannot undo.
                    new RenameAutoStrategy(), _copyLogger);
                break;

            case AgentWriteKind.Move:
                op = new MoveOperation(
                    _fs, _fs, entries, new VfsPath(proposal.Destination),
                    new RenameAutoStrategy(), _moveLogger);
                break;

            case AgentWriteKind.Delete:
                op = new DeleteOperation(
                    // The recycle bin when configured, so an approved deletion is still recoverable.
                    _fs, entries, _settings.Operations.MoveToRecycleBin, _deleteLogger);
                break;

            default:
                // No tool proposes this today (McpToolCatalog exposes copy/move/delete only), so a
                // refusal is the honest answer rather than a guess at what was meant.
                return $"'{proposal.Kind}' is not a supervised operation.";
        }

        // The tag is what makes the operation attributable afterwards in the dashboard.
        op.Origin = proposal.Agent;

        // This runs on the MCP thread. The leases are counters on the panels' view models, which are
        // taken and released on the dispatcher (INV-09) — doing it from here raced with the UI and
        // could leak a lease, leaving a mount undisposable. Taken and released through the UI thread.
        using (new UiLease(OnUi(LeasePanelFileSystems)))
            await _operationQueue.EnqueueAsync(op, ct);

        await OnUiAsync(async () =>
        {
            await LeftPanel.FileList.LoadAsync(LeftPanel.CurrentPath);
            await RightPanel.FileList.LoadAsync(RightPanel.CurrentPath);
        });

        return AgentWriteGate.DescribeOutcome(
            op.State, op.SkippedCount, op.Warnings, op.FailureException?.Message);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentSearchHit>> SearchForAgentAsync(
        AgentSearchRequest request, CancellationToken ct)
    {
        // Every hit is checked against the approved directories again before it leaves. The walk and the
        // index already avoid links, but a hit is a path an agent will act on, and «resolves inside an
        // approved root» is the only test that means what the user approved (a file symlink, or a
        // stale index entry from before links were skipped, would otherwise slip through).
        var scope = _agents.Bridge.CurrentScope();
        bool InScope(string path) => scope.Check(path.Replace('/', '\\')).IsAllowed;

        if (request.Semantic)
        {
            var hits = await _agents.Semantic.SearchAsync(request.Query, request.Path, request.Limit, ct);
            return hits
                .Where(h => InScope(h.Path))
                .Select(h => new AgentSearchHit(h.Path, h.StartLine, h.Text, h.Score))
                .ToList();
        }

        // Exact search reuses the ripgrep-grade engine from 6.17 rather than a second walker, so an
        // agent sees the same gitignore-aware results the user does.
        var criteria = new SearchCriteria
        {
            NamePattern = request.NamesOnly ? "*" + request.Query + "*" : null,
            ContentText = request.NamesOnly ? null : request.Query,
            FilesOnly = true,
            RespectGitignore = true,
            SkipBinary = true,
            MaxResults = request.Limit,
            MaxMatchesPerFile = 5,

            // A junction inside an approved directory can point anywhere; the walk must not follow it.
            SkipReparsePoints = true,
        };

        var engine = new SearchEngine(_fs);
        var results = new List<AgentSearchHit>();

        await foreach (var hit in engine.SearchAsync(new VfsPath(request.Path), criteria, ct))
        {
            var hitPath = FileSystem.WindowsFileSystem.ToWindowsPath(hit.Entry.Path);
            if (!InScope(hitPath)) continue;

            results.Add(new AgentSearchHit(
                hitPath,
                hit.MatchLineNumber ?? 0,
                hit.MatchLineText?.Trim() ?? hit.Entry.Name,
                0));

            if (results.Count >= request.Limit) break;
        }

        return results;
    }

    /// <summary>
    /// Runs <paramref name="read"/> on the UI thread and returns its value. The MCP server answers on
    /// a background thread, and every panel property it wants is owned by the dispatcher.
    /// </summary>
    private static T OnUi<T>(Func<T> read)
    {
        var application = Application.Current;
        if (application is null) return read();

        return application.Dispatcher.CheckAccess() ? read() : application.Dispatcher.Invoke(read);
    }

    private static async Task OnUiAsync(Func<Task> action)
    {
        var application = Application.Current;
        if (application is null) { await action(); return; }

        if (application.Dispatcher.CheckAccess()) await action();
        else await application.Dispatcher.InvokeAsync(action).Task.Unwrap();
    }
}
