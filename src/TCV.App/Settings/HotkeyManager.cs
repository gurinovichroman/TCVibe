using System.Windows.Input;

namespace TCV.App.Settings;

/// <summary>One key press: a key plus its modifiers. The unit a chord is built from.</summary>
public readonly record struct KeyStroke(Key Key, ModifierKeys Modifiers)
{
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        parts.Add(Key.ToString());
        return string.Join("+", parts);
    }

    /// <summary>Parses "Ctrl+Shift+K"; false when the key name is not one WPF knows.</summary>
    public static bool TryParse(string text, out KeyStroke stroke)
    {
        stroke = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        var modifiers = ModifierKeys.None;
        foreach (var part in parts[..^1])
        {
            modifiers |= part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => ModifierKeys.Control,
                "alt" => ModifierKeys.Alt,
                "shift" => ModifierKeys.Shift,
                _ => ModifierKeys.None,
            };
        }

        if (!Enum.TryParse<Key>(parts[^1], ignoreCase: true, out var key)) return false;
        stroke = new KeyStroke(key, modifiers);
        return true;
    }
}

/// <summary>
/// A gesture bound to a command: one key stroke, or two for an editor-style chord such as
/// <c>Ctrl+K Ctrl+S</c> (6.20).
/// </summary>
public sealed class HotkeyBinding
{
    public HotkeyBinding(string commandId, Key key, ModifierKeys modifiers = ModifierKeys.None)
        : this(commandId, new KeyStroke(key, modifiers), null) { }

    public HotkeyBinding(string commandId, KeyStroke first, KeyStroke? second = null)
    {
        CommandId = commandId;
        First = first;
        Second = second;
    }

    public string CommandId { get; }

    /// <summary>First (or only) stroke of the gesture.</summary>
    public KeyStroke First { get; }

    /// <summary>Second stroke of a chord, or null for a plain gesture.</summary>
    public KeyStroke? Second { get; }

    /// <summary>True when this gesture needs two presses.</summary>
    public bool IsChord => Second.HasValue;

    public Key Key => First.Key;
    public ModifierKeys Modifiers => First.Modifiers;

    /// <summary>
    /// Text form: "Ctrl+S" or "Ctrl+K Ctrl+S". A space separates the strokes of a chord, which is
    /// also how the hotkey dialog and <c>settings.json</c> spell them.
    /// </summary>
    public override string ToString() =>
        Second is { } second ? $"{First} {second}" : First.ToString();
}

/// <summary>
/// Single source of truth for keyboard dispatch. Holds one editable (primary) gesture per
/// command plus fixed aliases, and resolves a pressed gesture back to a command id.
/// </summary>
public sealed class HotkeyManager
{
    private readonly Dictionary<string, HotkeyBinding> _bindings = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Extra fixed gestures that also trigger a command (not editable in the dialog).</summary>
    private readonly List<HotkeyBinding> _aliases = new();

    /// <summary>
    /// Gestures declared by user tools (6.29). Kept apart from <see cref="_bindings"/> because they
    /// come and go with the tool list rather than from the command registry, and they must survive a
    /// <see cref="Reload"/> triggered by the hotkey dialog.
    /// </summary>
    private readonly List<HotkeyBinding> _userTools = new();

    /// <summary>Single-stroke gesture → command id. Rebuilt whenever bindings change.</summary>
    private readonly Dictionary<KeyStroke, string> _byGesture = new();

    /// <summary>Chord (first stroke, second stroke) → command id.</summary>
    private readonly Dictionary<(KeyStroke First, KeyStroke Second), string> _byChord = new();

    /// <summary>First strokes that open a chord, so the dispatcher knows to wait for a second key.</summary>
    private readonly HashSet<KeyStroke> _chordPrefixes = new();

    private readonly AppSettings _settings;

    public HotkeyManager(AppSettings settings)
    {
        _settings = settings;
        Reload();
    }

    /// <summary>Raised after the bindings were rebuilt, so menus and the F-key bar can show the new gestures.</summary>
    public event EventHandler? BindingsChanged;

