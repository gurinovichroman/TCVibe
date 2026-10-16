using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.Ai.Llm;
using TCV.Ai.Rename;
using TCV.App.Localization;

namespace TCV.App.ViewModels.Dialogs;

/// <summary>One editable preview row. The proposal is a suggestion; the user owns the final name.</summary>
public partial class AiRenameRow : ObservableObject
{
    public AiRenameRow(AiRenameItem item)
    {
        Original = item.Original;
        _proposed = item.Proposed;
        Note = Agents.AiRenameNoteText.Localize(item.Note);

        // A row the model's answer had to be corrected for (a folder path dropped, a suffix added…)
        // is offered but not pre-checked: it is no longer quite what the model meant.
        NeedsReview = item.NeedsReview;
        _apply = item.Changed && !item.NeedsReview;
    }

    public string Original { get; }

    /// <summary>Editable: a nearly-right name is faster to fix than to retype.</summary>
    [ObservableProperty]
    private string _proposed;

    public string Note { get; }

    /// <summary>True when the proposal was corrected on the way in and starts unchecked.</summary>
    public bool NeedsReview { get; }

    /// <summary>Per-row opt-in.</summary>
    [ObservableProperty]
    private bool _apply;

    /// <summary>Why the typed name cannot be used; empty when it can.</summary>
    [ObservableProperty]
    private string _problem = string.Empty;

    public bool IsChanged => !string.Equals(Original, Proposed, StringComparison.Ordinal);

    /// <summary>The note column: a validation problem outranks the model's own remark.</summary>
    public string NoteText => Problem.Length > 0 ? Problem : Note;

    partial void OnProblemChanged(string value) => OnPropertyChanged(nameof(NoteText));
}

/// <summary>
/// Content-aware rename (6.15), preview-first.
/// <para>
/// Nothing is renamed until the table has been read and the button pressed — required by ADR-0008 and
/// INV-11, and the reason the dialog is shaped as a table rather than a progress bar. The scope guard
/// runs before the model is ever called: this feature is for the junk corner, and
/// <see cref="JunkCornerGuard"/> refuses a source tree outright. The applied batch can be undone from
/// this window, and after it is closed from the operation log, whose line records the renames
/// (ADR-0008: preview before, undo after; 6.55).
/// </para>
/// </summary>
public partial class AiRenameDialogViewModel : ObservableObject
{
    private readonly LlmClient _llm;
    private readonly IReadOnlyList<(string Name, string Excerpt)> _files;
    private readonly IReadOnlyCollection<string> _directoryNames;
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>Performs the accepted renames; returns how many succeeded and a message on failure.</summary>
    private readonly Func<IReadOnlyList<(string From, string To)>, Task<(int Renamed, string Error)>> _apply;

    /// <summary>Takes the last applied batch back; returns how many renames could not be restored.</summary>
    private readonly Func<Task<int>> _undo;

    /// <param name="directoryNames">Every name in the folder, so a proposal cannot land on a file that
    /// is not part of the selection.</param>
    public AiRenameDialogViewModel(
        LlmClient llm,
        IReadOnlyList<(string Name, string Excerpt)> files,
        IReadOnlyCollection<string> directoryNames,
        Func<IReadOnlyList<(string From, string To)>, Task<(int Renamed, string Error)>> apply,
        Func<Task<int>> undo)
    {
        _llm = llm;
        _files = files;
        _directoryNames = directoryNames;
        _apply = apply;
        _undo = undo;

        WindowTitle = LocalizationManager.Get("AiRename_Title");

        foreach (var (name, _) in files)
            Rows.Add(new AiRenameRow(new AiRenameItem(name, name, string.Empty)));
    }

    public string WindowTitle { get; }

    public ObservableCollection<AiRenameRow> Rows { get; } = new();

