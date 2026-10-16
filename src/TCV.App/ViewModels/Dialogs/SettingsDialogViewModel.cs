using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Localization;
using TCV.App.Settings;
using TCV.App.Workspaces;

namespace TCV.App.ViewModels.Dialogs;

public partial class SettingsDialogViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly SettingsSerializer _serializer;
    private readonly Services.ExternalToolService? _externalTools;
    private readonly Services.ShellService? _shells;
    private readonly string _languageOnOpen;

    /// <summary>The live shortcut of «open in the default editor», for the label next to its picker.</summary>
    public string EditorGesture { get; init; } = "Ctrl+E";

    /// <summary>The live shortcut of «run the default agent», for the label next to its picker.</summary>
    public string AgentGesture { get; init; } = "Ctrl+Shift+A";

    public string DefaultEditorLabel => string.Format(LocalizationManager.Get("Settings_ExtDefaultFor"), EditorGesture);

    public string DefaultAgentLabel => string.Format(LocalizationManager.Get("Settings_ExtDefaultFor"), AgentGesture);

    /// <param name="externalTools">Optional: only the «Detect installed» button needs it, so tests
    /// and any caller without the service still get a working dialog.</param>
    /// <param name="shells">Optional for the same reason: only finding the installed shells needs it.</param>
    public SettingsDialogViewModel(
        AppSettings settings,
        SettingsSerializer serializer,
        Services.ExternalToolService? externalTools = null,
        Services.ShellService? shells = null)
    {
        _settings = settings;
        _serializer = serializer;
        _externalTools = externalTools;
        _shells = shells;
        _languageOnOpen = LocalizationManager.CurrentLanguage;
        LoadFromSettings();
    }

    // ── Theme ─────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _themeName = "Dark";
    [ObservableProperty] private bool _useSystemTheme;

    /// <summary>The themes ThemeManager ships, with names in the user's language.</summary>
    public ThemeOption[] AvailableThemes { get; } =
    [
        new("Dark", LocalizationManager.Get("Settings_ThemeDark")),
        new("Light", LocalizationManager.Get("Settings_ThemeLight")),
    ];

    /// <summary>The picker's selection; <see cref="ThemeName"/> is the stable name behind it.</summary>
    public ThemeOption? SelectedTheme
    {
        get => AvailableThemes.FirstOrDefault(t => t.Name == ThemeName) ?? AvailableThemes[0];
        set
        {
            if (value is not null && value.Name != ThemeName)
            {
                ThemeName = value.Name;
            }
        }
    }

    partial void OnThemeNameChanged(string value) => OnPropertyChanged(nameof(SelectedTheme));

    // ── Accent colour (6.33) ──────────────────────────────────────────────────

    /// <summary>
    /// The theme's own accent, the presets, then "custom". Every preset is dark enough for the white
    /// text the themes draw on an accent fill to keep a 4.5:1 contrast; a custom colour too light for
    /// that gets black text instead (<see cref="AppearanceResources.ForegroundOn"/>).
    /// </summary>
    public AccentOption[] AccentOptions { get; } =
    [
        new(string.Empty, LocalizationManager.Get("Settings_AccentDefault")),
        new("#0063B1", LocalizationManager.Get("Settings_AccentBlue")),
        new("#00777A", LocalizationManager.Get("Settings_AccentTeal")),
        new("#107C10", LocalizationManager.Get("Settings_AccentGreen")),
        new("#744DA9", LocalizationManager.Get("Settings_AccentPurple")),
        new("#C25100", LocalizationManager.Get("Settings_AccentOrange")),
        new("#C42B1C", LocalizationManager.Get("Settings_AccentRed")),
        new(null, LocalizationManager.Get("Settings_AccentCustom")),
    ];

    /// <summary>The accent as typed: <c>#RRGGBB</c>, a forgiving spelling of it, or empty for the theme's own.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccentError))]
    private string _accentColor = string.Empty;

    /// <summary>The preset the typed colour matches, or "custom".</summary>
    [ObservableProperty] private AccentOption? _selectedAccent;

    /// <summary>The picker and the hex box update each other; this stops the echo.</summary>
    private bool _syncingAccent;

    partial void OnSelectedAccentChanged(AccentOption? value)
    {
        // "Custom" has no colour of its own: it leaves the box to the user.
        if (_syncingAccent || value is null || value.IsCustom)
        {
            return;
        }

        _syncingAccent = true;
        AccentColor = value.Hex!;
        _syncingAccent = false;
    }

    partial void OnAccentColorChanged(string value)
    {
        if (_syncingAccent)
        {
            return;
        }

        _syncingAccent = true;
        SelectedAccent = AccentOption.Match(AccentOptions, value);
        _syncingAccent = false;
    }

    /// <summary>Why the typed accent is refused, or empty. Parsed like a workspace colour.</summary>
    public string AccentError => WorkspaceColor.TryNormalize(AccentColor, out _)
        ? string.Empty
        : LocalizationManager.Get("Settings_AccentInvalid");

    // ── Panel font (6.33) ─────────────────────────────────────────────────────

    /// <summary>Family of the file lists; empty = the default (the UI font, the bundled IBM Plex Sans).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedPanelFont), nameof(PreviewFontFamily))]
    private string _panelFontFamily = string.Empty;

    [ObservableProperty] private double _panelFontSize = AppearanceResources.DefaultPanelFontSize;

    /// <summary>Windows' own file icons in the lists, as in Explorer, instead of the line icons.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LineFileIcons))]
    private bool _systemFileIcons;

    /// <summary>The other radio button of <see cref="SystemFileIcons"/>.</summary>
    public bool LineFileIcons
    {
        get => !SystemFileIcons;
        set => SystemFileIcons = !value;
    }

    public double[] AvailableFontSizes { get; } = Enumerable
        .Range((int)AppearanceResources.MinPanelFontSize,
            (int)(AppearanceResources.MaxPanelFontSize - AppearanceResources.MinPanelFontSize) + 1)
        .Select(size => (double)size)
        .ToArray();

    private FontOption[]? _availableFonts;

    /// <summary>
    /// "Default", then every installed family. Built when the picker first asks, so a dialog that
    /// never shows it (a test, say) does not enumerate the system fonts.
    /// </summary>
    public FontOption[] AvailableFonts => _availableFonts ??= BuildFontList(
        System.Windows.Media.Fonts.SystemFontFamilies.Select(f => f.Source),
        PanelFontFamily,
        LocalizationManager.Get("Settings_FontDefault"));

    /// <summary>The picker's selection; <see cref="PanelFontFamily"/> is the name behind it.</summary>
    public FontOption? SelectedPanelFont
    {
        get => AvailableFonts.FirstOrDefault(
            f => string.Equals(f.Name, PanelFontFamily, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is not null)
            {
                PanelFontFamily = value.Name;
            }
        }
    }

    /// <summary>The family the preview line is drawn in.</summary>
    public string PreviewFontFamily =>
        string.IsNullOrWhiteSpace(PanelFontFamily) ? AppearanceResources.DefaultFontFamily : PanelFontFamily;

    /// <summary>
    /// The font picker's entries: "default" first, then the installed families sorted by name. A
    /// family saved on another machine is listed too, so the picker shows what is stored instead of
    /// a blank.
    /// </summary>
    public static FontOption[] BuildFontList(IEnumerable<string> installed, string saved, string defaultLabel)
    {
        var names = installed
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!string.IsNullOrWhiteSpace(saved) && !names.Contains(saved, StringComparer.OrdinalIgnoreCase))
        {
            names.Add(saved);
        }

        names.Sort(StringComparer.CurrentCultureIgnoreCase);
        return [new FontOption(string.Empty, defaultLabel), .. names.Select(name => new FontOption(name, name))];
    }

    // ── Interface: which parts of the window are shown (6.33) ─────────────────

    [ObservableProperty] private bool _showFunctionKeyBar;
    [ObservableProperty] private bool _showDriveBar;
    [ObservableProperty] private bool _showTabBar;
    [ObservableProperty] private bool _showPathBar;
    [ObservableProperty] private bool _showViewToolbar;
    [ObservableProperty] private bool _showStatusBar;
    [ObservableProperty] private bool _showToolbar;
    [ObservableProperty] private bool _showCommandLine;
    [ObservableProperty] private bool _showAppStatusBar;
    [ObservableProperty] private bool _showWorkspaceSidebar;

    /// <summary>True in a guest window (<c>--workspace</c>), which keeps its changes for the session only (TRAP-22).</summary>
    public bool IsGuestWindow => _serializer.ReadOnly;

    /// <summary>Why settings.json could not be written, or empty; shown in the dialog, which then stays open.</summary>
    [ObservableProperty] private string _saveError = string.Empty;

    /// <summary>Raised after a successful save; the view closes on it.</summary>
    public event EventHandler? CloseRequested;

    // ── Language ──────────────────────────────────────────────────────────────

    /// <summary>Display names for the codes LocalizationManager actually ships a dictionary for.</summary>
    private static readonly Dictionary<string, string> _languageNames = new()
    {
        ["ru"] = "Русский",
        ["en"] = "English",
    };

    // Built from LocalizationManager's list so the dialog cannot offer a language that
    // ApplyLanguage would silently coerce back to English.
    public LanguageOption[] AvailableLanguages { get; } =
    [
        new(string.Empty, "Системный / System"),
        .. LocalizationManager.SupportedLanguages.Select(
            code => new LanguageOption(code, _languageNames.GetValueOrDefault(code, code))),
    ];

    [ObservableProperty] private LanguageOption _selectedLanguage;

    /// <summary>Language switches live so the effect is visible before saving.</summary>
    partial void OnSelectedLanguageChanged(LanguageOption? value)
    {
        if (value is null) return;
        LocalizationManager.SetLanguage(value.Code);
        OnPropertyChanged(nameof(LlmTimeoutLabel));
    }

    /// <summary>Called when the dialog is dismissed — puts back the language that was active on open.</summary>
    public void RevertUnsavedChanges()
    {
        if (LocalizationManager.CurrentLanguage != _languageOnOpen)
            LocalizationManager.SetLanguage(_languageOnOpen);
    }

    // ── Operations ────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _confirmDelete;
    [ObservableProperty] private bool _moveToRecycleBin;
    [ObservableProperty] private bool _confirmOverwrite;

    // ── Panels ────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _showHiddenFiles;
    [ObservableProperty] private bool _foldersFirst;
    [ObservableProperty] private bool _quickSearchJumps;

    // ── Color rules ───────────────────────────────────────────────────────────

    public System.Collections.ObjectModel.ObservableCollection<ColorRuleViewModel> ColorRules { get; } = new();

    [RelayCommand]
    private void AddColorRule()
    {
        ColorRules.Add(new ColorRuleViewModel { Pattern = "*.txt", Foreground = "#9CDCFE" });
    }

    [RelayCommand]
    private void RemoveColorRule(ColorRuleViewModel rule) => ColorRules.Remove(rule);

    // ── External tools: editors and agent CLIs ────────────────────────────────

    public System.Collections.ObjectModel.ObservableCollection<ExternalToolViewModel> Editors { get; } = new();
    public System.Collections.ObjectModel.ObservableCollection<ExternalToolViewModel> Agents { get; } = new();

    /// <summary>Name of the editor Ctrl+E uses; empty (no selection) = the first available one.</summary>
    [ObservableProperty] private ExternalToolViewModel? _defaultEditor;
    [ObservableProperty] private ExternalToolViewModel? _defaultAgent;

    [RelayCommand]
    private void AddEditor() => Editors.Add(new ExternalToolViewModel { Name = "VS Code", Command = "code", Arguments = "\"%P\" %F" });

    [RelayCommand]
    private void RemoveEditor(ExternalToolViewModel tool) => Editors.Remove(tool);

    [RelayCommand]
    private void AddAgent() => Agents.Add(new ExternalToolViewModel { Name = "Claude Code", Command = "claude", InTerminal = true });

    [RelayCommand]
    private void RemoveAgent(ExternalToolViewModel tool) => Agents.Remove(tool);

    /// <summary>
    /// Replaces both lists with what is actually installed. Detection is also what an empty list
    /// does at launch time, so this button is really "freeze the current autodetection and edit it".
    /// </summary>
    [RelayCommand]
    private void DetectTools()
    {
        if (_externalTools is null) return;

        Replace(Editors, _externalTools.DetectInstalled(Navigation.ExternalToolCatalog.Editors));
        Replace(Agents, _externalTools.DetectInstalled(Navigation.ExternalToolCatalog.Agents));
        DefaultEditor = Editors.FirstOrDefault();
        DefaultAgent = Agents.FirstOrDefault();
    }

    private static void Replace(
        System.Collections.ObjectModel.ObservableCollection<ExternalToolViewModel> target,
        IReadOnlyList<ExternalTool> tools)
    {
        target.Clear();
        foreach (var t in tools)
            target.Add(ExternalToolViewModel.From(t));
    }

    // ── Shells of the command line ────────────────────────────────────────────

    public System.Collections.ObjectModel.ObservableCollection<ShellProfileViewModel> Shells { get; } = new();

    /// <summary><see cref="InitialTab"/> value that opens the dialog on the Shells tab («Command-line shells…»).</summary>
    public const string ShellsTab = "Shells";

    /// <summary>
    /// Adds a row with a name of its own. An empty grid stands for «the shells found here», so the first
    /// row added would replace them all: they are written into the grid first and the new one joins them.
    /// </summary>
    [RelayCommand]
    private async Task AddShell()
    {
        if (Shells.Count == 0)
        {
            await FillDetectedShellsAsync();
        }

        var baseName = LocalizationManager.Get("Settings_ShellNewName");
        var name = baseName;
        for (var i = 2; Shells.Any(s => string.Equals(s.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)); i++)
        {
            name = $"{baseName} {i}";
        }

        Shells.Add(new ShellProfileViewModel { Name = name });
    }

    [RelayCommand]
    private void RemoveShell(ShellProfileViewModel shell) => Shells.Remove(shell);

    /// <summary>Replaces the grid with the shells installed here — «freeze the detection and edit it».</summary>
    [RelayCommand]
    private Task DetectShells() => FillDetectedShellsAsync();

    // Probes the disk and PATH, which may hang on a dead share: in the pool (INV-06).
    private async Task FillDetectedShellsAsync()
    {
        if (_shells is not { } service)
        {
            return;
        }

        var found = await Task.Run(service.DetectAgain);
        Shells.Clear();
        foreach (var shell in found)
        {
            Shells.Add(ShellProfileViewModel.From(shell));
        }
    }

    /// <summary>The first name two saved shells share, or null: the command line picks a shell by its name.</summary>
    public string? DuplicateShellName()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var shell in Shells.Where(s => s.IsUsable))
        {
            var name = shell.Name.Trim();
            if (!seen.Add(name))
            {
                return name;
            }
        }

        return null;
    }

    // ── F4: the editor a file is opened in ────────────────────────────────────

    /// <summary>The program F4 runs; empty keeps Windows' choice (the type's Edit verb, else Notepad).</summary>
    [ObservableProperty] private string _fileEditorCommand = string.Empty;

    /// <summary>Its arguments; <c>%F</c> is the file, and is appended when missing.</summary>
    [ObservableProperty] private string _fileEditorArguments = string.Empty;

    [ObservableProperty] private bool _fileEditorInTerminal;

    /// <summary>
    /// The editors Ctrl+E knows (the list above, else what is installed), offered to fill the F4 editor
    /// in with one pick. Detection probes PATH, which can hang on a dead share, so the list is filled
    /// off the UI thread after the dialog opens (INV-06); empty without the tool service.
    /// </summary>
    [ObservableProperty] private IReadOnlyList<ExternalTool> _fileEditorSuggestions = [];

    private async Task LoadFileEditorSuggestionsAsync()
    {
        if (_externalTools is not { } tools)
        {
            return;
        }

        try
        {
            FileEditorSuggestions = await Task.Run(tools.AvailableEditors);
        }
        catch (Exception)
        {
            // Only a convenience: the box still takes a typed or browsed program.
        }
    }

    /// <summary>Picking a suggestion copies its program; the arguments become just the file.</summary>
    [ObservableProperty] private ExternalTool? _fileEditorSuggestion;

    partial void OnFileEditorSuggestionChanged(ExternalTool? value)
    {
        if (value is null)
        {
            return;
        }

        FileEditorCommand = value.Command;
        FileEditorArguments = "%F";
        FileEditorInTerminal = value.InTerminal;
    }

    /// <summary>
    /// A program typed or browsed after a pick is no longer that pick: the box is cleared, so it does not
    /// name an editor the command is not, and choosing the same editor again fills it in again.
    /// </summary>
    partial void OnFileEditorCommandChanged(string value)
    {
        if (FileEditorSuggestion is { } picked
            && !string.Equals(picked.Command, value, StringComparison.OrdinalIgnoreCase))
        {
            FileEditorSuggestion = null;
        }
    }

    /// <summary>Set by the view: shows the file picker and returns the chosen program, or null.</summary>
    public Func<string?>? PickExecutable { get; set; }

    [RelayCommand]
    private void BrowseFileEditor()
    {
        if (PickExecutable?.Invoke() is { Length: > 0 } path)
        {
            FileEditorCommand = path;
        }
    }

    /// <summary>Back to Windows' own editor for each type.</summary>
    [RelayCommand]
    private void ResetFileEditor()
    {
        FileEditorCommand = string.Empty;
        FileEditorArguments = string.Empty;
        FileEditorInTerminal = false;
        FileEditorSuggestion = null;
    }

    // ── Git: the worktree-for-a-task hook (6.30) ──────────────────────────────
    //
    // These four used to be editable only by hand in settings.json, which made the setup hook —
    // the thing that decides whether a fresh worktree is usable — the least discoverable feature
    // of the git half.

    [ObservableProperty] private string _worktreeRoot = string.Empty;
    [ObservableProperty] private string _worktreeSetupCommand = string.Empty;
    [ObservableProperty] private bool _worktreeCreatesWorkspace;

    /// <summary>
    /// The carry globs as one editable block. A list is a grid in every other tab, but these are
    /// short tokens people paste from a <c>.gitignore</c>, so a text box is the honest control.
    /// </summary>
    [ObservableProperty] private string _worktreeCarryPatterns = string.Empty;

    private static readonly char[] _patternSeparators = [',', ';', '\n', '\r'];

    /// <summary>Splits the pattern box; empty stays empty, which is what "use the defaults" means.</summary>
    public static List<string> ParsePatterns(string text) =>
        text.Split(_patternSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    // ── Gadgets (6.30) ────────────────────────────────────────────────────────

    public System.Collections.ObjectModel.ObservableCollection<GadgetViewModel> Gadgets { get; } = new();

    /// <summary><see cref="InitialTab"/> value that opens the dialog on the Gadgets tab («Manage gadgets…»).</summary>
    public const string GadgetsTab = "Gadgets";

    /// <summary>The tab to open on; null for the first one.</summary>
    public string? InitialTab { get; init; }

    /// <summary>
    /// Adds a row. The first one is a working example; later ones get a name of their own and an
    /// empty command to fill in. Every row used to be "build", and the picker finds a gadget by its
    /// name, so the second one could never be run.
    /// </summary>
    [RelayCommand]
    private void AddGadget()
    {
        if (Gadgets.Count == 0)
        {
            Gadgets.Add(new GadgetViewModel { Name = "build", Command = "dotnet", Arguments = "build", InTerminal = true });
            return;
        }

        var baseName = LocalizationManager.Get("Settings_GadgetNewName");
        var name = baseName;
        for (var i = 2; Gadgets.Any(g => string.Equals(g.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)); i++)
        {
            name = $"{baseName} {i}";
        }

        Gadgets.Add(new GadgetViewModel { Name = name, InTerminal = true });
    }

    [RelayCommand]
    private void RemoveGadget(GadgetViewModel gadget) => Gadgets.Remove(gadget);

    /// <summary>
    /// The first name two saved gadgets share, or null. The picker runs a gadget by its name, so a
    /// second one under the same name would be listed and never start.
    /// </summary>
    public string? DuplicateGadgetName()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var gadget in Gadgets.Where(g => g.IsUsable))
        {
            var name = gadget.Name.Trim();
            if (!seen.Add(name))
            {
                return name;
            }
        }

        return null;
    }

    // ── Agent layer (6D) ──────────────────────────────────────────────────────
    //
    // Only the values worth typing live here. The approved directories are edited in the «Agent
    // changes» dashboard (Ctrl+Shift+D) instead: granting an agent access to a folder is a decision
    // made while looking at that folder, not while scrolling a settings tab — and the dashboard is
    // also where the consequence is visible.

    [ObservableProperty] private bool _mcpEnabled;
    [ObservableProperty] private bool _mcpAutoStart;
    [ObservableProperty] private bool _mcpAllowWrites;
    [ObservableProperty] private bool _mcpAllowRemotePanels;
    [ObservableProperty] private int _mcpPort;
    [ObservableProperty] private int _approvalTimeoutSeconds;

    [ObservableProperty] private string _llmEndpoint = string.Empty;
    [ObservableProperty] private string _llmApiKey = string.Empty;
    [ObservableProperty] private string _llmModel = string.Empty;
    [ObservableProperty] private string _embeddingModel = string.Empty;

    /// <summary>
    /// How long a model call may take. A slow local model needs more than the default minute, and
    /// the «did not answer in time» message sends the user here.
    /// </summary>
    [ObservableProperty] private int _llmTimeoutSeconds;

    /// <summary>The label of the timeout box, with the accepted range in it.</summary>
    public string LlmTimeoutLabel => string.Format(
        LocalizationManager.Get("Settings_AgentLlmTimeout"),
        TCV.Ai.Llm.LlmOptions.MinTimeoutSeconds, TCV.Ai.Llm.LlmOptions.MaxTimeoutSeconds);

    // ── Save / Cancel ─────────────────────────────────────────────────────────

    private bool _loadedShowHidden;
    private bool _loadedFoldersFirst;

    /// <summary>True when the last save changed "show hidden files".</summary>
    public bool HiddenFilesChanged { get; private set; }

    /// <summary>True when the last save changed "folders first".</summary>
    public bool FoldersFirstChanged { get; private set; }

    [RelayCommand]
    private void Save()
    {
        // The theme would quietly ignore a colour that does not parse; refuse it here instead and
        // keep the dialog open, the same as a failed write.
        if (!WorkspaceColor.TryNormalize(AccentColor, out _))
        {
            SaveError = LocalizationManager.Get("Settings_AccentInvalid");
            return;
        }

        if (DuplicateGadgetName() is { } duplicate)
        {
            SaveError = string.Format(LocalizationManager.Get("Settings_GadgetDuplicate"), duplicate);
            return;
        }

        if (DuplicateShellName() is { } duplicateShell)
        {
            SaveError = string.Format(LocalizationManager.Get("Settings_ShellDuplicate"), duplicateShell);
            return;
        }

        // Refused rather than clamped: a silently changed number would leave the user wondering why
        // the model still gives up early.
        if (!TCV.Ai.Llm.LlmOptions.IsValidTimeout(LlmTimeoutSeconds))
        {
            SaveError = string.Format(
                LocalizationManager.Get("Settings_AgentLlmTimeoutInvalid"),
                TCV.Ai.Llm.LlmOptions.MinTimeoutSeconds, TCV.Ai.Llm.LlmOptions.MaxTimeoutSeconds);
            return;
        }

        // Only what the user actually changed is written to the panels: each panel has its own
        // live state (a toolbar toggle), and one checkbox must not overwrite both with a stale value.
        HiddenFilesChanged = ShowHiddenFiles != _loadedShowHidden;
        FoldersFirstChanged = FoldersFirst != _loadedFoldersFirst;

        // Written from a copy first: a failed write keeps the dialog open, and a Cancel after that
        // must not leave these values in the live settings for the next save (on exit) to persist.
        var draft = SettingsCopy.Of(_settings);
        ApplyTo(draft);

        // A guest window keeps the values for its own session; the serializer refuses to write.
        if (!_serializer.Save(draft) && !_serializer.ReadOnly)
        {
            SaveError = string.Format(LocalizationManager.Get("Settings_SaveFailed"), _serializer.LastSaveError);
            return;
        }

        ApplyTo(_settings);
        SaveError = string.Empty;
        SavedSuccessfully = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Writes the dialog's values into <paramref name="target"/>.</summary>
    private void ApplyTo(AppSettings target)
    {
        target.Theme.ThemeName = ThemeName;
        target.Theme.UseSystemTheme = UseSystemTheme;
        target.Theme.Language = SelectedLanguage?.Code ?? string.Empty;
        // Validated by Save; stored in the canonical #RRGGBB spelling.
        target.Theme.AccentColor = WorkspaceColor.TryNormalize(AccentColor, out var accent) ? accent : string.Empty;
        target.Theme.PanelFontFamily = PanelFontFamily.Trim();
        target.Theme.PanelFontSize = AppearanceResources.ClampFontSize(PanelFontSize);
        target.Theme.SystemFileIcons = SystemFileIcons;

        target.Layout.ShowFunctionKeyBar = ShowFunctionKeyBar;
        target.Layout.ShowDriveBar = ShowDriveBar;
        target.Layout.ShowTabBar = ShowTabBar;
        target.Layout.ShowPathBar = ShowPathBar;
        target.Layout.ShowViewToolbar = ShowViewToolbar;
        target.Layout.ShowStatusBar = ShowStatusBar;
        target.Layout.ShowToolbar = ShowToolbar;
        target.Layout.ShowCommandLine = ShowCommandLine;
        target.Layout.ShowAppStatusBar = ShowAppStatusBar;
        target.ShowWorkspaceSidebar = ShowWorkspaceSidebar;

        target.Operations.ConfirmDelete = ConfirmDelete;
        target.Operations.MoveToRecycleBin = MoveToRecycleBin;
        target.Operations.ConfirmOverwrite = ConfirmOverwrite;
        target.QuickSearchJumps = QuickSearchJumps;

        if (HiddenFilesChanged)
        {
            target.LeftPanel.ShowHiddenFiles = ShowHiddenFiles;
            target.RightPanel.ShowHiddenFiles = ShowHiddenFiles;
        }

        if (FoldersFirstChanged)
        {
            target.LeftPanel.FoldersFirst = FoldersFirst;
            target.RightPanel.FoldersFirst = FoldersFirst;
        }

        target.ColorRules = ColorRules.Select(r => new ColorRule
        {
            Pattern = r.Pattern,
            Foreground = r.Foreground,
            Background = r.Background,
            ApplyToDirectories = r.ApplyToDirectories,
        }).ToList();

        // Rows without a command would show up in the picker and then fail to start — drop them.
        target.ExternalTools.Editors = Editors.Where(t => t.IsUsable).Select(t => t.ToModel()).ToList();
        target.ExternalTools.Agents = Agents.Where(t => t.IsUsable).Select(t => t.ToModel()).ToList();
        // Only an actual selection is written. In the autodetect state the grids are empty, so there
        // is nothing to select — and blanking the name here would throw away the default the
        // Ctrl+Shift+E / Ctrl+Alt+A pickers persisted, on a save about something else entirely.
        if (DefaultEditor is not null) target.ExternalTools.DefaultEditor = DefaultEditor.Name.Trim();
        if (DefaultAgent is not null) target.ExternalTools.DefaultAgent = DefaultAgent.Name.Trim();
        target.ExternalTools.FileEditor = new ExternalTool
        {
            Command = FileEditorCommand.Trim(),
            Arguments = FileEditorArguments.Trim(),
            InTerminal = FileEditorInTerminal,
        };

        // A row without a program would be offered by the command line and then fail to start. The
        // picked shell stays as it is: one that is gone falls back to the first (ShellCatalog.Pick).
        target.Shells.Profiles = Shells.Where(s => s.IsUsable).Select(s => s.ToModel()).ToList();

        target.Git.WorktreeRoot = WorktreeRoot.Trim();
        target.Git.WorktreeSetupCommand = WorktreeSetupCommand.Trim();
        target.Git.WorktreeCreatesWorkspace = WorktreeCreatesWorkspace;
        target.Git.WorktreeCarryPatterns = ParsePatterns(WorktreeCarryPatterns);

        // Same rule as the editors grid: a row without a command would show up in the Ctrl+F2
        // picker and then fail to start.
        target.Gadgets = Gadgets.Where(g => g.IsUsable).Select(g => g.ToModel()).ToList();

        target.Agent.McpEnabled = McpEnabled;
        target.Agent.McpAutoStart = McpAutoStart;
        target.Agent.McpAllowWrites = McpAllowWrites;
        target.Agent.McpAllowRemotePanels = McpAllowRemotePanels;
        // 0 means "take a free one"; a negative number would just fail to bind.
        target.Agent.McpPort = Math.Clamp(McpPort, 0, 65535);
        target.Agent.ApprovalTimeoutSeconds = Math.Clamp(ApprovalTimeoutSeconds, 10, 3600);
        target.Agent.LlmEndpoint = LlmEndpoint.Trim();
        target.Agent.LlmApiKey = LlmApiKey.Trim();
        target.Agent.LlmModel = LlmModel.Trim();
        target.Agent.EmbeddingModel = EmbeddingModel.Trim();
        target.Agent.LlmTimeoutSeconds = TCV.Ai.Llm.LlmOptions.ClampTimeout(LlmTimeoutSeconds);
    }

    [ObservableProperty] private bool _savedSuccessfully;

    private void LoadFromSettings()
    {
        ThemeName = _settings.Theme.ThemeName;
        UseSystemTheme = _settings.Theme.UseSystemTheme;
        SelectedLanguage = AvailableLanguages.FirstOrDefault(
            l => l.Code == _settings.Theme.Language) ?? AvailableLanguages[0];

        // An unparsable hand-edited accent is shown as it is, with the error under it, rather than
        // silently replaced by the default.
        AccentColor = _settings.Theme.AccentColor;
        SelectedAccent = AccentOption.Match(AccentOptions, AccentColor);
        PanelFontFamily = _settings.Theme.PanelFontFamily.Trim();
        PanelFontSize = AppearanceResources.ClampFontSize(_settings.Theme.PanelFontSize);
        SystemFileIcons = _settings.Theme.SystemFileIcons;

        ShowFunctionKeyBar = _settings.Layout.ShowFunctionKeyBar;
        ShowDriveBar = _settings.Layout.ShowDriveBar;
        ShowTabBar = _settings.Layout.ShowTabBar;
        ShowPathBar = _settings.Layout.ShowPathBar;
        ShowViewToolbar = _settings.Layout.ShowViewToolbar;
        ShowStatusBar = _settings.Layout.ShowStatusBar;
        ShowToolbar = _settings.Layout.ShowToolbar;
        ShowCommandLine = _settings.Layout.ShowCommandLine;
        ShowAppStatusBar = _settings.Layout.ShowAppStatusBar;
        ShowWorkspaceSidebar = _settings.ShowWorkspaceSidebar;

        ConfirmDelete = _settings.Operations.ConfirmDelete;
        MoveToRecycleBin = _settings.Operations.MoveToRecycleBin;
        ConfirmOverwrite = _settings.Operations.ConfirmOverwrite;
        ShowHiddenFiles = _loadedShowHidden = _settings.LeftPanel.ShowHiddenFiles;
        FoldersFirst = _loadedFoldersFirst = _settings.LeftPanel.FoldersFirst;
        QuickSearchJumps = _settings.QuickSearchJumps;

        foreach (var rule in _settings.ColorRules)
            ColorRules.Add(new ColorRuleViewModel
            {
                Pattern = rule.Pattern,
                Foreground = rule.Foreground,
                Background = rule.Background,
                ApplyToDirectories = rule.ApplyToDirectories,
            });

        // Empty lists stay empty here: that state means "autodetect at launch", and filling the grid
        // with detected tools would silently freeze today's detection into settings.json on save.
        Replace(Editors, _settings.ExternalTools.Editors);
        Replace(Agents, _settings.ExternalTools.Agents);
        DefaultEditor = Editors.FirstOrDefault(
            t => t.Name == _settings.ExternalTools.DefaultEditor) ?? Editors.FirstOrDefault();
        DefaultAgent = Agents.FirstOrDefault(
            t => t.Name == _settings.ExternalTools.DefaultAgent) ?? Agents.FirstOrDefault();

        FileEditorCommand = _settings.ExternalTools.FileEditor.Command;
        FileEditorArguments = _settings.ExternalTools.FileEditor.Arguments;
        FileEditorInTerminal = _settings.ExternalTools.FileEditor.InTerminal;
        _ = LoadFileEditorSuggestionsAsync();

        // Empty stays empty, as with the editors: it means «the shells found here».
        foreach (var shell in _settings.Shells.Profiles)
        {
            Shells.Add(ShellProfileViewModel.From(shell));
        }

        WorktreeRoot = _settings.Git.WorktreeRoot;
        WorktreeSetupCommand = _settings.Git.WorktreeSetupCommand;
        WorktreeCreatesWorkspace = _settings.Git.WorktreeCreatesWorkspace;
        WorktreeCarryPatterns = string.Join(Environment.NewLine, _settings.Git.WorktreeCarryPatterns);

        foreach (var gadget in _settings.Gadgets)
            Gadgets.Add(GadgetViewModel.From(gadget));

        McpEnabled = _settings.Agent.McpEnabled;
        McpAutoStart = _settings.Agent.McpAutoStart;
        McpAllowWrites = _settings.Agent.McpAllowWrites;
        McpAllowRemotePanels = _settings.Agent.McpAllowRemotePanels;
        McpPort = _settings.Agent.McpPort;
        ApprovalTimeoutSeconds = _settings.Agent.ApprovalTimeoutSeconds;
        LlmEndpoint = _settings.Agent.LlmEndpoint;
        LlmApiKey = _settings.Agent.LlmApiKey;
        LlmModel = _settings.Agent.LlmModel;
        EmbeddingModel = _settings.Agent.EmbeddingModel;
        // Shown as the client uses it: a hand-edited 0 or 5000 is waited as 5 or 600.
        LlmTimeoutSeconds = TCV.Ai.Llm.LlmOptions.ClampTimeout(_settings.Agent.LlmTimeoutSeconds);
    }
}

