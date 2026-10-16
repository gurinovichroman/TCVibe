using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TCV.App.Localization;
using TCV.App.Services;
using TCV.App.ViewModels.Dialogs;
using TCV.App.Workspaces;
using TCV.Git;

namespace TCV.App.ViewModels;

/// <summary>
/// Git as a process: worktrees, the write minimum (stage/commit/branch/stash) and clone-from-URL.
/// <para>
/// Kept in its own file rather than appended to <c>MainViewModel.cs</c>: that file is the
/// architectural debt tracked as TRAP-17 / task 6.26, and the git commands are exactly the kind of
/// self-contained group that split will pull out. Everything substantial lives in
/// <see cref="GitWorkflowServices"/>; what stays here is the part that genuinely belongs to the
/// window — which panel to move, which workspace to create, which dialog to raise.
/// </para>
/// <para>
/// Nothing here touches libgit2 or walks the disk on the UI thread (INV-06): even the "is this a
/// repository?" precondition goes through <see cref="Task.Run(Action)"/>, because a panel may be
/// sitting on a network share where a directory probe is a freeze.
/// </para>
/// </summary>
public partial class MainViewModel
{
    // ─────────────────────────────── Worktrees (6.9 / 6.10) ───────────────────────────────

    /// <summary>
    /// Ctrl+Shift+G — the worktree dashboard. Everything here needs a real directory on disk, so a
    /// mounted panel (archive, FTP) is refused up front (INV-07): a worktree of <c>/pub</c> is not a
    /// thing, and the native error for it says nothing useful.
    /// </summary>
    [RelayCommand]
    private async Task OpenWorktreeDashboard()
    {
        // Search results work in the folder they were listed over (ADR-0026).
        if (await WorkingFolderOrRejectAsync(LocalizationManager.Get("Git_WtTitle")) is not { } path) return;
        if (!await RequireRepositoryAsync(path, "Git_WtTitle")) return;

        var (action, chosen, removed) = await _dialogs.ShowWorktreeDashboardAsync(_git.Worktrees, path, FindWorktreeUsers);

        if (removed) await LeaveRemovedFoldersAsync();

        switch (action)
        {
            case GitWorktreeAction.Open when chosen.Length > 0:
                NavigateActiveToDisk(chosen);
                break;
            case GitWorktreeAction.Create:
                await NewWorktree();
                break;
        }
    }

