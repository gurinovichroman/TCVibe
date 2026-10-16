using System.IO;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Localization;
using TCV.App.Operations;
using TCV.App.Services;
using TCV.Core.Interfaces;
using TCV.MultiRename;
using TCV.MultiRename.Rules;

namespace TCV.App.ViewModels.Dialogs;

public partial class MultiRenameDialogViewModel : ObservableObject
{
    private readonly IReadOnlyList<IFileEntry> _files;
    private readonly IVirtualFileSystem _fileSystem;
    private readonly MultiRenameProfileStore? _profileStore;
    private readonly OperationQueue? _queue;
    private readonly RenameEngine _engine;

    /// <summary>The renames performed by the last successful Start, kept for Undo.</summary>
    private IReadOnlyList<RenameAction> _lastActions = Array.Empty<RenameAction>();

    /// <summary>
    /// Per folder of the batch (keyed by the folder's path), the names there that are not part of the
    /// batch; null while unknown (still loading, or the listing failed — in which case a name that
    /// already exists is caught by the file system). A batch fed from search results spans several
    /// folders (TRAP-28), so one listing of the first file's folder is not enough.
    /// </summary>
    private IReadOnlyDictionary<string, HashSet<string>>? _otherNames;

    /// <summary>
    /// The moment [D] and [T] stand for. Fixed when the dialog opens, so the names the preview shows
    /// are exactly the names Rename produces — not whatever second the button happened to be pressed in.
    /// </summary>
    private readonly DateTime _referenceDate;

    /// <summary>Suppresses preview recomputation while a batch of properties is being applied.</summary>
    private bool _suppressRefresh;

    /// <param name="queue">
    /// When given, a rename run and its undo go through the operation queue and so into the
    /// operation log (INV-10). Without it (tests) the engine is called directly.
    /// </param>
    public MultiRenameDialogViewModel(
        IReadOnlyList<IFileEntry> files,
        IVirtualFileSystem fileSystem,
        MultiRenameProfileStore? profileStore = null,
        OperationQueue? queue = null,
        Func<DateTime>? clock = null)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _profileStore = profileStore;
        _queue = queue;
        _engine = new RenameEngine(_fileSystem);
        _referenceDate = (clock ?? (() => DateTime.Now))();

        ReloadProfileNames();

        // Initialise preview with default template showing original names
        RefreshPreview();