    /// <summary>Rebuilds defaults + user overrides. Call after the hotkey dialog saves.</summary>
    public void Reload()
    {
        _bindings.Clear();
        _aliases.Clear();
        RegisterDefaults();
        RebuildIndex(ApplyUserOverrides());
        BindingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RegisterDefaults()
    {
        // ── File operations (Total Commander layout) ──────────────────────────
        Register("Copy",             Key.F5);
        Register("Move",             Key.F6);
        Register("NewFolder",        Key.F7);
        Register("Delete",           Key.F8);
        Alias  ("Delete",            Key.Delete);
        Register("DeletePermanent",  Key.Delete, ModifierKeys.Shift);
        Register("Rename",           Key.F2);
        Alias  ("Rename",            Key.F6, ModifierKeys.Shift);
        Register("View",             Key.F3);
        Register("Edit",             Key.F4);
        Register("NewFile",          Key.F4, ModifierKeys.Shift);             // TC: edit a new file
        Register("Quit",             Key.F10);
        // F9 is the terminal, as the design's F-key row labels it (ADR-0030). The menu bar still opens
        // with Alt; "Menu" keeps its id, without a default gesture, for the palette and the hotkey dialog.
        Register("Terminal",         Key.F9);
        Alias  ("Terminal",          Key.T, ModifierKeys.Control | ModifierKeys.Alt);

        // ── Archives ──────────────────────────────────────────────────────────
        Register("Pack",             Key.F5, ModifierKeys.Alt);
        Register("Unpack",           Key.F9, ModifierKeys.Alt);

        // ── Navigation & panels ───────────────────────────────────────────────
        Register("SwitchPanel",      Key.Tab);
        Register("OpenFile",         Key.Return);
        Register("NavigateUp",       Key.Back);
        Register("NavigateBack",     Key.Left,  ModifierKeys.Alt);
        Register("NavigateForward",  Key.Right, ModifierKeys.Alt);
        Register("ToggleSplit",      Key.OemQuestion, ModifierKeys.Control);
        Register("SwapPanels",       Key.U, ModifierKeys.Control);           // Total Commander: swap the panels
        Register("GoToRoot",         Key.OemPipe, ModifierKeys.Control);   // Ctrl+\  → drive/mount root
        Register("GoHome",           Key.Home,    ModifierKeys.Control);   // Ctrl+Home → user profile
        Register("EditPath",         Key.L,       ModifierKeys.Control);   // Ctrl+L → type a path (Explorer, browsers)

        // ── View modes (Total Commander's keys) ──────────────────────────────
        // TC's Ctrl+F2 for Details is RunGadget's here, so Details takes the free Shift variant.
        Register("ViewModeBrief",      Key.F1, ModifierKeys.Control);
        Register("ViewModeDetails",    Key.F2, ModifierKeys.Control | ModifierKeys.Shift);
        Register("ViewModeThumbnails", Key.F1, ModifierKeys.Control | ModifierKeys.Shift);
        Register("ViewModeTree",       Key.F8, ModifierKeys.Control);

        // ── Selection ─────────────────────────────────────────────────────────
        // The numeric-keypad keys follow Total Commander: + selects a group, - unselects a group,
        // * inverts, Ctrl++ selects all, Ctrl+- clears the selection.
        Register("SelectToggle",     Key.Insert);
        Register("SelectToggleSize", Key.Space);      // Space marks and moves down too, and counts a folder's size (TC)
        Register("SelectByMask",     Key.Add);        // Num+   (TC: select group)
        Register("UnselectByMask",   Key.Subtract);   // Num-   (TC: unselect group)
        Register("InvertSelection",  Key.Multiply);   // Num*
        Register("SelectAll",        Key.Add,      ModifierKeys.Control);   // Ctrl+Num+
        Alias  ("SelectAll",         Key.A,        ModifierKeys.Control);   // the editor habit
        Register("DeselectAll",      Key.Subtract, ModifierKeys.Control);   // Ctrl+Num-
        Alias  ("DeselectAll",       Key.Divide);                           // Num/ (older default)

        // ── Tabs ──────────────────────────────────────────────────────────────
        Register("NewTab",           Key.T,   ModifierKeys.Control);
        Register("CloseTab",         Key.W,   ModifierKeys.Control);
        Register("NextTab",          Key.Tab, ModifierKeys.Control);
        Register("PreviousTab",      Key.Tab, ModifierKeys.Control | ModifierKeys.Shift);
        Register("RefreshTab",       Key.R,   ModifierKeys.Control);

        // ── Clipboard ─────────────────────────────────────────────────────────
        Register("ClipboardCopy",    Key.C, ModifierKeys.Control);
        Register("ClipboardCut",     Key.X, ModifierKeys.Control);
        Register("ClipboardPaste",   Key.V, ModifierKeys.Control);
        // Ctrl+P is the editor-world quick-open (see FindFiles below), so copying a path moved to
        // the other dev convention, Ctrl+Shift+C — ADR-0014.
        Register("CopyFullPath",     Key.C, ModifierKeys.Control | ModifierKeys.Shift); // path(s) as text
        Register("CopyNames",        Key.P, ModifierKeys.Control | ModifierKeys.Shift); // name(s) as text
        // CopyReadLink has no default gesture on purpose: a link anyone can open is made from the file
        // menu (which asks how long it lives) or the palette, and the hotkey dialog can still give it one.

        // ── Tools & dialogs ───────────────────────────────────────────────────
        Register("Search",           Key.F, ModifierKeys.Control);
        Register("MultiRename",      Key.M, ModifierKeys.Control);
        Register("Properties",       Key.Return, ModifierKeys.Alt);          // TC-compatible
        Alias  ("Properties",        Key.Return, ModifierKeys.Shift);        // pre-Alt+Enter habit
        Register("CalculateFolderSizes", Key.Return, ModifierKeys.Alt | ModifierKeys.Shift); // TC: count all folders
        Register("Settings",         Key.S, ModifierKeys.Control | ModifierKeys.Alt);
        Register("ConnectionManager", Key.F, ModifierKeys.Control | ModifierKeys.Shift);
        Register("Disconnect",       Key.F, ModifierKeys.Control | ModifierKeys.Alt);
        Register("QuickConnect",     Key.N, ModifierKeys.Control);   // Total Commander: connect by address
        Register("QueueTransfer",    Key.F5, ModifierKeys.Shift);
        Register("TransferQueue",    Key.Q, ModifierKeys.Control | ModifierKeys.Shift);
        Register("OpenInEditor",     Key.E, ModifierKeys.Control);                      // default editor
        Register("OpenInEditorPicker", Key.E, ModifierKeys.Control | ModifierKeys.Shift); // choose editor
        Register("RunAgentHere",     Key.A, ModifierKeys.Control | ModifierKeys.Shift);  // default agent CLI
        Register("RunAgentPicker",   Key.A, ModifierKeys.Control | ModifierKeys.Alt);    // choose agent
        Register("AddBookmark",      Key.D, ModifierKeys.Control);
        Register("OperationLog",     Key.L, ModifierKeys.Control | ModifierKeys.Alt);
        // BackgroundOperations has no default gesture: the status bar shows what runs in the background
        // and brings it back with a click; the palette and the Commands menu do the same.
        Register("CommandPalette",   Key.F1);   // fuzzy command palette (Ctrl+Shift+P is taken by CopyNames)
        Register("Help",             Key.F1, ModifierKeys.Shift);   // what every function does
        Register("FindFiles",        Key.P, ModifierKeys.Control);   // fuzzy file picker over the project
        Register("QuickFilter",      Key.S, ModifierKeys.Control);   // Total Commander: quick search without a letter
        Register("ToggleIgnored",    Key.H, ModifierKeys.Control | ModifierKeys.Shift); // hide gitignored
        Register("ToggleHiddenFiles", Key.H, ModifierKeys.Control);                     // Far Manager: hidden files
        // ToggleExtensions and ToggleFoldersFirst have no default gesture: set once and left, they live in
        // the Settings menu, the palette and the details header's menu.
        Register("NameSearch",       Key.P, ModifierKeys.Control | ModifierKeys.Alt);   // instant, whole disk
        Register("FolderTree",       Key.B, ModifierKeys.Control);                      // docked tree, editor convention
        Register("RunGadget",        Key.F2, ModifierKeys.Control);                     // any CLI, by name
        // GadgetsManager and SavedOperationsManager (6.50) have no default gesture on purpose: they
        // are reached from the Commands menu, the palette and the hint in their pickers, and the
        // hotkey dialog can still give them one. ShellsManager likewise: its picker is the box left of
        // the command line.

        // ── Git as a process (worktrees, the write minimum, clone) ────────────
        Register("GitDiff",          Key.G, ModifierKeys.Control);
        Register("GitWorktrees",     Key.G, ModifierKeys.Control | ModifierKeys.Shift);  // dashboard
        Register("GitNewWorktree",   Key.N, ModifierKeys.Control | ModifierKeys.Alt);    // worktree for a task
        Register("GitCommit",        Key.G, ModifierKeys.Control | ModifierKeys.Alt);
        Register("GitBranch",        Key.G, ModifierKeys.Alt);
        Register("GitStage",         Key.S, ModifierKeys.Control | ModifierKeys.Shift);
        Register("GitUnstage",       Key.U, ModifierKeys.Control | ModifierKeys.Shift);
        Register("GitStash",         Key.S, ModifierKeys.Alt);
        Register("GitStashPop",      Key.S, ModifierKeys.Alt | ModifierKeys.Shift);
        Register("GitClone",         Key.C, ModifierKeys.Control | ModifierKeys.Alt);

        // ── Agent-aware layer (6D) ────────────────────────────────────────────
        Register("AgentChanges",     Key.D, ModifierKeys.Control | ModifierKeys.Shift);  // review dashboard
        Register("AgentLaunch",      Key.R, ModifierKeys.Control | ModifierKeys.Shift);  // agent on a task

        // ── Workspaces (projects) ─────────────────────────────────────────────
        Register("WorkspaceSwitcher", Key.W, ModifierKeys.Alt);
        Register("NewWorkspace",      Key.W, ModifierKeys.Alt | ModifierKeys.Shift);
        Register("SaveWorkspace",     Key.W, ModifierKeys.Control | ModifierKeys.Alt);
        Register("WorkspaceNotes",    Key.N, ModifierKeys.Alt);   // per-workspace scratchpad
        Register("WorkspaceTerminal", Key.T, ModifierKeys.Alt);   // replay the saved command
        Register("WorkspaceNewWindow", Key.W, ModifierKeys.Control | ModifierKeys.Shift);
        Register("WorkspaceSidebar",   Key.B, ModifierKeys.Alt);   // pinned rail, editor convention
        // Committable definition layer (*.tcvproj), ADR-0011
        Register("WorkspaceOpenDefinition",   Key.O, ModifierKeys.Control | ModifierKeys.Alt);
        Register("WorkspaceExportDefinition", Key.E, ModifierKeys.Control | ModifierKeys.Alt);
        Register("Workspace1", Key.D1, ModifierKeys.Alt);
        Register("Workspace2", Key.D2, ModifierKeys.Alt);
        Register("Workspace3", Key.D3, ModifierKeys.Alt);
        Register("Workspace4", Key.D4, ModifierKeys.Alt);
        Register("Workspace5", Key.D5, ModifierKeys.Alt);
        Register("Workspace6", Key.D6, ModifierKeys.Alt);
        Register("Workspace7", Key.D7, ModifierKeys.Alt);
        Register("Workspace8", Key.D8, ModifierKeys.Alt);
        Register("Workspace9", Key.D9, ModifierKeys.Alt);
        // Name, icon, colour, group, pin — one window. The gesture is the one rename had, and the
        // dialog opens with the name selected, so renaming is still "Ctrl+Alt+R, type, Enter".
        Register("WorkspaceProperties", Key.R, ModifierKeys.Control | ModifierKeys.Alt);
        Register("BookmarksManager", Key.D, ModifierKeys.Control | ModifierKeys.Alt);

        // ── Chords (6.20) ─────────────────────────────────────────────────────
        // Ctrl+K is the editor-world chord prefix. Commands that live here are the ones with no
        // free single gesture left — not a second way to reach something already bound.
        Chord("ColumnSettings",  new KeyStroke(Key.K, ModifierKeys.Control), new KeyStroke(Key.C, ModifierKeys.Control));
        Chord("ExportSettings",  new KeyStroke(Key.K, ModifierKeys.Control), new KeyStroke(Key.E, ModifierKeys.Control));
        Chord("ImportSettings",  new KeyStroke(Key.K, ModifierKeys.Control), new KeyStroke(Key.M, ModifierKeys.Control));
        Chord("DropStackShow",   new KeyStroke(Key.K, ModifierKeys.Control), new KeyStroke(Key.D, ModifierKeys.Control));
        Chord("DropStackAdd",    new KeyStroke(Key.K, ModifierKeys.Control), new KeyStroke(Key.A, ModifierKeys.Control));
        Chord("RebuildNameIndex", new KeyStroke(Key.K, ModifierKeys.Control), new KeyStroke(Key.R, ModifierKeys.Control));
        Chord("SavedOperations", new KeyStroke(Key.K, ModifierKeys.Control), new KeyStroke(Key.O, ModifierKeys.Control));
        Chord("CompareDirectories", new KeyStroke(Key.K, ModifierKeys.Control), new KeyStroke(Key.S, ModifierKeys.Control));
        // The two AI features that are niche by design: renaming is scoped to the junk corner and
        // semantic search complements Ctrl+F rather than replacing it, so neither earns a single gesture.
        Chord("AiRename",        new KeyStroke(Key.K, ModifierKeys.Control), new KeyStroke(Key.N, ModifierKeys.Control));
        Chord("SemanticSearch",  new KeyStroke(Key.K, ModifierKeys.Control), new KeyStroke(Key.F, ModifierKeys.Control));
        // Navigation helpers that used to live in the menu only: V for the pasted path, X for eXplorer.
        Chord("GoToClipboardPath", new KeyStroke(Key.K, ModifierKeys.Control), new KeyStroke(Key.V, ModifierKeys.Control));
        Chord("OpenInExplorer",  new KeyStroke(Key.K, ModifierKeys.Control), new KeyStroke(Key.X, ModifierKeys.Control));

        // Quick search has no gesture: it starts by typing a letter in the file list, so there
        // is nothing here for the hotkey dialog to rebind.
    }

    /// <summary>
    /// Replaces the gestures the user's own tools declare (<c>UserTool:&lt;name&gt;</c> ids, 6.29).
    /// A tool cannot take a gesture a built-in command already owns, nor make one unreachable by
    /// declaring a chord on top of it (see <see cref="IndexUserTool"/>): the index below lets the
    /// earlier registration win, and the alternative — a shipped shortcut that silently stops
    /// working after someone types "Ctrl+C" into the tools dialog — is worse than a tool that has
    /// to pick a free key. The tool still runs from the menu and the palette either way.
    /// </summary>
    public void SetUserToolBindings(IEnumerable<(string CommandId, string Gesture)> tools)
    {
        _userTools.Clear();
        foreach (var (commandId, gesture) in tools)
            if (TryParseBinding(commandId, gesture, out var binding)) _userTools.Add(binding!);

        Reload();
    }

    private void Register(string commandId, Key key, ModifierKeys modifiers = ModifierKeys.None)
    {
        _bindings[commandId] = new HotkeyBinding(commandId, key, modifiers);
    }

    /// <summary>Registers a two-stroke chord, editor style: <c>Chord("X", Ctrl+K, Ctrl+S)</c>.</summary>
    private void Chord(string commandId, KeyStroke first, KeyStroke second)
    {
        _bindings[commandId] = new HotkeyBinding(commandId, first, second);
    }

    private void Alias(string commandId, Key key, ModifierKeys modifiers = ModifierKeys.None)
    {
        _aliases.Add(new HotkeyBinding(commandId, key, modifiers));
    }

    /// <summary>
    /// Command ids that were merged into another command. A gesture the user saved under the old id
    /// moves with it instead of turning into a binding that runs nothing.
    /// </summary>
    private static readonly Dictionary<string, string> MergedIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WorkspaceRename"] = "WorkspaceProperties",
    };