    /// <summary>
    /// After a worktree removal: a panel that was inside it would sit on a folder that no longer
    /// exists, and a missing folder lists as empty rather than failing (TRAP-44) — it would look like
    /// an empty checkout. Such a panel steps up to the nearest folder that still exists; then both
    /// panels reload. The existence probe walks the disk, so it runs off the UI thread (INV-06).
    /// </summary>
    private async Task LeaveRemovedFoldersAsync()
    {
        foreach (var panel in new[] { LeftPanel, RightPanel })
        {
            if (panel.FileList.IsMounted) continue;

            var path = panel.CurrentPath;
            var existing = await Task.Run(() => NearestExistingDirectory(path));
            if (existing is not null
                && !string.Equals(existing.TrimEnd('\\', '/'), path.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
                && panel.CurrentPath == path)
                panel.CurrentPath = existing;
        }

        RefreshAfterGitWrite();
    }

    private static string? NearestExistingDirectory(string path)
    {
        try
        {
            var current = new DirectoryInfo(Path.GetFullPath(path));
            while (current is not null && !current.Exists) current = current.Parent;
            return current?.FullName.TrimEnd('\\', '/') is { Length: > 0 } found
                ? (found.EndsWith(':') ? found + "\\" : found)
                : null;
        }
        catch (Exception)
        {
            return null;   // an unparseable path is left for the panel to report
        }
    }

    /// <summary>
    /// What still points into <paramref name="worktree"/>: a tab of either panel, or a saved
    /// workspace (panel tabs or terminal directory). Removing the folder leaves each of those
    /// standing in a directory that no longer exists, so the removal prompt names them.
    /// </summary>
    private IReadOnlyList<string> FindWorktreeUsers(GitWorktreeInfo worktree)
    {
        var users = new List<string>();
        if (worktree.Path.Length == 0) return users;

        foreach (var panel in new[] { LeftPanel, RightPanel })
        {
            foreach (var tab in panel.Tabs)
            {
                if (GitWorktreePaths.IsUnder(tab.Path, worktree.Path))
                    users.Add(string.Format(LocalizationManager.Get("Git_WtUserPanel"), panel.Name, tab.Path));
            }
        }

        foreach (var ws in Workspaces.Items)
        {
            var uses = ws.Left.Tabs.Concat(ws.Right.Tabs).Any(t => GitWorktreePaths.IsUnder(t.Path, worktree.Path))
                       || GitWorktreePaths.IsUnder(ws.TerminalDirectory, worktree.Path);
            if (uses) users.Add(string.Format(LocalizationManager.Get("Git_WtUserWorkspace"), ws.Name));
        }

        return users;
    }

    /// <summary>
    /// Ctrl+Alt+N — "new worktree under a task": branch, directory, the gitignored configuration the
    /// checkout needs, the setup command, a workspace, and the panel moved into it. The two chores
    /// that make the worktree workflow annoying in practice — missing <c>.env</c> and uninstalled
    /// dependencies — are handled by <see cref="WorktreeSetupService"/> right after the checkout.
    /// </summary>
    [RelayCommand]
    private async Task NewWorktree()
    {
        if (await WorkingFolderOrRejectAsync(LocalizationManager.Get("Git_WtNewTitle")) is not { } sourcePath) return;
        if (!await RequireRepositoryAsync(sourcePath, "Git_WtNewTitle")) return;

        var repositoryRoot = await RepositoryRootOfAsync(sourcePath);
        var branches = await _git.Write.ListBranchDetailsAsync(sourcePath);

        var request = await _dialogs.ShowNewWorktreeAsync(
            repositoryRoot,
            WorktreeSetupService.SuggestWorktreeRoot(repositoryRoot, _settings.Git.WorktreeRoot),
            branches.Select(b => b.Name).ToList(),
            _settings.Git.WorktreeSetupCommand,
            _settings.Git.WorktreeCreatesWorkspace,
            // A branch that is checked out anywhere — here or in a sibling worktree — cannot be
            // checked out again; the form says so before the user presses Create.
            branches.Where(b => b.IsCurrent || b.IsBlocked).Select(b => b.Name).ToList());
        if (request is null) return;

        var created = await _git.Worktrees.AddAsync(new GitWorktreeRequest(
            sourcePath, request.Branch, request.CreateBranch, request.BaseCommittish, request.TargetPath));
        if (!created.Success)
        {
            await _dialogs.ShowErrorAsync(
                LocalizationManager.Get("Git_WtNewTitle"),
                string.Format(LocalizationManager.Get("Git_WtCreateFailed"), GitResultText.Describe(created)));
            return;
        }

        var setup = await _git.Setup.RunAsync(
            repositoryRoot, request.TargetPath, _settings.Git,
            request.CarryFiles, request.RunSetupCommand);

        // The panel moves before the workspace is captured: a workspace is a snapshot of the panels,
        // so capturing first would file the new worktree under the old directory. The workspace we
        // are leaving keeps its latest layout first, as a switch would.
        if (request.CreateWorkspace)
            Workspaces.SaveActiveSnapshot();
        NavigateActiveToDisk(request.TargetPath);

        if (request.CreateWorkspace)
            CreateWorktreeWorkspace(request, repositoryRoot);

        _logger.LogInformation(
            "Worktree {Path} ready: {Copied} file(s) carried, setup command started: {Started}",
            request.TargetPath, setup.CopiedFiles, setup.CommandStarted);

        await _dialogs.ShowInfoAsync(
            LocalizationManager.Get("Git_WtNewTitle"),
            string.Format(
                LocalizationManager.Get("Git_WtCreated"),
                request.Branch, request.TargetPath, setup.CopiedFiles));
    }

    /// <summary>
    /// Registers the new worktree as a workspace and switches to it, so the branch gets a colour, a
    /// window title and an Alt+N scratchpad of its own. The workspace inherits the source project's
    /// look when there is one — several worktrees of one repository are meant to read as siblings,
    /// not as unrelated projects.
    /// </summary>
    private void CreateWorktreeWorkspace(NewWorktreeResult request, string repositoryRoot)
    {
        // A name that is already taken gets a numeric suffix. Re-using it would overwrite the older
        // workspace's panel layout — the worktree of a branch that was removed and is now back, say —
        // and then rewrite its group, icon and terminal directory as well.
        var name = GitWorktreePaths.UniqueName(
            WorkspaceNameFor(request.Branch, repositoryRoot),
            Workspaces.Items.Select(w => w.Name));
        var source = Workspaces.ActiveWorkspace;

        Workspaces.CaptureCurrent(name);

        var created = Workspaces.Items.FirstOrDefault(
            w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));
        if (created is null) return;

        created.Group = source?.Group is { Length: > 0 } group ? group : Path.GetFileName(repositoryRoot);
        created.Icon = source?.Icon ?? string.Empty;
        // The terminal starts in the worktree, not wherever the source project pointed — that is the
        // whole reason this is a separate workspace.
        created.TerminalCommand = source?.TerminalCommand ?? string.Empty;
        created.TerminalDirectory = request.TargetPath;

        Workspaces.Persist(created);
        Workspaces.Refresh();
        Workspaces.ApplyAppearance();
    }