    /// <summary>Where the file contents go — shown next to the explanation, because they leave the machine.</summary>
    public string Destination
    {
        get
        {
            var options = _llm.Options;
            return options.IsLocalEndpoint
                ? LocalizationManager.Get("AiRename_SentLocal")
                : string.Format(LocalizationManager.Get("AiRename_SentRemote"), options.EndpointHost);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyPropertyChangedFor(nameof(CanUndo))]
    [NotifyPropertyChangedFor(nameof(CanPropose))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusMessage = string.Empty;

    public bool HasStatus => StatusMessage.Length > 0;

    /// <summary>
    /// What the caller left out of the batch (code files, files past <c>AiRenamePlan.MaxBatch</c>). Kept
    /// apart from <see cref="StatusMessage"/>, which every proposal overwrites.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string _notice = string.Empty;

    public bool HasNotice => Notice.Length > 0;

    /// <summary>True once a proposal has come back, so the apply button is not offered before that.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    private bool _hasProposal;

    /// <summary>True after a batch was applied: the table then describes the past, and stays read-only.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyPropertyChangedFor(nameof(CanUndo))]
    [NotifyPropertyChangedFor(nameof(CanPropose))]
    [NotifyPropertyChangedFor(nameof(IsEditable))]
    private bool _isApplied;

    public bool IsEditable => !IsApplied;

    public bool CanApply => HasProposal && !IsBusy && !IsApplied;

    public bool CanUndo => IsApplied && RenamedCount > 0 && !IsBusy;

    public bool CanPropose => !IsBusy && !IsApplied;

    /// <summary>How many files carry a new name right now (0 after an undo).</summary>
    public int RenamedCount { get; private set; }

    public bool DialogResult { get; private set; }
    public event EventHandler? CloseRequested;

    /// <summary>Asks the model for names. Run from the dialog's Loaded, and repeatable.</summary>
    [RelayCommand]
    public async Task ProposeAsync()
    {
        if (!_llm.IsConfigured)
        {
            StatusMessage = LocalizationManager.Get("Llm_NotConfigured");
            return;
        }

        IsBusy = true;
        StatusMessage = LocalizationManager.Get("AiRename_Working");
        try
        {
            var messages = AiRenamePlan.BuildPrompt(_files);
            var result = await _llm.CompleteAsync(messages, _lifetime.Token);

            if (!result.Success)
            {
                StatusMessage = Agents.LlmErrorText.Localize(result.Error);
                return;
            }

            var names = _files.Select(f => f.Name).ToList();
            var items = AiRenamePlan.Parse(result.Text, names, _directoryNames.Except(names, StringComparer.OrdinalIgnoreCase));

            Rows.Clear();
            foreach (var item in items) Rows.Add(new AiRenameRow(item));

            var changed = items.Count(i => i.Changed);
            var review = items.Count(i => i.NeedsReview);
            HasProposal = changed > 0;
            StatusMessage = changed == 0
                ? LocalizationManager.Get("AiRename_NoChanges")
                : string.Format(LocalizationManager.Get("AiRename_Proposed"), changed, items.Count)
                  + (review > 0 ? " " + LocalizationManager.Format("AiRename_NeedReview", review) : string.Empty);
        }
        catch (OperationCanceledException)
        {
            // The window was closed while the model was thinking; nothing left to show it to.
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Applies the checked rows. Rows whose name was not actually changed are skipped.</summary>
    [RelayCommand]
    private async Task ApplyAsync()
    {
        foreach (var row in Rows) row.Proposed = row.Proposed.Trim();

        var chosen = Rows.Where(r => r.Apply && r.IsChanged && r.Proposed.Length > 0).ToList();
        var pairs = chosen.Select(r => (From: r.Original, To: r.Proposed)).ToList();

        if (pairs.Count == 0)
        {
            StatusMessage = LocalizationManager.Get("AiRename_NothingChecked");
            return;
        }

        // A name typed into the table skipped the model-output sanitising, and a separator or a drive
        // letter in it would move the file out of its folder. Checked here, and refused with the reason
        // on the row rather than quietly altered.
        var problems = AiRenamePlan.ValidateBatch(pairs, _directoryNames);
        foreach (var row in Rows) row.Problem = string.Empty;
        foreach (var row in chosen)
        {
            if (problems.TryGetValue(row.Original, out var problem))
                row.Problem = ProblemText(problem);
        }

        if (problems.Count > 0)
        {
            StatusMessage = LocalizationManager.Format("AiRename_FixProblems", problems.Count);
            return;
        }

        IsBusy = true;
        try
        {
            var (renamed, error) = await _apply(pairs);
            RenamedCount = renamed;

            if (renamed > 0) IsApplied = true;
            StatusMessage = error.Length > 0
                ? error
                : LocalizationManager.Format("AiRename_Done", renamed);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanUndo));
        }
    }

    /// <summary>Takes the applied batch back — the «undo after» half of ADR-0008.</summary>
    [RelayCommand]
    private async Task UndoAsync()
    {
        if (!CanUndo) return;

        IsBusy = true;
        try
        {
            var failed = await _undo();
            RenamedCount = failed;

            StatusMessage = failed == 0
                ? LocalizationManager.Get("AiRename_Undone")
                : LocalizationManager.Format("AiRename_UndonePartly", failed);

            IsApplied = failed > 0;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanUndo));
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _lifetime.Cancel();
        DialogResult = RenamedCount > 0;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Withdraws a model request still in flight when the window goes away by any route.</summary>
    public void Abandon() => _lifetime.Cancel();

    private static string ProblemText(AiRenameProblem problem) => LocalizationManager.Get(problem switch
    {
        AiRenameProblem.Empty => "AiRename_ProblemEmpty",
        AiRenameProblem.InvalidCharacters => "AiRename_ProblemChars",
        AiRenameProblem.TrailingDotOrSpace => "AiRename_ProblemTrailing",
        AiRenameProblem.ReservedName => "AiRename_ProblemReserved",
        AiRenameProblem.TooLong => "AiRename_ProblemLong",
        AiRenameProblem.DuplicateInBatch => "AiRename_ProblemDuplicate",
        _ => "AiRename_ProblemExists",
    });
}