    /// <summary>The command a settings.json entry applies to: itself, or the command it was merged into.</summary>
    public static string CanonicalCommandId(string savedId) => MergedIds.GetValueOrDefault(savedId, savedId);

    /// <summary>Applies settings.json overrides and reports which commands actually took one.</summary>
    private HashSet<string> ApplyUserOverrides()
    {
        var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (savedId, keyStr) in _settings.Hotkeys)
        {
            // An entry under a merged id gives way to one saved under the new id itself.
            var commandId = CanonicalCommandId(savedId);
            if (applied.Contains(commandId) && !string.Equals(commandId, savedId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryParseBinding(commandId, keyStr, out var binding)) continue;
            _bindings[commandId] = binding!;
            applied.Add(commandId);
        }

        return applied;
    }

    /// <summary>
    /// Resolution order is fixed so a hand-edited settings.json cannot produce ambiguity:
    /// user-overridden gestures win, then untouched defaults, then aliases. The priority pass
    /// walks the overrides that parsed, not every key in settings.json — a malformed entry
    /// must leave its command on the default tier instead of promoting the default gesture.
    /// </summary>
    private void RebuildIndex(HashSet<string> overriddenCommands)
    {
        _byGesture.Clear();
        _byChord.Clear();
        _chordPrefixes.Clear();

        foreach (var id in overriddenCommands)
            if (_bindings.TryGetValue(id, out var overridden))
                Index(overridden);

        foreach (var binding in _bindings.Values)
            Index(binding);

        foreach (var alias in _aliases)
            Index(alias);

        // Last tier: a user tool only gets a gesture nothing else claimed (see SetUserToolBindings).
        foreach (var tool in _userTools)
            IndexUserTool(tool);

        // A chord's first stroke must not also fire a single-key command, or the second press
        // would arrive after the command already ran. The chord wins: it is the more specific
        // gesture, and the alternative is a binding the user can never trigger.
        foreach (var prefix in _chordPrefixes)
            _byGesture.Remove(prefix);
    }