    /// <summary>"repo/branch" — unique enough to sit next to the source project in the list, short
    /// enough to read in a window title.</summary>
    private static string WorkspaceNameFor(string branch, string repositoryRoot)
    {
        var repository = Path.GetFileName(repositoryRoot.TrimEnd('\\', '/'));
        return repository.Length == 0 ? branch : $"{repository}/{branch}";
    }

    // ─────────────────────────── Write minimum (6.11) ───────────────────────────

    /// <summary>Ctrl+Alt+G — stage and commit without leaving the panel.</summary>
    [RelayCommand]
    private async Task OpenGitCommit()
    {
        if (await WorkingFolderOrRejectAsync(LocalizationManager.Get("Git_CommitTitle")) is not { } path) return;
        if (!await RequireRepositoryAsync(path, "Git_CommitTitle")) return;

        if (await _dialogs.ShowGitCommitAsync(_git.Write, path, _agents.Llm))
        {
            RefreshAfterGitWrite();
            // The dialog just closes; a commit should not look the same as Cancel.
            ShowNotice(LocalizationManager.Get("Git_CommittedNotice"), TimeSpan.FromSeconds(4));
        }
    }

    /// <summary>Alt+G — switch branch, create one from HEAD, or delete a local one. The dialog does
    /// the work itself so a refusal (dirty tree, branch held by another worktree) is answered there.</summary>
    [RelayCommand]
    private async Task OpenGitBranch()
    {
        if (await WorkingFolderOrRejectAsync(LocalizationManager.Get("Git_BranchTitle")) is not { } path) return;
        if (!await RequireRepositoryAsync(path, "Git_BranchTitle")) return;

        if (await _dialogs.ShowGitBranchAsync(_git.Write, path))
            RefreshAfterGitWrite();
    }

    /// <summary>Ctrl+Shift+S — stage whatever the panel has selected.</summary>
    [RelayCommand]
    private Task GitStageSelection() => StageOrUnstageAsync(stage: true);

    /// <summary>Ctrl+Shift+U — take the selection back out of the index.</summary>
    [RelayCommand]
    private Task GitUnstageSelection() => StageOrUnstageAsync(stage: false);

    private async Task StageOrUnstageAsync(bool stage)
    {
        var titleKey = stage ? "Git_Stage" : "Git_Unstage";
        var title = LocalizationManager.Get(titleKey);
        // Search hits are disk files (TRAP-28) and are staged like any.
        if (await RejectIfNotOnDiskAsync(title)) return;

        var selected = GetSelectedOrCurrent();
        if (selected.Count == 0) return;

        var paths = selected
            .Select(e => FileSystem.WindowsFileSystem.ToWindowsPath(e.Path))
            .ToList();

        // A folder's selection is in the folder's checkout. Search hits may come from several
        // checkouts: each group is staged in its own (the probe walks the disk, so off the UI thread).
        // The checkout of the folder a hit lies in, as in the folder itself: a hit that is a repository's
        // root is that folder's entry, not the whole nested repository.
        var repositories = ActivePanel.FileList.IsSearchResults
            ? await Task.Run(() => paths
                .GroupBy(p => GitHeadReader.FindWorkingDirectory(Path.GetDirectoryName(p)) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Path: g.Key, Paths: (IReadOnlyList<string>)g.ToList()))
                .ToList())
            : [(ActivePanel.CurrentPath, paths)];
        if (repositories.Any(r => r.Path.Length == 0))
        {
            await _dialogs.ShowErrorAsync(title, LocalizationManager.Get("Git_NoRepo"));
            return;
        }

        foreach (var (path, _) in repositories)
        {
            if (!await RequireRepositoryAsync(path, titleKey)) return;
        }

        // Staging a conflicted file marks it resolved as it stands, markers and all. Say so first.
        if (stage)
        {
            var conflicts = 0;
            foreach (var (path, group) in repositories)
            {
                conflicts += await CountConflictsAmongAsync(path, group);
            }

            if (conflicts > 0 && !await _dialogs.ConfirmAsync(
                    LocalizationManager.Get("Git_ConflictTitle"),
                    LocalizationManager.Format("Git_ConflictStageWarn", conflicts)))
                return;
        }

