using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.Ai.Semantic;
using TCV.App.Localization;

namespace TCV.App.ViewModels.Dialogs;

/// <summary>What the semantic search window was closed for.</summary>
public enum SemanticSearchAction
{
    None,

    /// <summary>Jump the active panel to <see cref="SemanticSearchDialogViewModel.ChosenPath"/>.</summary>
    GoToFile,

    /// <summary>Feed every hit into the panel as a result listing.</summary>
    ShowAllInPanel,
}

/// <summary>
/// Local semantic search (6.16): ask in words, get the chunks that mean it.
/// <para>
/// This <em>complements</em> exact search rather than replacing it, which is why it is a separate
/// window from Ctrl+F rather than a checkbox inside it. The two answer different questions — ripgrep
/// finds <c>RejectIfMountedAsync</c>, this finds «where do we decide a panel is remote» — and merging
/// them into one box would mean one of the two silently becoming the worse tool for its own job.
/// </para>
/// <para>
/// The index is local and built on demand (ADR-0008): nothing is embedded until the user presses
/// «Index», and the vectors never leave the machine. The <em>text</em> of the files does go to the
/// embedding endpoint to be embedded, so indexing asks first when that endpoint is not this machine.
/// </para>
/// </summary>
public partial class SemanticSearchDialogViewModel : ObservableObject
{
    private readonly SemanticIndexService _semantic;
    private readonly string _root;

    /// <summary>
    /// Enumerates the files to index — gitignore-aware, supplied by the caller. <c>Complete</c> is false
    /// when the walk hit its entry cap, in which case files past the cap must not be treated as deleted.
    /// </summary>
    private readonly Func<CancellationToken, Task<(IReadOnlyList<string> Files, bool Complete)>> _collectFiles;

    /// <summary>
    /// Asked once per endpoint before file contents are sent to it (the argument is the file count).
    /// Null means no question — a caller with no window, or an endpoint on this machine.
    /// </summary>
    private readonly Func<int, Task<bool>>? _confirmUpload;

    /// <summary>Asks whether to wipe the index. Null wipes without asking (tests).</summary>
    private readonly Func<Task<bool>>? _confirmClear;

    private CancellationTokenSource? _running;

    public SemanticSearchDialogViewModel(
        SemanticIndexService semantic,
        string root,
        Func<CancellationToken, Task<(IReadOnlyList<string> Files, bool Complete)>> collectFiles,
        Func<int, Task<bool>>? confirmUpload = null,
        Func<Task<bool>>? confirmClear = null)
    {
        _semantic = semantic;
        _root = root;
        _collectFiles = collectFiles;
        _confirmUpload = confirmUpload;
        _confirmClear = confirmClear;

        WindowTitle = LocalizationManager.Get("Semantic_Title");

        RefreshIndexState();

        // A disabled Search button with no word about why looks broken. Without an embedding model the
        // reason is the first thing the window says.
        if (!_semantic.IsConfigured)
            StatusMessage = LocalizationManager.Get("Llm_NotConfiguredEmbeddings");
    }

    public string WindowTitle { get; }

    public string Root => _root;