    /// <summary>
    /// Indexes a user tool's gesture under the rule of <see cref="SetUserToolBindings"/>: it may only
    /// take what nothing else claimed. <see cref="Index"/> alone enforces that for a single stroke
    /// (<c>TryAdd</c> leaves the earlier owner in place), but not for a chord: registering
    /// "Ctrl+F Ctrl+B" would add Ctrl+F to <see cref="_chordPrefixes"/>, and the prefix pass below
    /// then strips Ctrl+F from <see cref="_byGesture"/> — Search stops opening and swallows the next
    /// key instead, while the menu still advertises Ctrl+F for it. So a tool's chord is dropped when
    /// its first stroke already fires a command on its own.
    /// </summary>
    private void IndexUserTool(HotkeyBinding tool)
    {
        if (tool.IsChord && _byGesture.ContainsKey(tool.First)) return;
        Index(tool);
    }

    private void Index(HotkeyBinding binding)
    {
        if (binding.Second is { } second)
        {
            _byChord.TryAdd((binding.First, second), binding.CommandId);
            _chordPrefixes.Add(binding.First);
        }
        else
        {
            _byGesture.TryAdd(binding.First, binding.CommandId);
        }
    }

    /// <summary>
    /// The gesture shown for a command. For a user tool (6.29) it is reported only when the gesture
    /// actually resolves back to that tool — a tool whose key was already taken has none, and the
    /// palette must not advertise a shortcut that runs something else.
    /// </summary>
    public HotkeyBinding? GetBinding(string commandId)
    {
        if (_bindings.TryGetValue(commandId, out var b)) return b;

        var tool = _userTools.FirstOrDefault(
            t => string.Equals(t.CommandId, commandId, StringComparison.OrdinalIgnoreCase));
        if (tool is null) return null;

        var won = tool.Second is { } second
            ? _byChord.TryGetValue((tool.First, second), out var chordOwner) && chordOwner == tool.CommandId
            : _byGesture.TryGetValue(tool.First, out var owner) && owner == tool.CommandId;
        return won ? tool : null;
    }

