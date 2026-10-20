using TCV.App.Operations.Conflict;
using TCV.App.ViewModels.Dialogs;
using TCV.Core.Interfaces;
using TCV.Network;

namespace TCV.App.Services;

public interface IDialogService
{
    /// <summary>
    /// Shows the copy/move dialog with options. Returns result or null if cancelled.
    /// <paramref name="offered"/> leaves out the options the transfer cannot honour (an archive's).
    /// </summary>
    Task<CopyDialogResult?> ShowCopyDialogAsync(
        IReadOnlyList<IFileEntry> files, string defaultTarget, bool isMove = false,
        TCV.App.Operations.CopyOptionSet offered = TCV.App.Operations.CopyOptionSet.All);

    /// <summary>Shows a per-file conflict resolution dialog. Returns resolution + applyToAll flag.</summary>
    Task<(ConflictResolution Resolution, bool ApplyToAll)> ShowConflictDialogAsync(
        TCV.Core.Interfaces.IFileEntry source, TCV.Core.Interfaces.IFileEntry existing);

    /// <summary>
    /// Shows the delete confirmation dialog. Returns true if confirmed.
    /// <paramref name="recycleCount"/> is how many of <paramref name="files"/> will really go to the
    /// Recycle Bin (0 = all deleted permanently); the dialog must say exactly that (INV-11).
    /// </summary>
    Task<bool> ShowDeleteConfirmAsync(IReadOnlyList<IFileEntry> files, int recycleCount);

    /// <summary>One item of an operation failed: asks whether to retry, skip it (or all such) or abort.</summary>
    Task<TCV.App.Operations.OperationErrorResolution> ShowOperationErrorAsync(
        TCV.App.Operations.OperationErrorInfo error);

    /// <summary>Shows the new folder name dialog. Returns name or null if cancelled.</summary>
    Task<string?> ShowNewFolderDialogAsync();

    /// <summary>Shows the rename dialog for a single entry. Returns new name or null if cancelled.</summary>
    Task<string?> ShowRenameDialogAsync(string currentName, bool isDirectory = false);

    /// <summary>Shows a modal error message dialog.</summary>
    Task ShowErrorAsync(string title, string message);

    /// <summary>Shows a modal informational message. Separate from
    /// <see cref="ShowErrorAsync"/> so a completed action does not arrive wearing an error icon.</summary>
    Task ShowInfoAsync(string title, string message);

    /// <summary>Shows the progress dialog as a non-blocking window and returns its ViewModel.</summary>
    ProgressDialogViewModel ShowProgressDialog(string title);

    /// <summary>The progress windows sent to the background, which the status bar counts (ADR-0038).</summary>
    BackgroundProgress BackgroundProgress { get; }

    /// <summary>
    /// Shows the connection manager (saved connections and their editor) until it is closed; what to
    /// open or close is read back from <paramref name="viewModel"/>.
    /// </summary>
    void ShowConnectionManager(ViewModels.Dialogs.ConnectionManagerDialogViewModel viewModel);

    /// <summary>
    /// Shows what an import from <c>~/.ssh/config</c> would add, over the connection manager that asked.
    /// True when the user chose Import; the ticked rows are read back from <paramref name="viewModel"/>.
    /// </summary>
    bool ShowSshConfigImport(ViewModels.Dialogs.SshConfigImportDialogViewModel viewModel);

    /// <summary>Shows the Pack dialog. Returns result or null if cancelled.</summary>
    Task<PackDialogResult?> ShowPackDialogAsync(
        IReadOnlyList<IFileEntry> selectedFiles,
        string defaultDestDir,
        bool sevenZipAvailable);

    /// <summary>
    /// Shows the Unpack dialog for one archive or several (Alt+F9 on a selection). Returns result or
    /// null if cancelled. <paramref name="isZip"/>: every one is a ZIP, so Windows can extract them.
    /// </summary>
    Task<UnpackDialogResult?> ShowUnpackDialogAsync(
        IReadOnlyList<string> archiveNames,
        string defaultDestDir,
        bool isZip);

    /// <summary>
    /// Shows a password prompt for a password-protected archive.
    /// Returns the entered password, or null if cancelled.
    /// </summary>
    Task<string?> ShowPasswordDialogAsync(string archiveName);

    /// <summary>
    /// Opens the internal file viewer (F3 Lister) as a non-modal window over the already-fetched
    /// bytes. Works for any source (disk/archive/FTP) since the caller reads them through the VFS.
    /// </summary>
    /// <param name="fileName">Name shown in the title/status.</param>
    /// <param name="data">The bytes to display (already capped to a viewable size by the caller).</param>
    /// <param name="fetchTruncated">True when <paramref name="data"/> is only the head of a larger file.</param>
    /// <param name="fullSize">The real file size, for the status line (-1 if unknown).</param>
    /// <param name="llm">Enables «Summarise» in the viewer (6.14); null hides the button.</param>
    /// <param name="source">Lets the viewer move its window over a file bigger than <paramref name="data"/>
    /// (start, tail, blocks, offset); the viewer owns it and disposes it when the window closes.</param>
    /// <param name="siblings">The panel's other files for N/P, and the lease the window holds; the viewer
    /// owns it and releases it when the window closes.</param>
    void ShowFileViewer(
        string fileName, byte[] data, bool fetchTruncated, long fullSize,
        TCV.Ai.Llm.LlmClient? llm = null, TCV.App.Viewer.IViewerSource? source = null,
        TCV.App.Viewer.ViewerSiblings? siblings = null);

