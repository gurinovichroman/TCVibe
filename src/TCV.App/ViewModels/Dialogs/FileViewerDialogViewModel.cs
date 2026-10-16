using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Localization;
using TCV.App.Viewer;

namespace TCV.App.ViewModels.Dialogs;

/// <summary>
/// Backs the internal file viewer (F3 Lister). Works on one in-memory <em>window</em> of bytes the
/// caller already fetched through the panel's <c>IVirtualFileSystem</c>, so the same window views files
/// on the local disk, inside an archive, or on an FTP/SFTP host — nothing is copied but an archive entry,
/// which is unpacked once for the window (<see cref="ExtractedViewerSource"/>).
/// </summary>
/// <remarks>
/// A file bigger than the window is still fully reachable when a <see cref="IViewerSource"/> is given:
/// the window can be moved to the start, the end (the tail of a log), the next or previous block, or a
/// typed offset. Only one window is ever in memory, so a multi-gigabyte file costs the same as a small one.
/// With <see cref="ViewerSiblings"/> the same window steps on to the panel's next or previous file (N/P).
/// </remarks>
public sealed partial class FileViewerDialogViewModel : ObservableObject, IDisposable
{
    // The displayed content is capped independently of the fetched buffer: a plain read-only
    // TextBox degrades badly past a few MB / tens of thousands of lines.
    private const int TextByteCap = 4 * 1024 * 1024;
    private const int HexByteCap = 512 * 1024;

    /// <summary>Size of the block the window moves by, and the most a text view shows at once.</summary>
    public const int WindowBytes = TextByteCap;

    private IViewerSource? _source;
    private byte[] _data;
    private long _windowOffset;
    private long _fullSize;
    private readonly ViewerSiblings? _siblings;
    // Set while N/P replaces the file, so resetting the encoding does not render the new file twice.
    private bool _switchingFile;
    // Auto-encoding detection scans the window, so compute it at most once per window and reuse it
    // across every re-render / status update instead of re-scanning on each mode change.
    private Lazy<Encoding> _autoEncoding;
    // Image decoding is deferred until Image mode is actually shown — most files opened in F3 are
    // text or binary and would otherwise pay a full-buffer WPF decode attempt for nothing.
    private ImageSource? _image;
    private string? _imageError;
    private bool _imageDecodeAttempted;
    private CancellationTokenSource? _loadCts;

    /// <summary>Optional: without it the «Summarise» button is not offered (6.14, ADR-0007).</summary>
    private readonly TCV.Ai.Llm.LlmClient? _llm;

    public FileViewerDialogViewModel(
        string fileName, byte[] data, bool fetchTruncated, long fullSize,
        TCV.Ai.Llm.LlmClient? llm = null, IViewerSource? source = null, ViewerSiblings? siblings = null)
    {
        _data = data;
        _llm = llm;
        _source = source;
        _siblings = siblings;
        _fullSize = fullSize < 0 ? data.Length : Math.Max(fullSize, data.Length);
        _autoEncoding = NewAutoEncoding();
        Identify(fileName);
        _ = fetchTruncated;   // the window's position against the full size says the same, and says where

        Encodings = new[]
        {
            new EncodingOption(EncodingKind.Auto, null),
            new EncodingOption(EncodingKind.Named, ViewerContent.Utf8, "UTF-8"),
            new EncodingOption(EncodingKind.Named, Encoding.Unicode, "UTF-16 LE"),
            new EncodingOption(EncodingKind.Named, Encoding.BigEndianUnicode, "UTF-16 BE"),
            new EncodingOption(EncodingKind.Named, ViewerContent.GetCodePage(1251), "Windows-1251 (Cyrillic)"),
            new EncodingOption(EncodingKind.Named, ViewerContent.GetCodePage(1252), "Windows-1252 (Western)"),
            new EncodingOption(EncodingKind.Named, ViewerContent.GetCodePage(866), "CP866 (DOS Cyrillic)"),
            new EncodingOption(EncodingKind.Named, ViewerContent.GetCodePage(20866), "KOI8-R"),
            new EncodingOption(EncodingKind.Named, Encoding.Latin1, "ISO-8859-1 (Latin-1)"),
        };
        _selectedEncoding = Encodings[0];

        _mode = ChooseInitialMode(fileName, data);
        Render();
    }