    /// <summary>Resolves a pressed gesture to a command id, or null when nothing is bound.</summary>
    public string? GetCommandId(Key key, ModifierKeys modifiers) =>
        _byGesture.TryGetValue(new KeyStroke(key, modifiers), out var id) ? id : null;

    /// <summary>
    /// True when this stroke starts a chord and the dispatcher should hold the next key press for
    /// <see cref="GetChordCommandId"/> instead of acting on it.
    /// </summary>
    public bool IsChordPrefix(Key key, ModifierKeys modifiers) =>
        _chordPrefixes.Contains(new KeyStroke(key, modifiers));

    /// <summary>Resolves the second stroke of a chord, or null when the pair is not bound.</summary>
    public string? GetChordCommandId(
        Key firstKey, ModifierKeys firstModifiers, Key secondKey, ModifierKeys secondModifiers) =>
        _byChord.TryGetValue(
            (new KeyStroke(firstKey, firstModifiers), new KeyStroke(secondKey, secondModifiers)),
            out var id) ? id : null;

    public IReadOnlyDictionary<string, HotkeyBinding> AllBindings => _bindings;

    /// <summary>
    /// Parses a stored gesture: "Ctrl+S", or "Ctrl+K Ctrl+S" for a chord. Anything longer than two
    /// strokes is rejected — the dispatcher only holds one pending stroke, and a three-key gesture
    /// would parse into something that never fires.
    /// </summary>
    public static bool TryParseBinding(string commandId, string keyStr, out HotkeyBinding? binding)
    {
        binding = null;
        if (string.IsNullOrWhiteSpace(keyStr)) return false;

        var strokes = keyStr.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (strokes.Length is 0 or > 2) return false;

        if (!KeyStroke.TryParse(strokes[0], out var first)) return false;
        if (strokes.Length == 1)
        {
            binding = new HotkeyBinding(commandId, first);
            return true;
        }

        if (!KeyStroke.TryParse(strokes[1], out var second)) return false;
        binding = new HotkeyBinding(commandId, first, second);
        return true;
    }
}
