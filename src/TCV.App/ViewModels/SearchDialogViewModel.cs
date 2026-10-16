using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Localization;
using TCV.App.Services;
using TCV.Archives;
using TCV.Core.Domain;
using TCV.Core.Interfaces;
using TCV.Core.PluginSdk;
using TCV.Search;

namespace TCV.App.ViewModels;

/// <summary>
/// What the user asked to do with the results they selected. The dialog only raises the request;
/// the mutation itself goes through <c>MainViewModel</c> and the operation queue (INV-10).
/// </summary>
public enum SearchResultAction
{
    /// <summary>Copy the selected hits into the opposite panel.</summary>
    Copy,
    /// <summary>Move the selected hits into the opposite panel.</summary>
    Move,
    /// <summary>Delete the selected hits.</summary>
    Delete,
    /// <summary>Show only the hits in the active panel, as a synthetic listing.</summary>
    FeedToPanel,
}

public partial class SearchDialogViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// Rows the window will hold. A content search for a common word would otherwise add one row per
    /// hit until memory ran out; past the cap the walk stops and the status says so.
    /// </summary>
    public const int DefaultResultCap = 10_000;

    private readonly IVirtualFileSystem _vfs;
    private readonly IArchivePlugin? _archivePlugin;
    private readonly SearchPresetStore _presets;
    private CancellationTokenSource? _searchCts;

    public SearchDialogViewModel(
        IVirtualFileSystem vfs,
        string startPath,
        IArchivePlugin? archivePlugin = null,
        SearchPresetStore? presetStore = null)
    {
        _vfs = vfs;
        _archivePlugin = archivePlugin;
        _presets = presetStore ?? new SearchPresetStore();
        SearchPath = startPath;
        _statusText = LocalizationManager.Get("Search_Ready");
        ReloadPresets();
    }

    /// <summary>
    /// True when the window searches a panel's mount (server, archive) rather than the disk. Hits then
    /// carry paths inside that file system: the operations on hits list them over that mount (6.60),
    /// the look inside archives, which needs disk paths, is declined, and the start says where the
    /// search runs.
    /// </summary>
    public bool SearchesMount
    {
        get => _searchesMount;
        init
        {
            _searchesMount = value;
            if (value)
            {
                StatusText = string.Format(LocalizationManager.Get("Search_ReadyInMount"), _vfs.DisplayName);
            }
        }
    }

    private readonly bool _searchesMount;

    /// <summary>The "inside archives" box applies: there is an archive engine and the hits are disk files.</summary>
    public bool CanSearchInsideArchives => _archivePlugin is not null && !SearchesMount;

    // ── Criteria ──────────────────────────────────────────────────────────────

    [ObservableProperty] private string _searchPath;

    /// <summary>A name without wildcards matches anywhere in the name (<c>config</c> finds <c>app.config.json</c>).</summary>
    [ObservableProperty] private string _namePattern = "*";
    [ObservableProperty] private bool _nameIsRegex;
    [ObservableProperty] private bool _caseSensitive;
    [ObservableProperty] private string _contentText = string.Empty;
    [ObservableProperty] private bool _contentIsRegex;
    [ObservableProperty] private bool _wholeWord;
    [ObservableProperty] private bool _includeHidden;
    [ObservableProperty] private bool _includeSystem;
    [ObservableProperty] private bool _filesOnly;
    [ObservableProperty] private bool _directoriesOnly;
    [ObservableProperty] private bool _searchInsideArchives;
    [ObservableProperty] private string _minSizeText = string.Empty;
    [ObservableProperty] private string _maxSizeText = string.Empty;

    /// <summary>First day a hit may have been modified on, as typed; blank for no bound (<see cref="DateParser"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModifiedAfter))]
    private string _modifiedAfterText = string.Empty;

    /// <summary>Last day a hit may have been modified on, as typed — that whole day included.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModifiedBefore))]
    private string _modifiedBeforeText = string.Empty;

    /// <summary>The "from" day the form holds; null when blank or not a date. Presets load through here.</summary>
    public DateTime? ModifiedAfter
    {
        get => DateParser.TryParse(ModifiedAfterText, out var day) ? day : null;
        set => ModifiedAfterText = DateParser.Format(value);
    }

    /// <summary>The "to" day the form holds (the date, not its end); the criteria extend it to the day's end.</summary>
    public DateTime? ModifiedBefore
    {
        get => DateParser.TryParse(ModifiedBeforeText, out var day) ? day : null;
        set => ModifiedBeforeText = DateParser.Format(value);
    }

    // ── Ripgrep-grade options (6.17) ──────────────────────────────────────────

    /// <summary>Honour <c>.gitignore</c> in the searched tree — on by default, as in ripgrep.</summary>
    [ObservableProperty] private bool _respectGitignore = true;

    /// <summary>Extra exclusions in gitignore grammar: <c>bin/;obj/;*.min.js</c>.</summary>
    [ObservableProperty] private string _excludePatterns = string.Empty;

    /// <summary>Skip files whose head looks binary instead of grepping them.</summary>
    [ObservableProperty] private bool _skipBinary = true;

    /// <summary>Context lines kept on each side of a hit.</summary>
    [ObservableProperty] private int _contextLines;

    [ObservableProperty] private EncodingChoice _selectedEncoding = EncodingChoice.All[0];

    /// <summary>Text encodings offered for content search; the first one auto-detects per file.</summary>
    public IReadOnlyList<EncodingChoice> Encodings => EncodingChoice.All;

    // ── Presets ───────────────────────────────────────────────────────────────

    public ObservableCollection<SearchPreset> Presets { get; } = new();

    [ObservableProperty] private SearchPreset? _selectedPreset;

    // ── State ─────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string _statusText;

    /// <summary>True when <see cref="StatusText"/> reports a mistake in the form, drawn in the danger colour.</summary>
    [ObservableProperty] private bool _hasError;

    [ObservableProperty] private SearchResultViewModel? _selectedResult;

    public ObservableCollection<SearchResultViewModel> Results { get; } = new();

    /// <summary>Rows the grid has selected. Kept in sync by the view — a DataGrid cannot bind
    /// <c>SelectedItems</c> — and read by the result actions.</summary>
    public ObservableCollection<SearchResultViewModel> SelectedResults { get; } = new();

    /// <summary>
    /// Raised when the user asks for an operation over <see cref="SelectedResults"/>. The app
    /// performs it; the dialog stays open so the result list survives the operation.
    /// </summary>
    public event EventHandler<SearchResultActionRequest>? ActionRequested;

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task Start()
    {
        _searchCts?.Cancel();

        // Mistakes in the form are reported before anything runs, in the status line — not as a
        // silently ignored field or a search of a folder that is not there.
        var problem = await ValidateAsync();
        if (problem is not null)
        {
            StatusText = problem;
            HasError = true;
            return;
        }

        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        Results.Clear();
        SelectedResults.Clear();
        IsSearching = true;
        HasError = false;
        StatusText = LocalizationManager.Get("Search_Running");

        var criteria = BuildCriteria();
        criteria.MaxResults = DefaultResultCap;
        var engine = new SearchEngine(_vfs);

        // A progress callback that arrives after the final status must not overwrite it.
        var statusGate = new object();
        var finished = false;
        void Finish(string text)
        {
            lock (statusGate)
            {
                finished = true;
                StatusText = text;
            }
        }

        // Created here, on the UI thread, so its callbacks come back to it.
        engine.Progress = new Progress<SearchProgress>(p =>
        {
            lock (statusGate)
            {
                if (!finished)
                {
                    StatusText = string.Format(LocalizationManager.Get("Search_Progress"), Results.Count, p.Directories, p.Files);
                }
            }
        });

        var count = 0;
        var truncated = false;

        try
        {
            await foreach (var result in engine.SearchAsync(new VfsPath(SearchPath), criteria, ct))
            {
                Results.Add(new SearchResultViewModel(result, inMount: SearchesMount));
                count++;
            }
            truncated = engine.WasTruncated;

            if (!ct.IsCancellationRequested && SearchInsideArchives && CanSearchInsideArchives && !truncated)
            {
                await foreach (var vm in SearchInsideArchivesAsync(criteria, ct))
                {
                    Results.Add(vm);
                    count++;
                    if (count >= DefaultResultCap)
                    {
                        truncated = true;
                        break;
                    }
                }
            }

            Finish(Summarize(ct.IsCancellationRequested ? "Search_Stopped" : "Search_Done", count, truncated, engine));
        }
        catch (OperationCanceledException)
        {
            Finish(Summarize("Search_Stopped", count, truncated, engine));
        }
        catch (ArgumentException ex)
        {
            // A malformed regular expression is the user's typo, not a crash.
            Finish(string.Format(LocalizationManager.Get("Search_RegexError"), ex.Message));
            HasError = true;
        }
        finally
        {
            IsSearching = false;
        }
    }

    private static string Summarize(string key, int count, bool truncated, SearchEngine engine)
    {
        var text = string.Format(LocalizationManager.Get(key), count);
        if (truncated)
        {
            text += LocalizationManager.Get("Search_Truncated");
        }

        if (engine.UnreadableDirectories > 0)
        {
            text += string.Format(LocalizationManager.Get("Search_Unreadable"), engine.UnreadableDirectories);
        }

        if (engine.TimedOutFiles > 0)
        {
            text += string.Format(LocalizationManager.Get("Search_TimedOut"), engine.TimedOutFiles);
        }

        return text;
    }

    [RelayCommand]
    private void Stop() => _searchCts?.Cancel();

    [RelayCommand]
    private void Clear()
    {
        _searchCts?.Cancel();
        Results.Clear();
        SelectedResults.Clear();
        HasError = false;
        StatusText = LocalizationManager.Get("Search_Ready");
    }

    /// <summary>Cancels a running search — called when the window closes, so it does not keep walking the disk unseen.</summary>
    public void Dispose()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
    }

    // ── Validation ────────────────────────────────────────────────────────────

    /// <summary>The first problem with the form, localised, or null when the search may run.</summary>
    private async Task<string?> ValidateAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchPath))
        {
            return string.Format(LocalizationManager.Get("Search_PathMissing"), string.Empty);
        }

        try
        {
            var root = await _vfs.GetEntryAsync(new VfsPath(SearchPath));
            if (root is null || !root.IsDirectory)
            {
                return string.Format(LocalizationManager.Get("Search_PathMissing"), SearchPath);
            }
        }
        catch (Exception)
        {
            return string.Format(LocalizationManager.Get("Search_PathMissing"), SearchPath);
        }

        if (!SizeParser.TryParse(MinSizeText, out var min))
        {
            return string.Format(LocalizationManager.Get("Search_BadSize"), MinSizeText);
        }

        if (!SizeParser.TryParse(MaxSizeText, out var max))
        {
            return string.Format(LocalizationManager.Get("Search_BadSize"), MaxSizeText);
        }

        if (min.HasValue && max.HasValue && min > max)
        {
            return LocalizationManager.Get("Search_SizeRange");
        }

        if (!DateParser.TryParse(ModifiedAfterText, out var from))
        {
            return string.Format(LocalizationManager.Get("Search_BadDate"), ModifiedAfterText);
        }

        if (!DateParser.TryParse(ModifiedBeforeText, out var to))
        {
            return string.Format(LocalizationManager.Get("Search_BadDate"), ModifiedBeforeText);
        }

        if (from.HasValue && to.HasValue && from > to)
        {
            return LocalizationManager.Get("Search_DateRange");
        }

        return null;
    }

    // ── Result actions (operations straight from the hit list) ────────────────

    [RelayCommand] private void CopyResults() => RequestAction(SearchResultAction.Copy);
    [RelayCommand] private void MoveResults() => RequestAction(SearchResultAction.Move);
    [RelayCommand] private void DeleteResults() => RequestAction(SearchResultAction.Delete);
    [RelayCommand] private void FeedResultsToPanel() => RequestAction(SearchResultAction.FeedToPanel);

    private void RequestAction(SearchResultAction action)
    {
        // The operations feed the hits to the panel as disk files; a server's or an archive's paths
        // are not that, and would be looked up on the disk.
        var chosen = ActionTargets();
        if (chosen.Count == 0)
        {
            StatusText = LocalizationManager.Get("Search_NothingSelected");
            return;
        }

        ActionRequested?.Invoke(this, new SearchResultActionRequest(action, chosen));
    }

    /// <summary>
    /// Drops the rows whose file no longer exists — after a move or delete started from this window
    /// the list would otherwise keep offering files that are gone.
    /// </summary>
    public async Task PruneMissingAsync()
    {
        foreach (var row in Results.Where(r => r.ArchivePath is null).ToList())
        {
            IFileEntry? still;
            try { still = await _vfs.GetEntryAsync(new VfsPath(row.FullPath)); }
            catch (Exception) { still = null; }

            if (still is null)
            {
                Results.Remove(row);
                SelectedResults.Remove(row);
            }
        }
    }

    /// <summary>
    /// Rows an action applies to: the multi-selection, or the focused row when nothing is
    /// explicitly selected. Hits inside archives are excluded — the operation queue works on real
    /// paths, and a path like <c>a.zip!/x</c> is not one (TRAP-02).
    /// </summary>
    private IReadOnlyList<SearchResultViewModel> ActionTargets()
    {
        var source = SelectedResults.Count > 0
            ? SelectedResults.ToList()
            : SelectedResult is { } one ? [one] : new List<SearchResultViewModel>();

        // One row per file: a content search reports every hit, and copying the same file five
        // times because it matched five times is never what the user meant.
        return source
            .Where(r => r.ArchivePath is null)
            .GroupBy(r => r.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    // ── Presets ───────────────────────────────────────────────────────────────

    private void ReloadPresets()
    {
        Presets.Clear();
        foreach (var p in _presets.LoadAll())
        {
            Presets.Add(p);
        }
    }

    partial void OnSelectedPresetChanged(SearchPreset? value)
    {
        if (value is null) return;
        ApplyPreset(value);
    }

    /// <summary>Loads a preset's settings into the form. Public so tests can drive it without a window.</summary>
    public void ApplyPreset(SearchPreset p)
    {
        NamePattern = p.NamePattern;
        NameIsRegex = p.NameIsRegex;
        CaseSensitive = p.CaseSensitive;
        ContentText = p.ContentText;
        ContentIsRegex = p.ContentIsRegex;
        WholeWord = p.WholeWord;
        SkipBinary = p.SkipBinary;
        ContextLines = p.ContextLines;
        ExcludePatterns = p.ExcludePatterns;
        RespectGitignore = p.RespectGitignore;
        IncludeHidden = p.IncludeHidden;
        IncludeSystem = p.IncludeSystem;
        FilesOnly = p.FilesOnly;
        DirectoriesOnly = p.DirectoriesOnly;
        SearchInsideArchives = p.SearchInsideArchives;
        MinSizeText = p.MinSizeBytes > 0 ? p.MinSizeBytes.ToString(CultureInfo.InvariantCulture) : string.Empty;
        MaxSizeText = p.MaxSizeBytes > 0 ? p.MaxSizeBytes.ToString(CultureInfo.InvariantCulture) : string.Empty;
        ModifiedAfter = p.ModifiedAfter;
        ModifiedBefore = p.ModifiedBefore;
        SelectedEncoding = EncodingChoice.All.FirstOrDefault(e => e.CodePage == p.ContentCodePage)
                           ?? EncodingChoice.All[0];
    }

    /// <summary>Captures the current form as a preset under <paramref name="name"/> and saves it.</summary>
    public bool SavePreset(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var preset = SearchPreset.FromCriteria(name.Trim(), BuildCriteria(), SearchInsideArchives);
        var saved = _presets.Save(preset);
        ReloadPresets();
        SelectedPreset = Presets.FirstOrDefault(p =>
            string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
        return saved;
    }

    /// <summary>Deletes the selected preset, if any.</summary>
    public bool DeleteSelectedPreset()
    {
        if (SelectedPreset is not { } preset)
        {
            return false;
        }

        var deleted = _presets.Delete(preset.Name);
        ReloadPresets();
        return deleted;
    }

    // ── Archive search ────────────────────────────────────────────────────────

    private static readonly string[] ArchiveExtensions =
        [".zip", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".tbz2", ".rar", ".cab", ".iso"];

    /// <summary>
    /// Finds the archives under the search root with the same walk as the search itself (so ignored
    /// folders stay ignored and an unreadable one is skipped, not fatal), then looks inside each. All
    /// of it — the walk, <c>ListAll</c> (SharpCompress or 7z.exe) and the reads — runs off the UI thread.
    /// </summary>
    private async IAsyncEnumerable<SearchResultViewModel> SearchInsideArchivesAsync(
        SearchCriteria criteria,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var walkCriteria = CloneStructure(criteria);
        var walker = new SearchEngine(_vfs);
        var archives = walker.EnumerateFilesAsync(
            new VfsPath(SearchPath), walkCriteria,
            entry => ArchiveExtensions.Contains(Path.GetExtension(entry.Name), StringComparer.OrdinalIgnoreCase),
            ct);

        await foreach (var archive in archives.WithCancellation(ct))
        {
            var windowsPath = archive.Path.Value.Replace('/', Path.DirectorySeparatorChar);
            var batch = await Task.Run(
                () => CollectArchiveResultsAsync(windowsPath, criteria, ct, _archivePlugin), ct);
            foreach (var vm in batch)
            {
                yield return vm;
            }
        }
    }

    private static async Task<List<SearchResultViewModel>> CollectArchiveResultsAsync(
        string archivePath, SearchCriteria criteria, CancellationToken ct, IArchivePlugin? archivePlugin)
    {
        var results = new List<SearchResultViewModel>();
        IVirtualFileSystem? archiveFs = null;
        try
        {
            if (archivePlugin is null)
            {
                return results;
            }

            var format = archivePlugin.DetectFormat(archivePath);
            if (format is null)
            {
                return results;
            }

            var engine = archivePlugin.EngineFactory.Create(format);
            var entries = engine.ListAll(archivePath);
            archiveFs = new ArchiveFileSystem(archivePath, engine, entries);
            var archiveSearch = new SearchEngine(archiveFs);
            // An archive carries no .gitignore worth honouring, and its own copy of one would
            // hide exactly the files the user unpacked it to find.
            var archiveCriteria = CloneWithoutGitignore(criteria);
            await foreach (var result in archiveSearch.SearchAsync(new VfsPath("/"), archiveCriteria, ct).ConfigureAwait(false))
            {
                results.Add(new SearchResultViewModel(result, archivePath));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* skip unreadable archives */ }
        finally { archiveFs?.Dispose(); }
        return results;
    }

    /// <summary>The walk-shaping part of the criteria only: no name, size, date or content filters.</summary>
    private static SearchCriteria CloneStructure(SearchCriteria c) => new()
    {
        RespectGitignore = c.RespectGitignore,
        ExcludePatterns = c.ExcludePatterns,
        IncludeHidden = c.IncludeHidden,
        IncludeSystem = c.IncludeSystem,
        MaxDepth = c.MaxDepth,
        SkipDirectories = c.SkipDirectories,
    };

    private static SearchCriteria CloneWithoutGitignore(SearchCriteria c) => new()
    {
        NamePattern = c.NamePattern,
        NameIsRegex = c.NameIsRegex,
        CaseSensitive = c.CaseSensitive,
        ContentText = c.ContentText,
        ContentIsRegex = c.ContentIsRegex,
        WholeWord = c.WholeWord,
        ContentCodePage = c.ContentCodePage,
        SkipBinary = c.SkipBinary,
        ContextLines = c.ContextLines,
        ExcludePatterns = c.ExcludePatterns,
        RespectGitignore = false,
        IncludeHidden = c.IncludeHidden,
        IncludeSystem = c.IncludeSystem,
        FilesOnly = c.FilesOnly,
        DirectoriesOnly = c.DirectoriesOnly,
        MinSizeBytes = c.MinSizeBytes,
        MaxSizeBytes = c.MaxSizeBytes,
        ModifiedAfter = c.ModifiedAfter,
        ModifiedBefore = c.ModifiedBefore,
        MaxResults = c.MaxResults,
    };

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Builds the criteria described by the form. Public so presets and tests can read it.</summary>
    public SearchCriteria BuildCriteria()
    {
        var c = new SearchCriteria
        {
            NamePattern = string.IsNullOrWhiteSpace(NamePattern) ? null : NamePattern,
            NameIsRegex = NameIsRegex,
            CaseSensitive = CaseSensitive,
            // A blank is something to search for (a double space, a tab); only an empty box means "no content filter".
            ContentText = string.IsNullOrEmpty(ContentText) ? null : ContentText,
            ContentIsRegex = ContentIsRegex,
            WholeWord = WholeWord,
            ContentCodePage = SelectedEncoding.CodePage,
            SkipBinary = SkipBinary,
            ContextLines = ContextLines,
            ExcludePatterns = string.IsNullOrWhiteSpace(ExcludePatterns) ? null : ExcludePatterns,
            RespectGitignore = RespectGitignore,
            IncludeHidden = IncludeHidden,
            IncludeSystem = IncludeSystem,
            FilesOnly = FilesOnly,
            DirectoriesOnly = DirectoriesOnly,
            // Whole days: from the first instant of "from" to the last instant of "to".
            ModifiedAfter = ModifiedAfter,
            ModifiedBefore = DateParser.EndOfDay(ModifiedBefore),
        };

        if (SizeParser.TryParse(MinSizeText, out var minSz))
        {
            c.MinSizeBytes = minSz;
        }

        if (SizeParser.TryParse(MaxSizeText, out var maxSz))
        {
            c.MaxSizeBytes = maxSz;
        }

        return c;
    }
}

/// <summary>
/// Parses a size typed in the search form: a plain number of bytes, or a number with a unit —
/// <c>10MB</c>, <c>1.5 GB</c>, <c>512k</c>. Blank means "no bound". Anything else is an error the form
/// reports, not a value that is quietly ignored.
/// </summary>
public static class SizeParser
{
    /// <summary>True when the text is blank (<paramref name="bytes"/> null) or a valid size.</summary>
    public static bool TryParse(string? text, out long? bytes)
    {
        bytes = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var s = text.Trim().Replace(",", string.Empty).Replace(" ", string.Empty);
        var multiplier = 1L;

        foreach (var (suffix, factor) in Units)
        {
            if (!s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            s = s[..^suffix.Length];
            multiplier = factor;
            break;
        }

        // '.' only: a comma has already been taken as a thousands separator above.
        if (!double.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)
            || number < 0 || double.IsNaN(number) || double.IsInfinity(number))
        {
            return false;
        }

        var total = number * multiplier;
        if (total > long.MaxValue)
        {
            return false;
        }

        bytes = (long)total;
        return true;
    }

    // Longest suffix first: "KB" before "B".
    private static readonly (string Suffix, long Factor)[] Units =
    [
        ("TB", 1L << 40), ("GB", 1L << 30), ("MB", 1L << 20), ("KB", 1L << 10),
        ("T", 1L << 40), ("G", 1L << 30), ("M", 1L << 20), ("K", 1L << 10),
        ("B", 1L),
    ];
}

/// <summary>An operation the user asked for over a set of search hits.</summary>
public sealed record SearchResultActionRequest(
    SearchResultAction Action, IReadOnlyList<SearchResultViewModel> Results);

/// <summary>A text encoding offered for content search; code page 0 means "detect per file".</summary>
public sealed class EncodingChoice(string name, int codePage)
{
    /// <summary>Shown in the list; the automatic entry follows the UI language.</summary>
    public string Name => CodePage == 0 ? LocalizationManager.Get("Search_EncodingAuto") : name;

    public int CodePage { get; } = codePage;

    public static IReadOnlyList<EncodingChoice> All { get; } =
    [
        new("Auto", 0),
        new("UTF-8", 65001),
        new("UTF-16 LE", 1200),
        new("Windows-1251", 1251),
        new("Windows-1252", 1252),
        new("CP866", 866),
        new("KOI8-R", 20866),
        new("ISO-8859-1", 28591),
    ];

    public override string ToString() => Name;
}

public sealed class SearchResultViewModel
{
    private readonly SearchResult _result;
    private readonly string? _archivePath;
    private readonly bool _inMount;

    /// <param name="result">The hit.</param>
    /// <param name="archivePath">Archive on disk the hit was found in, for the "inside archives" pass.</param>
    /// <param name="inMount">The hit came from a search through a panel's mount: its path is a VFS path.</param>
    public SearchResultViewModel(SearchResult result, string? archivePath = null, bool inMount = false)
    {
        _result = result;
        _archivePath = archivePath;
        _inMount = inMount;
    }

    public string Name => _result.Entry.Name;
    public string FullPath => _archivePath is not null
        ? $"{_archivePath}!{_result.FullPath}"
        : _result.FullPath;

    /// <summary>Archive this hit was found in, or null when it is a plain file on disk.</summary>
    public string? ArchivePath => _archivePath;

    /// <summary>Path of the hit inside its archive (empty for plain files).</summary>
    public string EntryPath => _archivePath is not null ? _result.FullPath : string.Empty;

    // A server's or an archive's folder keeps its forward slashes (TRAP-18): Path.GetDirectoryName
    // would show "/pub/docs" as "\pub\docs".
    public string Directory => _archivePath is not null
        ? _archivePath
        : _inMount
            ? new VfsPath(_result.FullPath).Parent?.Value ?? "/"
            : System.IO.Path.GetDirectoryName(_result.FullPath) ?? string.Empty;
    public long Size => _result.Entry.Size;
    public string SizeText => _result.Entry.IsDirectory ? "<DIR>" : FormatSize(Size);
    public DateTime LastModified => _result.Entry.LastModified;
    public int? MatchLine => _result.MatchLineNumber;

    /// <summary>The matched line, trimmed for the grid; the untrimmed one lives in the tooltip.</summary>
    public string? MatchText => _result.MatchLineText?.Trim();

    /// <summary>Hit line plus its context lines — what the row's tooltip shows.</summary>
    public string? MatchContext
    {
        get
        {
            if (_result.MatchLineText is null)
            {
                return null;
            }

            var before = _result.ContextBefore ?? Array.Empty<string>();
            var after = _result.ContextAfter ?? Array.Empty<string>();
            if (before.Count == 0 && after.Count == 0)
            {
                return _result.MatchLineText;
            }

            return string.Join(Environment.NewLine, before.Concat([_result.MatchLineText]).Concat(after));
        }
    }

    public bool IsDirectory => _result.Entry.IsDirectory;
    public IFileEntry Entry => _result.Entry;

    private static string FormatSize(long bytes) => TCV.App.Converters.FileSizeConverter.Format(bytes);
}