    /// <summary>Shows the file-operation log window (modal): a history of copy/move/delete/new-folder.</summary>
    /// <param name="undoRename">Takes a logged rename batch back (the AI rename's undo once its window
    /// is closed); null leaves the log's undo button disabled.</param>
    void ShowOperationLog(OperationLog log, Func<TCV.App.Operations.RenameBatchRecord, Task>? undoRename = null);

    /// <summary>
    /// Shows a generic single-line text prompt. Returns the entered value, or null if cancelled.
    /// With <paramref name="allowEmpty"/> OK also accepts a blank value and returns an empty string —
    /// the "leave empty to clear" prompts need that; without it OK stays disabled until there is text.
    /// </summary>
    Task<string?> ShowInputAsync(string title, string prompt, string initial = "", bool allowEmpty = false);

    /// <summary>Shows the system "open file" picker. Returns the chosen path, or null if cancelled.</summary>
    /// <param name="filter">Win32 filter string ("Name (*.ext)|*.ext|All files (*.*)|*.*").</param>
    /// <param name="initialDirectory">Where the picker starts; null lets the system decide.</param>
    string? PickOpenFile(string title, string filter, string? initialDirectory = null);

    /// <summary>Shows the system "save file" picker. Returns the chosen path, or null if cancelled.</summary>
    string? PickSaveFile(string title, string filter, string fileName, string? initialDirectory = null);

    /// <summary>
    /// Shows the bookmarks manager over a ready view model (the same shape as the agent dialogs
    /// below: the model needs the current panel path and the persistence policy, which do not
    /// belong in this interface).
    /// </summary>
    Task ShowBookmarksManagerAsync(ViewModels.Dialogs.BookmarksManagerDialogViewModel viewModel);

    /// <summary>
    /// Shows the saved-operations manager over a ready view model (it needs the store and,
    /// sometimes, a draft built from the panels). True when the changes were written.
    /// </summary>
    Task<bool> ShowSavedOperationsManagerAsync(ViewModels.Dialogs.SavedOperationsDialogViewModel viewModel);

    /// <summary>
    /// Shows the command-palette control as a fuzzy picker over a ready view model (saved
    /// operations, gadgets, editors and agents). Returns the chosen row, or null if cancelled.
    /// </summary>
    Task<Commands.CommandPaletteItem?> PickFromPaletteAsync(
        ViewModels.Dialogs.CommandPaletteViewModel viewModel);

    /// <summary>
    /// Shows the settings over a ready view model (it needs the serializer, the tool service and the
    /// current gestures). The caller reads the outcome from the model (<c>SavedSuccessfully</c>).
    /// </summary>
    Task ShowSettingsAsync(ViewModels.Dialogs.SettingsDialogViewModel viewModel);

    /// <summary>Shows the checksum tool over a ready view model; returns when the window is closed.</summary>
    Task ShowChecksumAsync(ViewModels.Dialogs.ChecksumDialogViewModel viewModel);

    /// <summary>
    /// Shows the user-tools editor over a ready view model (it writes straight into the plugin);
    /// returns when the window is closed.
    /// </summary>
    Task ShowUserToolsAsync(ViewModels.Dialogs.UserToolsDialogViewModel viewModel);

    /// <summary>Shows the plugin manager over a ready view model; returns when the window is closed.</summary>
    Task ShowPluginManagerAsync(ViewModels.Dialogs.PluginManagerDialogViewModel viewModel);

    /// <summary>Shows the hotkey editor over a ready view model; returns when the window is closed.</summary>
    Task ShowHotkeySettingsAsync(ViewModels.Dialogs.HotkeySettingsDialogViewModel viewModel);

    /// <summary>
    /// Shows the per-workspace notes editor (markdown scratchpad + saved terminal command and cwd).
    /// Returns the edited values, or null if cancelled.
    /// </summary>
    Task<WorkspaceNotesResult?> ShowWorkspaceNotesAsync(
        string workspaceName,
        string notes,
        string terminalCommand,
        string terminalDirectory,
        string notesPath);

    /// <summary>
    /// Shows the workspace properties (name, icon, colour, group, pinned) in one window. Returns
    /// the edited values, or null if cancelled; the workspace itself is not touched.
    /// </summary>
    /// <param name="allNames">Every saved workspace name, so a rename cannot take an existing one.</param>
    /// <param name="groups">Groups already in use, offered for reuse.</param>
    /// <param name="isNew">The same window for a workspace being created: its own title, "Create".</param>
    Task<WorkspacePropertiesResult?> ShowWorkspacePropertiesAsync(
        TCV.App.Workspaces.Workspace workspace,
        IReadOnlyList<string> allNames,
        IReadOnlyList<string> groups,
        bool isNew = false);