    /// <summary>What the file's name decides: the title, and how its text is highlighted.</summary>
    private void Identify(string fileName)
    {
        FileName = fileName;
        Title = string.Format(LocalizationManager.Get("Viewer_Title"), fileName);
        Language = SyntaxLanguage.ForFile(fileName);
        IsDiffFile = SyntaxLanguage.IsDiff(fileName);
        IsMarkdownFile = SyntaxLanguage.IsMarkdown(fileName);
    }

    private Lazy<Encoding> NewAutoEncoding() => new(() => DetectWindowEncoding(_data));

    /// <summary>Encoding of the window (see <see cref="ViewerContent.DetectWindowEncoding"/>, TRAP-33).</summary>
    public static Encoding DetectWindowEncoding(byte[] window) => ViewerContent.DetectWindowEncoding(window);

    private ViewerMode ChooseInitialMode(string fileName, byte[] data)
    {
        // Only files that look like images pay the decode cost, and only up front so the initial
        // mode can fall back when an "image" turns out to be undecodable (corrupt, or e.g. WebP).
        if (!ViewerContent.IsImage(fileName, data) || !HasWholeFile)
        {
            return ChooseTextualMode(data);
        }

        EnsureImageDecoded();
        // Extension said "image" but nothing decoded it — show the bytes instead of a blank pane.
        if (_image is null)
        {
            return ChooseTextualMode(data);
        }

        return ViewerMode.Image;
    }

    /// <summary>
    /// Which of the text-shaped modes a non-image file opens in. Binary always wins: a
    /// <c>.cs</c> that is actually a compiled blob must not be handed to the highlighter.
    /// </summary>
    private ViewerMode ChooseTextualMode(byte[] data)
    {
        if (ViewerContent.LooksBinary(data))
        {
            return ViewerMode.Hex;
        }

        if (IsMarkdownFile)
        {
            return ViewerMode.Markdown;
        }

        if (CanShowCode)
        {
            return ViewerMode.Code;
        }

        return ViewerMode.Text;
    }