/// <summary>One entry of the theme picker: the name ThemeManager knows, and how it reads.</summary>
public sealed record ThemeOption(string Name, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>One entry of the language picker. Empty <see cref="Code"/> = follow the system culture.</summary>
public sealed record LanguageOption(string Code, string DisplayName);

/// <summary>
/// One entry of the accent picker. <see cref="Hex"/> is <c>#RRGGBB</c>, empty for the theme's own
/// accent, or null for "custom" — the entry that leaves the colour to the hex box.
/// </summary>
public sealed record AccentOption(string? Hex, string DisplayName)
{
    public bool IsCustom => Hex is null;

    public override string ToString() => DisplayName;

    /// <summary>
    /// The entry a typed colour corresponds to: empty → the theme's default, a preset's value in any
    /// spelling → that preset, anything else (including text that is not a colour) → custom.
    /// </summary>
    public static AccentOption Match(IReadOnlyList<AccentOption> options, string? text)
    {
        var custom = options.First(o => o.IsCustom);
        if (!WorkspaceColor.TryNormalize(text, out var normalized))
        {
            return custom;
        }

        return options.FirstOrDefault(o => o.Hex == normalized) ?? custom;
    }
}

/// <summary>One entry of the panel font picker. Empty <see cref="Name"/> = the default family.</summary>
public sealed record FontOption(string Name, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>One editable row of the editors / agent-CLIs grids.</summary>
public sealed class ExternalToolViewModel : ObservableObject
{
    private string _name = string.Empty;
    private string _command = string.Empty;
    private string _arguments = string.Empty;
    private bool _inTerminal;

    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string Command { get => _command; set => SetProperty(ref _command, value); }
    public string Arguments { get => _arguments; set => SetProperty(ref _arguments, value); }
    public bool InTerminal { get => _inTerminal; set => SetProperty(ref _inTerminal, value); }

    /// <summary>A row is worth keeping once it has both a label and something to run.</summary>
    public bool IsUsable => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Command);

    public static ExternalToolViewModel From(ExternalTool tool) => new()
    {
        Name = tool.Name,
        Command = tool.Command,
        Arguments = tool.Arguments,
        InTerminal = tool.InTerminal,
    };

    public ExternalTool ToModel() => new()
    {
        Name = Name.Trim(),
        Command = Command.Trim(),
        Arguments = Arguments.Trim(),
        InTerminal = InTerminal,
    };
}

/// <summary>One editable row of the gadgets grid (6.30).</summary>
public sealed class GadgetViewModel : ObservableObject
{
    private string _name = string.Empty;
    private string _command = string.Empty;
    private string _arguments = string.Empty;
    private string _workingDirectory = string.Empty;
    private bool _inTerminal = true;

    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string Command { get => _command; set => SetProperty(ref _command, value); }
    public string Arguments { get => _arguments; set => SetProperty(ref _arguments, value); }
    public string WorkingDirectory { get => _workingDirectory; set => SetProperty(ref _workingDirectory, value); }
    public bool InTerminal { get => _inTerminal; set => SetProperty(ref _inTerminal, value); }

    /// <summary>The name is also the id the picker matches on, so both halves must be there.</summary>
    public bool IsUsable => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Command);

    public static GadgetViewModel From(Gadget gadget) => new()
    {
        Name = gadget.Name,
        Command = gadget.Command,
        Arguments = gadget.Arguments,
        WorkingDirectory = gadget.WorkingDirectory,
        InTerminal = gadget.InTerminal,
    };

    public Gadget ToModel() => new()
    {
        Name = Name.Trim(),
        Command = Unquote(Command),
        Arguments = Arguments.Trim(),
        WorkingDirectory = Unquote(WorkingDirectory),
        InTerminal = InTerminal,
    };

    /// <summary>
    /// The bare path: a program path is quoted by the launch itself (<c>ShellQuoting</c>), so one
    /// typed or pasted in quotes — the habit for <c>"C:\Program Files\…"</c> — came out quoted twice
    /// in the terminal and did not start.
    /// </summary>
    public static string Unquote(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"'
            ? trimmed[1..^1].Trim()
            : trimmed;
    }
}