    /// <summary>Shows a modal yes/no question. True when the user confirmed.</summary>
    Task<bool> ConfirmAsync(string title, string message);

    /// <summary>
    /// Shows a modal question with two ways to go ahead and one way back: Cancel, the default button,
    /// which is also the answer when the window is closed any other way.
    /// </summary>
    Task<ConfirmChoice> ChooseAsync(
        string title, string message, string confirmText, string alternateText, bool isWarning = false);

    /// <summary>
    /// Shows the worktree dashboard for the repository containing <paramref name="anyPathInRepository"/>.
    /// Returns what the user asked for next — nothing, a worktree to open, or the "new worktree" flow —
    /// and whether any worktree was removed meanwhile (a panel may be standing in a deleted folder).
    /// </summary>
    /// <param name="findUsers">Names what still points into a worktree (the other panel, saved
    /// workspaces), so the removal prompt can list them. Null means "nothing known".</param>
    Task<(ViewModels.Dialogs.GitWorktreeAction Action, string Path, bool Removed)> ShowWorktreeDashboardAsync(
        TCV.Git.GitWorktreeService worktrees,
        string anyPathInRepository,
        Func<TCV.Git.GitWorktreeInfo, IReadOnlyList<string>>? findUsers = null);

    /// <summary>Shows the "new worktree for a task" form. Returns the request, or null if cancelled.</summary>
    Task<ViewModels.Dialogs.NewWorktreeResult?> ShowNewWorktreeAsync(
        string repositoryRoot,
        string worktreeRoot,
        IReadOnlyList<string> existingBranches,
        string setupCommand,
        bool createWorkspaceByDefault,
        IReadOnlyCollection<string>? branchesHeldElsewhere = null);

    /// <summary>
    /// Shows the stage/commit dialog. True when the repository changed (a commit, or staging and
    /// discarding along the way), so the caller refreshes the panels.
    /// <paramref name="llm"/> enables the AI message draft (6.14); null hides the button entirely.
    /// </summary>
    Task<bool> ShowGitCommitAsync(
        TCV.Git.GitWriteService git, string anyPathInRepository, TCV.Ai.Llm.LlmClient? llm = null);

    /// <summary>
    /// Shows the branch switcher / creator / local-branch deleter. The dialog runs the operations
    /// itself; true when anything in the repository changed.
    /// </summary>
    Task<bool> ShowGitBranchAsync(TCV.Git.GitWriteService git, string anyPathInRepository);

    /// <summary>Shows the diff of one file (or of everything below a folder) against HEAD; returns
    /// when the window is closed.</summary>
    Task ShowGitDiffAsync(TCV.Git.GitStatusProvider provider, string fullPath);

    /// <summary>Shows the clone dialog. Returns the new working directory, or null if cancelled.</summary>
    Task<string?> ShowGitCloneAsync(
        TCV.Git.GitCloneService clone, string parentDirectory, string initialUrl);

    // ── Agent-aware layer (6D) ────────────────────────────────────────────────
    //
    // These five take a ready view model instead of the collaborators it needs, unlike the methods
    // above. Deliberate: each of these models joins four to eight services (git, the session store,
    // the operation log, the write gate, the MCP host, the LLM client) plus callbacks back into
    // MainViewModel, and threading all of that through this interface would turn it into a second
    // constructor for every dialog. The rule that matters is still kept — a view model never creates
    // a window; it just no longer pretends the dialog service knows how to assemble it.

    /// <summary>
    /// Shows the «Agent changes» dashboard (6.12). Returns what the user asked for next.
    /// </summary>
    Task<(ViewModels.Dialogs.AgentChangesAction Action, string Path)> ShowAgentChangesAsync(
        ViewModels.Dialogs.AgentChangesDialogViewModel viewModel);

    /// <summary>Shows the approval prompt for one agent write (6.13b). True when approved.</summary>
    Task<bool> ShowAgentApprovalAsync(ViewModels.Dialogs.AgentApprovalDialogViewModel viewModel);

    /// <summary>Shows the agent launcher (6.13a). Returns the request, or null if cancelled.</summary>
    Task<ViewModels.Dialogs.AgentLaunchResult?> ShowAgentLaunchAsync(
        ViewModels.Dialogs.AgentLaunchDialogViewModel viewModel);

    /// <summary>Shows the AI rename preview (6.15). Returns how many files were actually renamed.</summary>
    Task<int> ShowAiRenameAsync(ViewModels.Dialogs.AiRenameDialogViewModel viewModel);

    /// <summary>Shows semantic search (6.16). Returns what the user asked for next.</summary>
    Task<ViewModels.Dialogs.SemanticSearchAction> ShowSemanticSearchAsync(
        ViewModels.Dialogs.SemanticSearchDialogViewModel viewModel);
}