    public ObservableCollection<SemanticHit> Hits { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSearch))]
    private string _query = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoToFile))]
    private SemanticHit? _selected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSearch))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>Index size, so «no hits» can be told apart from «nothing indexed».</summary>
    [ObservableProperty]
    private string _indexState = string.Empty;

    public bool IsIdle => !IsBusy;

    public bool CanSearch => !IsBusy && Query.Trim().Length > 0 && _semantic.IsConfigured;

    public bool CanGoToFile => Selected is not null;

    public bool HasHits => Hits.Count > 0;

    public SemanticSearchAction Action { get; private set; } = SemanticSearchAction.None;
    public string ChosenPath { get; private set; } = string.Empty;

    /// <summary>Every hit's path, deduplicated — what «show all in panel» feeds the panel.</summary>
    public IReadOnlyList<string> HitPaths =>
        Hits.Select(h => h.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public bool DialogResult { get; private set; }
    public event EventHandler? CloseRequested;

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (!CanSearch) return;

        IsBusy = true;
        StatusMessage = LocalizationManager.Get("Semantic_Searching");
        try
        {
            // Off the dispatcher (INV-06): the embedding call is HTTP, and the store then scores
            // every vector in the index synchronously.
            var query = Query.Trim();
            var hits = await Task.Run(() => _semantic.SearchAsync(query, _root, 100));

            Hits.Clear();
            foreach (var hit in hits) Hits.Add(hit);

            // An index that cannot be searched (built by another model, extension missing) is not
            // «nothing found», and the two need different next steps.
            StatusMessage = hits.Count > 0
                ? LocalizationManager.Format("Semantic_Found", hits.Count)
                : _semantic.Store.UnavailableReason is { Length: > 0 } reason
                    ? reason
                    : LocalizationManager.Get("Semantic_NoHits");

            Selected = Hits.FirstOrDefault();
            OnPropertyChanged(nameof(HasHits));
        }
        finally
        {
            IsBusy = false;
            RefreshIndexState();
        }
    }

    /// <summary>
    /// Builds or refreshes the index for this root. Files already embedded at their current
    /// modification time are skipped, so re-running it after editing a handful of files is cheap.
    /// </summary>
    [RelayCommand]
    private async Task IndexAsync()
    {
        if (IsBusy) return;

        if (!_semantic.IsConfigured)
        {
            StatusMessage = LocalizationManager.Get("Llm_NotConfiguredEmbeddings");
            return;
        }

        _running = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            var ct = _running.Token;

            // Both halves run off the dispatcher (INV-06). Neither awaits anything that resumes on a
            // pool thread by itself: the walk is file-system work and the index pass reads files,
            // chunks them and writes SQLite between its HTTP calls, so on the UI thread the whole
            // pass — up to 20 000 files — would freeze the window, Stop button included. Progress is
            // constructed here, so its reports still come back to the dispatcher.
            StatusMessage = LocalizationManager.Get("Semantic_Collecting");
            var (files, complete) = await Task.Run(() => _collectFiles(ct), ct);

            // The chunk text of every file is about to leave for the embedding endpoint. That is the
            // one thing «the index stays on this machine» does not cover, so it is asked about once.
            if (_confirmUpload is not null && !await _confirmUpload(files.Count))
            {
                StatusMessage = LocalizationManager.Get("Semantic_NotConfirmed");
                return;
            }

            var progress = new Progress<SemanticIndexProgress>(p =>
                StatusMessage = LocalizationManager.Format("Semantic_Indexing", p.FilesSeen, files.Count, p.ChunksEmbedded));

            var result = await Task.Run(
                () => _semantic.IndexAsync(_root, files, progress, ct, filesAreComplete: complete), ct);

            StatusMessage = result.Success
                ? LocalizationManager.Format("Semantic_Indexed", result.FilesIndexed, result.ChunksEmbedded, result.FilesSkipped)
                  + (result.Message.Length > 0 ? " " + result.Message : string.Empty)
                : Agents.LlmErrorText.Localize(result.Message);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = LocalizationManager.Get("Semantic_Cancelled");
        }
        finally
        {
            IsBusy = false;
            _running?.Dispose();
            _running = null;
            RefreshIndexState();
        }
    }

    [RelayCommand]
    private void CancelIndexing() => _running?.Cancel();

    /// <summary>
    /// Empties the index. It is derived data — rebuilt by «Index» — but rebuilding costs embedding
    /// calls, so it is confirmed rather than done on a stray click.
    /// </summary>
    [RelayCommand]
    private async Task ClearIndexAsync()
    {
        if (IsBusy) return;
        if (_confirmClear is not null && !await _confirmClear()) return;

        await Task.Run(() => _semantic.Store.Clear());

        Hits.Clear();
        OnPropertyChanged(nameof(HasHits));
        StatusMessage = LocalizationManager.Get("Semantic_Cleared");
        RefreshIndexState();
    }

    [RelayCommand]
    private void GoToFile()
    {
        if (Selected is null) return;

        Action = SemanticSearchAction.GoToFile;
        ChosenPath = Selected.Path.Replace('/', '\\');
        Close(true);
    }

    [RelayCommand]
    private void ShowAllInPanel()
    {
        if (Hits.Count == 0) return;

        Action = SemanticSearchAction.ShowAllInPanel;
        Close(true);
    }

    [RelayCommand]
    private void Close() => Close(false);

    private void RefreshIndexState()
    {
        var store = _semantic.Store;
        if (!store.IsOpen)
        {
            // Not opened yet: the width is unknown until the model has been asked once, so the state
            // line stays honest about that rather than reporting a count of zero.
            IndexState = LocalizationManager.Get("Semantic_NotOpened");
            return;
        }

        IndexState = LocalizationManager.Format("Semantic_State", store.FileCount(), store.ChunkCount());
    }

    private void Close(bool result)
    {
        DialogResult = result;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