/// <summary>One editable row of the Shells grid.</summary>
public sealed class ShellProfileViewModel : ObservableObject
{
    private string _name = string.Empty;
    private string _command = string.Empty;
    private string _arguments = string.Empty;

    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string Command { get => _command; set => SetProperty(ref _command, value); }
    public string Arguments { get => _arguments; set => SetProperty(ref _arguments, value); }

    /// <summary>The name is the id the command line picks by, and the program is what it starts.</summary>
    public bool IsUsable => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Command);

    public static ShellProfileViewModel From(ShellProfile shell) => new()
    {
        Name = shell.Name,
        Command = shell.Command,
        Arguments = shell.Arguments,
    };

    /// <summary>The program stored bare: the launch quotes it (see <see cref="GadgetViewModel.Unquote"/>).</summary>
    public ShellProfile ToModel() => new()
    {
        Name = Name.Trim(),
        Command = GadgetViewModel.Unquote(Command),
        Arguments = Arguments.Trim(),
    };
}

public sealed class ColorRuleViewModel : ObservableObject
{
    private string _pattern = string.Empty;
    private string _foreground = string.Empty;
    private string _background = string.Empty;
    private bool _applyToDirectories;

    public string Pattern { get => _pattern; set => SetProperty(ref _pattern, value); }
    public string Foreground { get => _foreground; set => SetProperty(ref _foreground, value); }
    public string Background { get => _background; set => SetProperty(ref _background, value); }
    public bool ApplyToDirectories { get => _applyToDirectories; set => SetProperty(ref _applyToDirectories, value); }
}