        // Which names are already taken is a server round trip on a remote panel: fetch it
        // without holding the dialog back, and refresh the conflict marks when it arrives.
        _ = LoadOtherNamesAsync();
    }

    // ── Template ──────────────────────────────────────────────────────────────

    /// <summary>The rename template string typed by the user.</summary>
    [ObservableProperty]
    private string _templateText = "[N].[E]";

    partial void OnTemplateTextChanged(string value) => RefreshPreview();

    // ── Counter settings ──────────────────────────────────────────────────────

    [ObservableProperty]
    private int _counterStart = 1;

    partial void OnCounterStartChanged(int value) => RefreshPreview();

    [ObservableProperty]
    private int _counterStep = 1;

    partial void OnCounterStepChanged(int value) => RefreshPreview();

    /// <summary>Digits a plain <c>[C]</c> is zero-padded to; 0 = no padding.</summary>
    [ObservableProperty]
    private int _counterWidth;

    partial void OnCounterWidthChanged(int value) => RefreshPreview();

    // ── Case change ───────────────────────────────────────────────────────────

    [ObservableProperty]
    private bool _applyUppercase;

    partial void OnApplyUppercaseChanged(bool value)
    {
        if (value)
        {
            ApplyLowercase = false;
            ApplyTitleCase = false;
        }

        RefreshPreview();
    }

    [ObservableProperty]
    private bool _applyLowercase;

    partial void OnApplyLowercaseChanged(bool value)
    {
        if (value)
        {
            ApplyUppercase = false;
            ApplyTitleCase = false;
        }

        RefreshPreview();
    }

    [ObservableProperty]
    private bool _applyTitleCase;

    partial void OnApplyTitleCaseChanged(bool value)
    {
        if (value)
        {
            ApplyUppercase = false;
            ApplyLowercase = false;
        }

        RefreshPreview();
    }

    // ── Regex replace ─────────────────────────────────────────────────────────

    [ObservableProperty]
    private string _regexFind = string.Empty;

    partial void OnRegexFindChanged(string value) => RefreshPreview();

    [ObservableProperty]
    private string _regexReplace = string.Empty;

    partial void OnRegexReplaceChanged(string value) => RefreshPreview();

    [ObservableProperty]
    private bool _regexCaseSensitive;

    partial void OnRegexCaseSensitiveChanged(bool value) => RefreshPreview();

    // ── Insert text ───────────────────────────────────────────────────────────

    [ObservableProperty]
    private string _insertText = string.Empty;

    partial void OnInsertTextChanged(string value) => RefreshPreview();

    /// <summary>Zero-based insert position; negative counts back from the end.</summary>
    [ObservableProperty]
    private int _insertPosition;

    partial void OnInsertPositionChanged(int value) => RefreshPreview();

    // ── Delete range ──────────────────────────────────────────────────────────

    [ObservableProperty]
    private int _deleteStart;

    partial void OnDeleteStartChanged(int value) => RefreshPreview();

    /// <summary>Characters to remove; 0 disables the rule.</summary>
    [ObservableProperty]
    private int _deleteCount;

    partial void OnDeleteCountChanged(int value) => RefreshPreview();

    /// <summary>When set, Insert/Delete skip the extension (Total Commander behaviour).</summary>
    [ObservableProperty]
    private bool _rangeRulesSkipExtension = true;

    partial void OnRangeRulesSkipExtensionChanged(bool value) => RefreshPreview();

    // ── Preview list ──────────────────────────────────────────────────────────

    public ObservableCollection<RenamePreviewItemViewModel> PreviewItems { get; } = new();

    [ObservableProperty]
    private string _statusText = string.Empty;

    // ── Result / undo state ───────────────────────────────────────────────────

    /// <summary>True while the last rename batch can still be reverted.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private bool _canUndo;

    /// <summary>True while a rename or an undo is running; the buttons are off so it cannot start twice.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private bool _isBusy;

    // ── Commands ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Renames the files. Unlike a plain OK, the dialog stays open afterwards (Total Commander
    /// style) so the batch can be undone; a second Start is blocked until Undo or Close.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRename))]
    private async Task ConfirmAsync()
    {
        IsBusy = true;
        try
        {
            // The folder may have changed since the dialog opened: look again before committing.
            _otherNames = await TryListOtherNamesAsync() ?? _otherNames;

            var previews = BuildPreviews(out var error);
            if (previews is null)
            {
                StatusText = error!;
                return;
            }

            var conflictCount = previews.Count(p => p.HasConflict);
            var changes = previews.Count(p => !p.HasConflict && p.OldName != p.NewName);
            if (changes == 0)
            {
                StatusText = conflictCount > 0
                    ? string.Format(LocalizationManager.Get("MR_NothingConflicts"), conflictCount)
                    : LocalizationManager.Get("MR_NothingToRename");
                return;
            }

            var result = await RunAsync(ct => _engine.ExecuteBatchAsync(_files, previews, ct), changes);

            // Whatever was renamed stays renamed and stays undoable, even when part of the batch failed.
            _lastActions = result.Performed;
            CanUndo = _lastActions.Count > 0;
            StatusText = DescribeResult(result, conflictCount);
        }
        catch (Exception ex)
        {
            StatusText = string.Format(LocalizationManager.Get("MR_Error"), ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRename() => !CanUndo && !IsBusy && _files.Count > 0;

    /// <summary>Reverts the last rename batch, renaming every file back to its original name.</summary>
    [RelayCommand(CanExecute = nameof(CanUndoNow))]
    private async Task UndoAsync()
    {
        if (_lastActions.Count == 0)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var actions = _lastActions;
            var result = await RunAsync(ct => _engine.UndoBatchAsync(actions, ct), actions.Count);
            var count = actions.Count;
            StatusText = result.Failures.Count == 0
                ? string.Format(LocalizationManager.Get("MR_Undone"), count)
                : string.Format(LocalizationManager.Get("MR_UndonePartly"), result.Performed.Count, count, result.Failures.Count);
            _lastActions = Array.Empty<RenameAction>();
            CanUndo = false;
        }
        catch (Exception ex)
        {
            StatusText = string.Format(LocalizationManager.Get("MR_UndoError"), ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanUndoNow() => CanUndo && !IsBusy;

    /// <summary>Runs a batch through the operation queue (so it is logged), or directly when there is none.</summary>
    private async Task<RenameBatchResult> RunAsync(Func<CancellationToken, Task<RenameBatchResult>> work, int count)
    {
        if (_queue is null)
        {
            return await work(CancellationToken.None);
        }

        var source = _files[0].Path.Parent?.Value ?? string.Empty;
        var operation = new RenameBatchOperation(work, source, count);
        await _queue.EnqueueAsync(operation);

        // The queue swallows a thrown exception into the operation's state; turn it back into a result.
        return operation.Result
               ?? new RenameBatchResult([], [new RenameFailure(string.Empty, string.Empty,
                   operation.FailureException?.Message ?? "the operation did not run")], operation.State == OperationState.Cancelled);
    }

    private static string DescribeResult(RenameBatchResult result, int skippedConflicts)
    {
        var done = result.Performed.Count;
        var text = string.Format(LocalizationManager.Get("MR_Renamed"), done);

        if (skippedConflicts > 0)
        {
            text += " " + string.Format(LocalizationManager.Get("MR_SkippedConflicts"), skippedConflicts);
        }

        if (result.Failures.Count > 0)
        {
            var first = result.Failures[0];
            text += " " + string.Format(LocalizationManager.Get("MR_Failed"), result.Failures.Count, first.OldName, first.Error);
        }

        if (result.Cancelled)
        {
            text += " " + LocalizationManager.Get("MR_Cancelled");
        }

        return text;
    }

    // ── Profiles ──────────────────────────────────────────────────────────────

    public bool ProfilesEnabled => _profileStore is not null;

    /// <summary>Names of saved rename profiles.</summary>
    public ObservableCollection<string> ProfileNames { get; } = new();

    /// <summary>Selecting a profile loads its settings into the dialog.</summary>
    [ObservableProperty]
    private string? _selectedProfile;

    partial void OnSelectedProfileChanged(string? value)
    {
        if (_suppressRefresh || value is null || _profileStore is null)
        {
            return;
        }

        var profile = _profileStore.Get(value);
        if (profile is not null)
        {
            ApplyProfile(profile);
        }
    }

    /// <summary>Name to save the current settings under (falls back to the selected profile).</summary>
    [ObservableProperty]
    private string _newProfileName = string.Empty;

    [RelayCommand]
    private void SaveProfile()
    {
        if (_profileStore is null)
        {
            return;
        }

        var name = (string.IsNullOrWhiteSpace(NewProfileName) ? SelectedProfile : NewProfileName)?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusText = LocalizationManager.Get("MR_ProfileNameNeeded");
            return;
        }

        try
        {
            _profileStore.Save(CaptureProfile(name));
            ReloadProfileNames();
            _suppressRefresh = true;
            SelectedProfile = name;
            _suppressRefresh = false;
            NewProfileName = string.Empty;
            StatusText = string.Format(LocalizationManager.Get("MR_ProfileSaved"), name);
        }
        catch (Exception ex)
        {
            StatusText = string.Format(LocalizationManager.Get("MR_ProfileSaveFailed"), ex.Message);
        }
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (_profileStore is null || string.IsNullOrWhiteSpace(SelectedProfile))
        {
            return;
        }

        var removed = SelectedProfile;
        try
        {
            _profileStore.Delete(removed);
            _suppressRefresh = true;
            SelectedProfile = null;
            _suppressRefresh = false;
            ReloadProfileNames();
            StatusText = string.Format(LocalizationManager.Get("MR_ProfileDeleted"), removed);
        }
        catch (Exception ex)
        {
            StatusText = string.Format(LocalizationManager.Get("MR_ProfileDeleteFailed"), ex.Message);
        }
    }

    private void ReloadProfileNames()
    {
        ProfileNames.Clear();
        if (_profileStore is null)
        {
            return;
        }

        foreach (var n in _profileStore.Names())
        {
            ProfileNames.Add(n);
        }
    }

    private MultiRenameProfile CaptureProfile(string name) => new()
    {
        Name = name,
        TemplateText = TemplateText,
        CounterStart = CounterStart,
        CounterStep = CounterStep,
        CounterWidth = CounterWidth,
        ApplyUppercase = ApplyUppercase,
        ApplyLowercase = ApplyLowercase,
        ApplyTitleCase = ApplyTitleCase,
        RegexFind = RegexFind,
        RegexReplace = RegexReplace,
        RegexCaseSensitive = RegexCaseSensitive,
        InsertText = InsertText,
        InsertPosition = InsertPosition,
        DeleteStart = DeleteStart,
        DeleteCount = DeleteCount,
        RangeRulesSkipExtension = RangeRulesSkipExtension,
    };

    private void ApplyProfile(MultiRenameProfile p)
    {
        _suppressRefresh = true;
        TemplateText = p.TemplateText;
        CounterStart = p.CounterStart;
        CounterStep = p.CounterStep;
        CounterWidth = p.CounterWidth;
        ApplyUppercase = p.ApplyUppercase;
        ApplyLowercase = p.ApplyLowercase;
        ApplyTitleCase = p.ApplyTitleCase;
        RegexFind = p.RegexFind;
        RegexReplace = p.RegexReplace;
        RegexCaseSensitive = p.RegexCaseSensitive;
        InsertText = p.InsertText;
        InsertPosition = p.InsertPosition;
        DeleteStart = p.DeleteStart;
        DeleteCount = p.DeleteCount;
        RangeRulesSkipExtension = p.RangeRulesSkipExtension;
        _suppressRefresh = false;
        RefreshPreview();
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    /// <summary>The other names in each folder of the batch, or null when they cannot be listed.</summary>
    private async Task<IReadOnlyDictionary<string, HashSet<string>>?> TryListOtherNamesAsync()
    {
        try
        {
            var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in _files.GroupBy(f => FolderKey(f), StringComparer.OrdinalIgnoreCase))
            {
                var parent = folder.First().Path.Parent;
                if (parent is null)
                {
                    continue;
                }

                // Only the batch's files in *this* folder are exempt: a same-named file elsewhere
                // does not free the name here.
                var listing = await _fileSystem.ListAsync(parent);
                var selected = new HashSet<string>(folder.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
                result[folder.Key] = listing.Select(e => e.Name).Where(n => !selected.Contains(n))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            return result;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task LoadOtherNamesAsync()
    {
        var names = await TryListOtherNamesAsync();
        if (names is null)
        {
            return;
        }

        _otherNames = names;
        RefreshPreview();
    }

    /// <summary>Builds the previews for the current settings, or null with <paramref name="error"/> saying why not.</summary>
    private IReadOnlyList<RenamePreviewItem>? BuildPreviews(out string? error)
    {
        error = null;
        RenameTemplate template;
        List<IRenameRule> rules;

        try
        {
            template = BuildTemplate();
        }
        catch (Exception ex)
        {
            error = string.Format(LocalizationManager.Get("MR_TemplateError"), ex.Message);
            return null;
        }

        try
        {
            rules = BuildRules();
        }
        catch (Exception ex)
        {
            error = string.Format(LocalizationManager.Get("MR_RuleError"), ex.Message);
            return null;
        }

        var otherNames = _otherNames;
        try
        {
            return _engine.Preview(_files, template, rules, CounterStart, CounterStep,
                referenceDate: _referenceDate, counterWidth: CounterWidth,
                nameTaken: otherNames is null
                    ? null
                    : (file, newName) => otherNames.TryGetValue(FolderKey(file), out var taken) && taken.Contains(newName));
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            error = LocalizationManager.Get("MR_RegexTooSlow");
            return null;
        }
        catch (Exception ex)
        {
            error = string.Format(LocalizationManager.Get("MR_PreviewError"), ex.Message);
            return null;
        }
    }

    private static string FolderKey(IFileEntry file) => file.Path.Parent?.Value ?? string.Empty;

    private void RefreshPreview()
    {
        if (_suppressRefresh)
        {
            return;
        }

        if (_files.Count == 0)
        {
            PreviewItems.Clear();
            StatusText = LocalizationManager.Get("MR_NoFiles");
            return;
        }

        var previews = BuildPreviews(out var error);
        if (previews is null)
        {
            // Keep the last good preview on screen while a half-typed pattern is invalid.
            StatusText = error!;
            return;
        }

        PreviewItems.Clear();
        var conflicts = 0;
        foreach (var item in previews)
        {
            PreviewItems.Add(new RenamePreviewItemViewModel(item));
            if (item.HasConflict)
            {
                conflicts++;
            }
        }

        StatusText = conflicts > 0
            ? string.Format(LocalizationManager.Get("MR_StatusConflicts"), _files.Count, conflicts)
            : string.Format(LocalizationManager.Get("MR_StatusReady"), _files.Count);
    }

    private RenameTemplate BuildTemplate()
    {
        var text = string.IsNullOrWhiteSpace(TemplateText) ? "[N].[E]" : TemplateText;
        return new RenameTemplate(text);
    }

    /// <summary>
    /// Builds the rule pipeline in a fixed order: regex replace → delete range → insert text →
    /// case change. Positions in the delete/insert rules therefore refer to the name as it looks
    /// after the preceding step.
    /// </summary>
    private List<IRenameRule> BuildRules()
    {
        var rules = new List<IRenameRule>();

        // Regex replace first (runs on the expanded template result). A lone blank is a real pattern:
        // "replace spaces with underscores" is the most common batch rename there is.
        if (!string.IsNullOrEmpty(RegexFind))
        {
            rules.Add(new RegexReplaceRule(RegexFind, RegexReplace, RegexCaseSensitive));
        }

        // Delete a character range, then insert into what is left
        if (DeleteCount > 0 && DeleteStart >= 0)
        {
            rules.Add(ScopeToName(new DeleteRule(DeleteStart, DeleteCount)));
        }

        if (!string.IsNullOrEmpty(InsertText))
        {
            rules.Add(ScopeToName(new InsertRule(InsertText, InsertPosition)));
        }

        // Case change
        if (ApplyUppercase)
        {
            rules.Add(new CaseChangeRule(CaseChangeMode.Uppercase));
        }
        else if (ApplyLowercase)
        {
            rules.Add(new CaseChangeRule(CaseChangeMode.Lowercase));
        }
        else if (ApplyTitleCase)
        {
            rules.Add(new CaseChangeRule(CaseChangeMode.TitleCase));
        }

        return rules;
    }

    private IRenameRule ScopeToName(IRenameRule rule) =>
        RangeRulesSkipExtension ? new NameOnlyRule(rule) : rule;
}

/// <summary>View-model wrapper around <see cref="RenamePreviewItem"/> for DataGrid binding.</summary>
public sealed class RenamePreviewItemViewModel
{
    private readonly RenamePreviewItem _item;

    public RenamePreviewItemViewModel(RenamePreviewItem item) => _item = item;

    public string OldName => _item.OldName;
    public string NewName => _item.NewName;
    public bool HasConflict => _item.HasConflict;

    /// <summary>Why the name cannot be used, in words; empty when it can. Colour alone is not a signal.</summary>
    public string ConflictText => _item.Kind == ConflictKind.None
        ? string.Empty
        : LocalizationManager.Get("MR_Conflict_" + _item.Kind);
}