        foreach (var (path, group) in repositories)
        {
            var result = stage
                ? await _git.Write.StageAsync(path, group)
                : await _git.Write.UnstageAsync(path, group);

            if (!result.Success)
            {
                await _dialogs.ShowErrorAsync(title, GitResultText.Describe(result));
                RefreshAfterGitWrite();   // a group before this one may have gone through
                return;
            }
        }

        RefreshAfterGitWrite();
        // The Git column is off by default, so the panel looks exactly as before: say what happened.
        ShowNotice(LocalizationManager.Format(stage ? "Git_StagedNotice" : "Git_UnstagedNotice", paths.Count), TimeSpan.FromSeconds(4));
    }

    /// <summary>How many of the selected paths (or files under a selected folder) are unmerged.</summary>
    private async Task<int> CountConflictsAmongAsync(string anyPath, IReadOnlyList<string> selectedPaths)
    {
        var status = await _git.Write.GetStatusAsync(anyPath);
        if (!status.IsRepository) return 0;

        return status.Files
            .Where(f => f.Status == GitFileStatus.Conflict)
            .Select(f => Path.GetFullPath(Path.Combine(status.RepositoryRoot, f.RelativePath.Replace('/', Path.DirectorySeparatorChar))))
            .Count(conflict => selectedPaths.Any(p => GitWorktreePaths.IsUnder(conflict, p)));
    }

    /// <summary>Alt+S — stash everything, untracked files included; a stash that leaves new files
    /// behind does not actually hand back a clean tree.</summary>
    [RelayCommand]
    private async Task GitStash()
    {
        var title = LocalizationManager.Get("Git_StashTitle");
        if (await WorkingFolderOrRejectAsync(title) is not { } path) return;
        if (!await RequireRepositoryAsync(path, "Git_StashTitle")) return;

        // The description is optional (the prompt says so), so an empty box must still confirm.
        var message = await _dialogs.ShowInputAsync(
            title, LocalizationManager.Get("Git_StashMessage"), string.Empty, allowEmpty: true);
        if (message is null) return;   // cancelled

        var result = await _git.Write.StashAsync(path, message);
        if (!result.Success)
        {
            await _dialogs.ShowErrorAsync(title, GitResultText.Describe(result));
            return;
        }

        RefreshAfterGitWrite();
        ShowNotice(LocalizationManager.Get("Git_StashedNotice"), TimeSpan.FromSeconds(4));
    }

    /// <summary>Alt+Shift+S — restore the most recent stash. Confirmed first, naming the entry: the
    /// stash is shared by every worktree of the repository, so the top entry may be another
    /// worktree's work, and popping onto a dirty tree is the one stash operation that can lose work.</summary>
    [RelayCommand]
    private async Task GitStashPop()
    {
        var title = LocalizationManager.Get("Git_StashPopTitle");
        if (await WorkingFolderOrRejectAsync(title) is not { } path) return;
        if (!await RequireRepositoryAsync(path, "Git_StashPopTitle")) return;

        var stash = await _git.Write.GetStashInfoAsync(path);
        if (stash.Count == 0)
        {
            await _dialogs.ShowErrorAsync(title, LocalizationManager.Get("Git_NoStash"));
            return;
        }

        var entry = stash.TopMessage.Length > 0 ? stash.TopMessage : LocalizationManager.Get("Git_StashUnnamed");
        if (!await _dialogs.ConfirmAsync(
                title, string.Format(LocalizationManager.Get("Git_StashPopConfirm"), entry, stash.Count)))
            return;

        var result = await _git.Write.StashPopAsync(path);
        if (!result.Success)
        {
            await _dialogs.ShowErrorAsync(title, GitResultText.Describe(result));
            return;
        }

        RefreshAfterGitWrite();
        ShowNotice(string.Format(LocalizationManager.Get("Git_StashPoppedNotice"), entry), TimeSpan.FromSeconds(4));
    }

    // ─────────────────────────── Clone from URL (6.11a) ───────────────────────────

    /// <summary>
    /// Ctrl+Alt+C — clone into the active panel's directory. The URL is taken from the clipboard when
    /// it looks like one: this command is nearly always run straight after copying a link.
    /// </summary>
    [RelayCommand]
    private async Task GitClone()
    {
        var title = LocalizationManager.Get("Git_CloneTitle");
        // The clone is put into the folder: the search-results list itself is none.
        if (await DiskFolderOrRejectAsync(title) is not { } parent) return;
        if (!await Task.Run(() => Directory.Exists(parent)))
        {
            await _dialogs.ShowErrorAsync(title, string.Format(LocalizationManager.Get("WsTerminal_NoDirectory"), parent));
            return;
        }

        var cloned = await _dialogs.ShowGitCloneAsync(_git.Clone, parent, FileCommands.TryGetClipboardText() ?? string.Empty);
        if (cloned is null || cloned.Length == 0) return;

        NavigateActiveToDisk(cloned);

        // A freshly cloned repository is the single most likely place for a committed *.tcvproj, and
        // opening it is meant to be the whole setup step (ADR-0011).
        await OfferProjectFileAsync(cloned);
    }

    /// <summary>
    /// Offers to open a <c>*.tcvproj</c> found in a fresh clone. Only when there is exactly one:
    /// picking between several is a decision the file dialog (Ctrl+Alt+O) already handles better.
    /// </summary>
    private async Task OfferProjectFileAsync(string clonedPath)
    {
        string[] candidates;
        try
        {
            candidates = await Task.Run(() =>
                Directory.GetFiles(clonedPath, "*" + WorkspaceProjectFile.Extension, SearchOption.TopDirectoryOnly));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not scan {Path} for a project file", clonedPath);
            return;
        }

        if (candidates.Length != 1) return;

        var file = candidates[0];
        if (!await _dialogs.ConfirmAsync(
                LocalizationManager.Get("WsProj_Title"),
                string.Format(LocalizationManager.Get("Git_CloneOpenProject"), Path.GetFileName(file))))
            return;

        var definition = WorkspaceProjectFile.Load(file);
        if (definition is null) return;

        await Workspaces.ApplyDefinition(WorkspaceProjectFile.ToWorkspace(definition, file));
    }

    // ─────────────────────────────── Shared helpers ───────────────────────────────

    /// <summary>
    /// Guards every git command with the one precondition they share. Reported rather than silently
    /// ignored: "nothing happened" on a folder that merely looks like a checkout is the most
    /// confusing possible answer. The probe walks parent directories, so it runs off the UI thread.
    /// </summary>
    private async Task<bool> RequireRepositoryAsync(string path, string titleKey)
    {
        if (await Task.Run(() => GitHeadReader.FindGitDirectory(path)) is not null) return true;

        await _dialogs.ShowErrorAsync(
            LocalizationManager.Get(titleKey), LocalizationManager.Get("Git_NoRepo"));
        return false;
    }

    /// <summary>
    /// Root of the checkout containing <paramref name="path"/>, falling back to the path itself when
    /// it is not in one. The <em>checkout</em>, not the repository: called from inside a worktree
    /// this is that worktree's own root, which is what the setup hook must copy configuration from.
    /// </summary>
    private static Task<string> RepositoryRootOfAsync(string path) =>
        Task.Run(() => GitHeadReader.FindWorkingDirectory(path) ?? path);

    /// <summary>
    /// Re-reads both panels after a write. Both, not just the active one: a commit or a checkout
    /// changes the working tree that either panel may be showing, and a stale listing after a branch
    /// switch is how you end up editing a file that no longer exists. The reload also refreshes the
    /// Git column.
    /// </summary>
    private void RefreshAfterGitWrite()
    {
        LeftPanel.FileList.RefreshCommand.Execute(null);
        RightPanel.FileList.RefreshCommand.Execute(null);

        // The listing reload does not touch HEAD, and a checkout is precisely the case where the
        // files are the same and the branch is not.
        LeftPanel.RefreshBranch();
        RightPanel.RefreshBranch();
    }

    /// <summary>
    /// Ctrl+G — diff against HEAD of the selected file, or of everything below the selected folder.
    /// On <c>..</c> it is the folder the panel shows, so at the repository root that is the whole
    /// working tree. Read on a background thread.
    /// </summary>
    [RelayCommand]
    private async Task OpenGitDiff()
    {
        var item = ActivePanel.FileList.SelectedItem;
        if (item is null) return;

        // Search hits are disk files (TRAP-28); ".." stands for the folder the panel works in.
        if (await RejectIfNotOnDiskAsync(LocalizationManager.Get("Git_DiffTitleShort"))) return;

        string winPath;
        if (!item.IsParentEntry)
        {
            winPath = FileSystem.WindowsFileSystem.ToWindowsPath(item.Entry.Path);
        }
        else if (await WorkingFolderOrRejectAsync(LocalizationManager.Get("Git_DiffTitleShort")) is { } folder)
        {
            winPath = folder;
        }
        else
        {
            return;
        }
        await _dialogs.ShowGitDiffAsync(_gitStatusProvider, winPath);
    }
}