    /// <summary>Decodes the buffer as a bitmap on first demand; caches the result (or its error).</summary>
    private void EnsureImageDecoded()
    {
        if (_imageDecodeAttempted)
        {
            return;
        }

        _imageDecodeAttempted = true;
        (_image, _imageError) = HasWholeFile ? TryDecodeImage(_data) : (null, "too large");
        OnPropertyChanged(nameof(ImageSource));
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(HasImageError));
        OnPropertyChanged(nameof(ImageMessage));
    }

    public string FileName { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public IReadOnlyList<EncodingOption> Encodings { get; }

    /// <summary>Grammar the code view highlights with, resolved from the file extension.</summary>
    public SyntaxLanguage Language { get; private set; } = SyntaxLanguage.None;

    /// <summary>True for <c>.diff</c>/<c>.patch</c>: coloured per line rather than lexed.</summary>
    public bool IsDiffFile { get; private set; }

    /// <summary>True for <c>.md</c>: offered as rendered markdown.</summary>
    public bool IsMarkdownFile { get; private set; }

    /// <summary>Whether the code view has anything to add over plain text for this file.</summary>
    public bool CanShowCode => IsDiffFile || !ReferenceEquals(Language, SyntaxLanguage.None);

    /// <summary>Total size of the file, which may be far more than the window holds.</summary>
    public long FullSize => _fullSize;

    /// <summary>Offset of the first byte of the window within the file.</summary>
    public long WindowOffset => _windowOffset;

    /// <summary>True when the window holds the whole file (an image can be decoded, offsets are absolute).</summary>
    public bool HasWholeFile => _windowOffset == 0 && _data.Length >= _fullSize;

    /// <summary>
    /// Line numbers are only meaningful from the start of the file: a window in the middle cannot know
    /// how many lines came before it, and a wrong number is worse than none.
    /// </summary>
    public bool ShowLineNumbers => _windowOffset == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTextMode))]
    [NotifyPropertyChangedFor(nameof(IsHexMode))]
    [NotifyPropertyChangedFor(nameof(IsImageMode))]
    [NotifyPropertyChangedFor(nameof(IsCodeMode))]
    [NotifyPropertyChangedFor(nameof(IsMarkdownMode))]
    [NotifyPropertyChangedFor(nameof(IsRichMode))]
    private ViewerMode _mode;

    [ObservableProperty]
    private EncodingOption _selectedEncoding;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextWrapping))]
    private bool _wordWrap;

    [ObservableProperty]
    private string _textContent = string.Empty;

    [ObservableProperty]
    private string _hexContent = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>Result of the last find (not found, wrapped around), shown beside the status.</summary>
    [ObservableProperty]
    private string _findStatus = string.Empty;

    public ImageSource? ImageSource => _image;
    public bool HasImage => _image is not null;
    public bool HasImageError => _imageError is not null;

    public string ImageMessage => _imageError is null
        ? string.Empty
        : HasWholeFile || _fullSize <= _data.Length
            ? string.Format(LocalizationManager.Get("Viewer_ImageError"), _imageError)
            : string.Format(LocalizationManager.Get("Viewer_ImageTooLarge"), FormatSize(_fullSize));

    public bool IsTextMode => Mode == ViewerMode.Text;
    public bool IsHexMode => Mode == ViewerMode.Hex;
    public bool IsImageMode => Mode == ViewerMode.Image;
    public bool IsCodeMode => Mode == ViewerMode.Code;
    public bool IsMarkdownMode => Mode == ViewerMode.Markdown;

    /// <summary>True in the two flow-document modes, which share one rendering surface.</summary>
    public bool IsRichMode => IsCodeMode || IsMarkdownMode;

    public System.Windows.TextWrapping TextWrapping =>
        WordWrap ? System.Windows.TextWrapping.Wrap : System.Windows.TextWrapping.NoWrap;

    /// <summary>
    /// Raised when the rendered document must be rebuilt: the mode, the encoding, the wrap setting or
    /// the window changed. The flow document lives in the view (it is made of WPF elements), so the
    /// ViewModel signals rather than builds it.
    /// </summary>
    public event EventHandler? DocumentInvalidated;

    [RelayCommand] private void ShowText() => Mode = ViewerMode.Text;
    [RelayCommand] private void ShowHex() => Mode = ViewerMode.Hex;
    [RelayCommand] private void ShowImage() => Mode = ViewerMode.Image;
    [RelayCommand] private void ShowCode() => Mode = ViewerMode.Code;
    [RelayCommand] private void ShowMarkdown() => Mode = ViewerMode.Markdown;

    partial void OnModeChanged(ViewerMode value)
    {
        FindStatus = string.Empty;
        Render();
        NotifyNavigation();
    }

    partial void OnSelectedEncodingChanged(EncodingOption value)
    {
        if (_switchingFile)
        {
            return;   // the new file is rendered once, when it is all in place
        }

        if (Mode is ViewerMode.Text or ViewerMode.Code or ViewerMode.Markdown)
        {
            RenderText();
            if (IsRichMode) DocumentInvalidated?.Invoke(this, EventArgs.Empty);
        }
        UpdateStatus();
    }

    // Wrapping is a property of the flow document's paragraphs, not a binding, so the rendered
    // view has to be rebuilt for the change to show.
    partial void OnWordWrapChanged(bool value)
    {
        if (IsRichMode) DocumentInvalidated?.Invoke(this, EventArgs.Empty);
    }

    // ── Moving through a big file ─────────────────────────────────────────────

    /// <summary>True when the file is bigger than the window and can be read piece by piece.</summary>
    public bool CanNavigate => _source is not null && _fullSize > WindowStep;

    /// <summary>How far one step moves: a hex dump shows less than a text page, so it steps by less.</summary>
    private int WindowStep => Mode == ViewerMode.Hex ? HexByteCap : TextByteCap;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    [NotifyCanExecuteChangedFor(nameof(GoToStartCommand), nameof(GoToEndCommand), nameof(PageBackCommand),
        nameof(PageForwardCommand), nameof(GoToOffsetCommand), nameof(OpenNextFileCommand), nameof(OpenPreviousFileCommand))]
    private bool _isBusy;

    public bool IsNotBusy => !IsBusy;

    /// <summary>
    /// The strip with the block buttons, the busy text and Cancel: for a file bigger than the window, and
    /// while another file (N/P) is slow to open, so that read can be watched and cancelled too.
    /// </summary>
    public bool ShowsNavigationBar => CanNavigate || IsOpeningSlowly;

    /// <summary>Set once a file switch (N/P) has taken a moment; see <see cref="RevealBusyStripAsync"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsNavigationBar))]
    private bool _isOpeningSlowly;

    /// <summary>What the busy strip says while a window is being read.</summary>
    [ObservableProperty]
    private string _busyText = string.Empty;

    /// <summary>The offset the user typed: bytes (<c>4096</c>, <c>0x1000</c>), a percentage, or a size.</summary>
    [ObservableProperty]
    private string _offsetInput = string.Empty;

    private void NotifyNavigation()
    {
        OnPropertyChanged(nameof(CanNavigate));
        OnPropertyChanged(nameof(ShowsNavigationBar));
        GoToStartCommand.NotifyCanExecuteChanged();
        GoToEndCommand.NotifyCanExecuteChanged();
        PageBackCommand.NotifyCanExecuteChanged();
        PageForwardCommand.NotifyCanExecuteChanged();
        GoToOffsetCommand.NotifyCanExecuteChanged();
    }

    private bool CanMove() => CanNavigate && !IsBusy;
    private bool CanMoveBack() => CanMove() && _windowOffset > 0;
    private bool CanMoveForward() => CanMove() && _windowOffset + ShownLength < _fullSize;

    /// <summary>How many bytes of the window the current mode shows.</summary>
    private int ShownLength => Math.Min(_data.Length, WindowStep);

    [RelayCommand(CanExecute = nameof(CanMoveBack))]
    private Task GoToStart() => MoveWindowAsync(0, alignToLine: false);

    /// <summary>The tail: what a log viewer is opened for. Starts on a whole line.</summary>
    [RelayCommand(CanExecute = nameof(CanMove))]
    private Task GoToEnd() => MoveWindowAsync(Math.Max(0, _fullSize - WindowStep), alignToLine: true);

    /// <summary>
    /// The block before, ending where this one starts, so nothing between them is skipped. Hex steps by
    /// exactly what it shows; text starts on a whole line (rounded up to a code unit, never past the join).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMoveBack))]
    private Task PageBack()
    {
        var start = Math.Max(0, _windowOffset - WindowStep);
        if (Mode == ViewerMode.Hex)
        {
            return MoveWindowAsync(start, alignToLine: false, exact: true);
        }

        start += (4 - start % 4) % 4;
        return MoveWindowAsync(start, alignToLine: start > 0);
    }

    /// <summary>
    /// The block after. Text continues from the start of the line the window edge cut through, so that
    /// line is shown whole next time instead of its rest being lost between the two windows.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMoveForward))]
    private Task PageForward() => Mode == ViewerMode.Hex
        ? MoveWindowAsync(_windowOffset + ShownLength, alignToLine: false, exact: true)
        : MoveWindowAsync(
            TextBlockSearch.NextWindowStart(_data, _windowOffset, SelectedEncoding.Encoding ?? _autoEncoding.Value, TextByteCap),
            alignToLine: false, exact: true);

    [RelayCommand(CanExecute = nameof(CanMove))]
    private Task GoToOffset()
    {
        if (!OffsetParser.TryParse(OffsetInput, _fullSize, out var offset))
        {
            FindStatus = string.Format(LocalizationManager.Get("Viewer_BadOffset"), OffsetInput);
            return Task.CompletedTask;
        }

        return MoveWindowAsync(Math.Clamp(offset, 0, Math.Max(0, _fullSize - 1)), alignToLine: offset > 0);
    }

    [RelayCommand]
    private void CancelLoad() => _loadCts?.Cancel();

    /// <summary>
    /// Reads the window at <paramref name="offset"/> and shows it. A window that begins mid-file starts on
    /// a character boundary and, for the tail and paging, on a whole line. Cancelling keeps the old one.
    /// </summary>
    /// <param name="exact"><paramref name="offset"/> is already a boundary (a line start found in this
    /// file's encoding, or a hex step): read from exactly there, without rounding or skipping.</param>
    public async Task MoveWindowAsync(long offset, bool alignToLine, bool exact = false)
    {
        if (_source is null)
        {
            return;
        }

        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        // 4-aligned so a UTF-16/32 window starts on a code unit whatever the encoding turns out to be.
        offset = exact ? Math.Max(0, offset) : Math.Max(0, offset - offset % 4);

        IsBusy = true;
        BusyText = LocalizationManager.Get("Viewer_Reading");
        try
        {
            var progress = new Progress<long>(done =>
                BusyText = string.Format(LocalizationManager.Get("Viewer_ReadingBytes"), FormatSize(done), FormatSize(_fullSize)));

            var bytes = await _source.ReadAsync(offset, TextByteCap, progress, ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            var skip = 0;
            if (offset > 0 && !exact && Mode != ViewerMode.Hex)   // a hex dump shows bytes: no character or line alignment
            {
                var encoding = SelectedEncoding.Encoding ?? DetectWindowEncoding(bytes);
                skip = ViewerContent.LeadingPartialCharacter(bytes, encoding);
                if (alignToLine)
                {
                    skip = Math.Max(skip, ViewerContent.FirstLineStart(bytes, encoding));
                }
            }

            _data = skip == 0 ? bytes : bytes[skip..];
            _windowOffset = offset + skip;
            _autoEncoding = NewAutoEncoding();
            FindStatus = string.Empty;

            OnPropertyChanged(nameof(WindowOffset));
            OnPropertyChanged(nameof(ShowLineNumbers));
            OnPropertyChanged(nameof(HasWholeFile));
            Render();
        }
        catch (OperationCanceledException)
        {
            // superseded or cancelled: the previous window stays
        }
        catch (Exception ex)
        {
            FindStatus = string.Format(LocalizationManager.Get("Viewer_ReadFailed"), ex.Message);
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
            NotifyNavigation();
        }
    }

    /// <summary>True when "find next" can go on past this window into the rest of the file (text modes only).</summary>
    public bool CanFindBeyondWindow => _source is not null && Mode != ViewerMode.Hex && CanMoveForward();

    /// <summary>
    /// Find next past the end of the window: reads the following windows, cut like paging so a line is
    /// never split between two, off the UI thread (INV-06), until one holds <paramref name="query"/> ending
    /// past the text this window shows, and moves there. Returns where in the new window's text to search
    /// on from (the match starts there or after: what came before was on screen already), or -1 when the
    /// rest of the file has no match or the search was cancelled; the window then stays where it was.
    /// </summary>
    public async Task<int> FindBeyondWindowAsync(string query)
    {
        if (!CanFindBeyondWindow || string.IsNullOrEmpty(query))
        {
            return -1;
        }

        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;
        IsBusy = true;
        try
        {
            var source = _source!;
            var window = _data;
            var windowOffset = _windowOffset;
            var length = _fullSize;
            var chosen = SelectedEncoding.Encoding;
            var windowEncoding = chosen ?? _autoEncoding.Value;   // judged already: the window is on screen
            var progress = new Progress<long>(at =>
                BusyText = string.Format(LocalizationManager.Get("Viewer_Searching"), FormatSize(at), FormatSize(length)));

            var found = await Task.Run(() =>
            {
                var (start, seen) = TextBlockSearch.After(window, windowOffset, windowEncoding, TextByteCap, query);
                return TextBlockSearch.FindForwardAsync(source, start, seen, length, query, chosen, TextByteCap, progress, ct);
            }, ct);
            if (found is not { } match)
            {
                return -1;
            }

            await MoveWindowAsync(match.Start, alignToLine: false, exact: true);

            // A match ending past what was seen starts at most a query's length before it.
            return _windowOffset == match.Start ? Math.Max(0, TextIndexAt(match.Seen) - query.Length + 1) : -1;
        }
        catch (OperationCanceledException)
        {
            return -1;
        }
        catch (Exception ex)
        {
            FindStatus = string.Format(LocalizationManager.Get("Viewer_ReadFailed"), ex.Message);
            return -1;
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
            NotifyNavigation();
        }
    }

    /// <summary>True when "find previous" can go on before this window into the earlier part of the file (text modes only).</summary>
    public bool CanFindBeforeWindow => _source is not null && Mode != ViewerMode.Hex && CanMoveBack();

    /// <summary>
    /// Find previous before the start of the window, the mirror of <see cref="FindBeyondWindowAsync"/>:
    /// reads the earlier part of the file block by block, nearest first, off the UI thread (INV-06), and
    /// moves to the window holding the nearest match that starts before this window. Returns where in the new
    /// window's text to search back from (the match ends there or before), or -1 when nothing earlier matches
    /// or the search was cancelled; the window then stays where it was.
    /// </summary>
    public async Task<int> FindBeforeWindowAsync(string query)
    {
        if (!CanFindBeforeWindow || string.IsNullOrEmpty(query))
        {
            return -1;
        }

        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;
        IsBusy = true;
        try
        {
            var source = _source!;
            var end = _windowOffset;
            var encoding = SelectedEncoding.Encoding;
            var progress = new Progress<long>(at =>
                BusyText = string.Format(LocalizationManager.Get("Viewer_Searching"), FormatSize(at), FormatSize(_fullSize)));

            var found = await Task.Run(
                () => TextBlockSearch.FindBackwardAsync(source, end, query, encoding, TextByteCap, progress, ct), ct);
            if (found is not { } block)
            {
                return -1;
            }

            await MoveWindowAsync(block.Start, alignToLine: false, exact: true);

            // Up to the old window's start, plus a query's length less one: a match across that edge (in a
            // line longer than a window) is still searched back to, one that starts after it is not.
            return _windowOffset == block.Start ? TextIndexAt(end) + query.Length - 1 : -1;
        }
        catch (OperationCanceledException)
        {
            return -1;
        }
        catch (Exception ex)
        {
            FindStatus = string.Format(LocalizationManager.Get("Viewer_ReadFailed"), ex.Message);
            return -1;
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
            NotifyNavigation();
        }
    }

    /// <summary>
    /// Where the byte at <paramref name="offset"/> (a character boundary within the window) falls in the
    /// window's text: the number of characters before it, decoded as the text pane decodes them.
    /// </summary>
    public int TextIndexAt(long offset)
    {
        var encoding = SelectedEncoding.Encoding ?? _autoEncoding.Value;
        var length = (int)Math.Clamp(offset - _windowOffset, 0, Math.Min(_data.Length, TextByteCap));
        return Math.Min(ViewerContent.DecodeText(_data[..length], encoding).Length, TextContent.Length);
    }

    // ── The panel's next / previous file (N/P, 6.41) ─────────────────────────

    private static readonly TimeSpan BusyStripDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>True when the window was opened from a panel with other files to step to.</summary>
    public bool CanBrowseFiles => _siblings is { Files.Files.Count: > 1 };

    private bool CanOpenSibling() => CanBrowseFiles && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanOpenSibling))]
    private Task OpenNextFile() => OpenSiblingAsync(+1);

    [RelayCommand(CanExecute = nameof(CanOpenSibling))]
    private Task OpenPreviousFile() => OpenSiblingAsync(-1);

    /// <summary>
    /// Shows the file <paramref name="step"/> places after (or before) this one in the panel's order. At
    /// either end nothing happens but a word in the status line. The first window is read as on F3 — the
    /// whole file for an image — with Cancel at hand (it may be on a server). Cancelling keeps the file in
    /// view; a failed read says so and still counts as a step, so the next press goes past that file
    /// instead of failing on it again.
    /// </summary>
    public async Task OpenSiblingAsync(int step)
    {
        if (_siblings is null || IsBusy)
        {
            return;
        }

        if (_siblings.Files.Neighbour(step) is not { } entry)
        {
            FindStatus = LocalizationManager.Get(step > 0 ? "Viewer_LastFile" : "Viewer_FirstFile");
            return;
        }

        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;
        using var reveal = CancellationTokenSource.CreateLinkedTokenSource(ct);
        IsBusy = true;
        BusyText = LocalizationManager.Get("Viewer_Opening");
        _ = RevealBusyStripAsync(reveal.Token);

        IViewerSource? source = null;
        try
        {
            source = _siblings.Open(entry);
            var progress = new Progress<long>(done => BusyText = string.Format(
                LocalizationManager.Get("Viewer_ReadingBytes"), FormatSize(done), FormatSize(Math.Max(entry.Size, done))));
            var data = await source.ReadAsync(0, ViewerLauncher.InitialReadBytes(entry.Name, entry.Size), progress, ct);
            ct.ThrowIfCancellationRequested();

            _siblings.Files.MoveTo(entry);
            var opened = source;
            source = null;   // the window owns it from here on
            ShowFile(entry.Name, data, entry.Size, opened);
        }
        catch (OperationCanceledException)
        {
            // cancelled or the window closed: the file in view stays
        }
        catch (Exception ex)
        {
            _siblings.Files.MoveTo(entry);
            FindStatus = string.Format(LocalizationManager.Get("Viewer_ReadFailed"), $"{entry.Name}: {ex.Message}");
        }
        finally
        {
            source?.Dispose();
            reveal.Cancel();
            IsOpeningSlowly = false;
            IsBusy = false;
            BusyText = string.Empty;
            NotifyNavigation();
        }
    }

    /// <summary>
    /// The busy strip comes up for a file switch only once it has taken a moment, so stepping through
    /// small files does not make the content jump down and back by a row on every key press.
    /// </summary>
    private async Task RevealBusyStripAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(BusyStripDelay, ct);
            if (!ct.IsCancellationRequested)
            {
                IsOpeningSlowly = true;
            }
        }
        catch (OperationCanceledException)
        {
            // done before it was worth showing
        }
    }

    /// <summary>
    /// Puts another file in this window (N/P): its first window replaces everything that described the old
    /// one — source, size, name, language, image, summary, find status — and the old source is disposed
    /// now, taking its unpacked copy of an archive entry with it (6.37). The view mode is chosen afresh,
    /// as on open, and the encoding goes back to Auto; word wrap stays, being a preference, not a property
    /// of the file.
    /// </summary>
    private void ShowFile(string fileName, byte[] data, long fullSize, IViewerSource source)
    {
        var previous = _source;
        _source = source;
        previous?.Dispose();

        _data = data;
        _windowOffset = 0;
        _fullSize = Math.Max(fullSize, data.Length);   // 0 from some servers: what was read is all there is
        _autoEncoding = NewAutoEncoding();
        _image = null;
        _imageError = null;
        _imageDecodeAttempted = false;
        Identify(fileName);

        _switchingFile = true;
        try
        {
            SelectedEncoding = Encodings[0];
        }
        finally
        {
            _switchingFile = false;
        }

        Summary = string.Empty;
        FindStatus = string.Empty;
        OffsetInput = string.Empty;

        // Everything bound to the file changed at once: an empty name refreshes every binding.
        OnPropertyChanged(string.Empty);

        var mode = ChooseInitialMode(fileName, data);
        if (Mode == mode)
        {
            Render();
        }
        else
        {
            Mode = mode;   // renders
        }

        NotifyNavigation();
    }

    // ── Summarise on request (6.14) ───────────────────────────────────────────

    /// <summary>Whether a model is configured — drives the button's visibility.</summary>
    public bool LlmAvailable => _llm?.IsConfigured == true;

    /// <summary>
    /// The summary, once asked for. Shown in a strip above the content rather than a message box, so
    /// it can be read next to the file it describes.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary))]
    private string _summary = string.Empty;

    public bool HasSummary => Summary.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSummarize))]
    private bool _isSummarizing;

    public bool CanSummarize => LlmAvailable && !IsSummarizing;

    /// <summary>
    /// Sends the decoded text for a summary — only when asked. Never on open: summarising every file
    /// the user glances at would ship the contents of whatever they browse to an endpoint, which is
    /// exactly what ADR-0008 rules out.
    /// </summary>
    [RelayCommand]
    private async Task SummarizeAsync()
    {
        if (_llm is null || !CanSummarize)
        {
            return;
        }

        // Whatever is on screen: if the user switched encoding to read the file correctly, that is
        // the text worth summarising.
        var content = TextContent;
        if (string.IsNullOrWhiteSpace(content))
        {
            Summary = LocalizationManager.Get("Viewer_SummaryNoText");
            return;
        }

        IsSummarizing = true;
        Summary = LocalizationManager.Get("Viewer_Summarizing");
        var fileName = FileName;
        try
        {
            var result = await _llm.CompleteAsync(TCV.Ai.Llm.SummaryPrompt.Build(fileName, content));

            // N/P moved on meanwhile: the summary is of a file no longer in view.
            if (fileName == FileName)
            {
                Summary = result.Success ? result.Text : Agents.LlmErrorText.Localize(result.Error);
            }
        }
        finally
        {
            IsSummarizing = false;
        }
    }

    /// <summary>Dismisses the summary strip.</summary>
    [RelayCommand]
    private void ClearSummary() => Summary = string.Empty;

    // ── Rendering ─────────────────────────────────────────────────────────────

    private void Render()
    {
        switch (Mode)
        {
            case ViewerMode.Text: RenderText(); break;
            case ViewerMode.Hex: RenderHex(); break;
            case ViewerMode.Image: EnsureImageDecoded(); break; // bound directly to the cached ImageSource
            case ViewerMode.Code:
            case ViewerMode.Markdown:
                // Both render the decoded text; the view turns it into a flow document.
                RenderText();
                DocumentInvalidated?.Invoke(this, EventArgs.Empty);
                break;
        }
        UpdateStatus();
    }

    private void RenderText()
    {
        var encoding = SelectedEncoding.Encoding ?? _autoEncoding.Value;
        var slice = Slice(TextByteCap);
        // A window cut at an arbitrary byte can end inside a multi-byte character; trim it back to a
        // boundary so the visible text doesn't end in a stray replacement character. Only when
        // something follows — the true end of the file is complete as it is.
        if (_windowOffset + slice.Length < _fullSize)
        {
            slice = ViewerContent.TrimIncompleteTail(slice, encoding);
        }

        TextContent = ViewerContent.DecodeText(slice, encoding);
    }

    private void RenderHex()
    {
        // Offsets in the dump are real file offsets, whichever window this is.
        HexContent = ViewerContent.ToHexDump(Slice(HexByteCap), _windowOffset);
    }

    private byte[] Slice(int cap) => _data.Length <= cap ? _data : _data[..cap];

    private void UpdateStatus()
    {
        var parts = new List<string> { FormatSize(_fullSize) };
        if (Mode is ViewerMode.Text or ViewerMode.Code or ViewerMode.Markdown)
        {
            parts.Add(EffectiveEncodingName());
        }

        parts.Add(Mode switch
        {
            ViewerMode.Text => LocalizationManager.Get("Viewer_Text"),
            ViewerMode.Hex => "HEX",
            ViewerMode.Code => IsDiffFile ? "Diff" : Language.Name,
            ViewerMode.Markdown => "Markdown",
            _ => LocalizationManager.Get("Viewer_Image"),
        });

        // Where the window is, when it is not the whole file — with the honest "only part" wording gone:
        // the range says exactly which part.
        var shown = Mode == ViewerMode.Hex ? Math.Min(_data.Length, HexByteCap) : Math.Min(_data.Length, TextByteCap);
        if (Mode != ViewerMode.Image && (_windowOffset > 0 || shown < _fullSize))
        {
            var end = _windowOffset + shown;
            parts.Add(string.Format(
                LocalizationManager.Get("Viewer_Range"),
                _windowOffset.ToString("N0", CultureInfo.CurrentCulture),
                end.ToString("N0", CultureInfo.CurrentCulture),
                _fullSize.ToString("N0", CultureInfo.CurrentCulture),
                _fullSize == 0 ? 1.0 : (double)end / _fullSize));
        }

        StatusText = string.Join("   ·   ", parts);
    }

    private string EffectiveEncodingName()
    {
        if (SelectedEncoding.Encoding is not null)
        {
            return SelectedEncoding.Name;
        }

        return string.Format(LocalizationManager.Get("Viewer_EncodingAutoNamed"), _autoEncoding.Value.WebName);
    }

    private static (ImageSource? Image, string? Error) TryDecodeImage(byte[] data)
    {
        if (data.Length == 0)
        {
            return (null, null);
        }

        try
        {
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(data, writable: false);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return (bmp, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    internal static string FormatSize(long bytes) => TCV.App.Converters.FileSizeConverter.Format(bytes);

    /// <summary>
    /// Cancels a read in flight, drops the source (with an archive entry's unpacked copy) and releases the
    /// file system lease the window holds.
    /// </summary>
    public void Dispose()
    {
        _loadCts?.Cancel();
        _source?.Dispose();
        _siblings?.Dispose();
    }
}

/// <summary>How an encoding entry is labelled: the automatic one follows the UI language.</summary>
public enum EncodingKind { Auto, Named }

/// <summary>A named text encoding offered in the viewer; a null <see cref="Encoding"/> means "Auto".</summary>
public sealed class EncodingOption(EncodingKind kind, Encoding? encoding, string? name = null)
{
    public Encoding? Encoding { get; } = encoding;

    public string Name => kind == EncodingKind.Auto ? LocalizationManager.Get("Viewer_EncodingAuto") : name ?? string.Empty;

    public override string ToString() => Name;
}

/// <summary>
/// Reads the offset field of the viewer: <c>4096</c>, <c>0x1000</c>, <c>50%</c>, <c>12MB</c>, <c>1.5 GB</c>.
/// </summary>
public static class OffsetParser
{
    public static bool TryParse(string? text, long length, out long offset)
    {
        offset = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim().Replace(" ", string.Empty).Replace(",", string.Empty);

        if (s.EndsWith('%'))
        {
            if (!double.TryParse(s[..^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var percent)
                || percent < 0 || percent > 100)
            {
                return false;
            }

            offset = (long)(length * percent / 100.0);
            return true;
        }

        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(s[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out offset) && offset >= 0;
        }

        return TCV.App.ViewModels.SizeParser.TryParse(s, out var bytes) && bytes.HasValue && (offset = bytes.Value) >= 0;
    }
}
