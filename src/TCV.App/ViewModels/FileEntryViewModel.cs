using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using TCV.App.Services;
using TCV.App.Settings;
using TCV.Core.Interfaces;

namespace TCV.App.ViewModels;

public partial class FileEntryViewModel : ObservableObject
{
    public FileEntryViewModel(IFileEntry entry, IReadOnlyList<ColorRule>? colorRules = null)
    {
        Entry = entry;
        Name = entry.Name;
        IsDirectory = entry.IsDirectory;
        Size = entry.IsDirectory ? 0 : entry.Size;
        LastModified = entry.LastModified;
        Attributes = entry.Attributes;
        Extension = IsDirectory ? string.Empty : Path.GetExtension(Name).TrimStart('.');
        NameWithoutExtension = IsDirectory ? Name : Path.GetFileNameWithoutExtension(Name);

        // Apply color rules (first matching rule wins)
        if (colorRules != null)
        {
            foreach (var rule in colorRules)
            {
                if (!MatchesRule(rule)) continue;
                if (!string.IsNullOrEmpty(rule.Foreground))
                {
                    try { RowForeground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(rule.Foreground)); }
                    catch { /* ignore bad color strings */ }
                }
                if (!string.IsNullOrEmpty(rule.Background))
                {
                    try { RowBackground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(rule.Background)); }
                    catch { /* ignore bad color strings */ }
                }
                break;
            }
        }
    }

    private FileEntryViewModel(string name, bool isDirectory)
    {
        Entry = null!;
        Name = name;
        IsDirectory = isDirectory;
        Size = 0;
        LastModified = default;
        Attributes = default;
        Extension = string.Empty;
        NameWithoutExtension = name;
        IsParentEntry = true;
    }

    public static FileEntryViewModel CreateParent() => new("..", true);

    public IFileEntry Entry { get; }
    public bool IsParentEntry { get; }
    public string Name { get; }
    public string NameWithoutExtension { get; }
    public bool IsDirectory { get; }
    public long Size { get; }
    public DateTime LastModified { get; }
    public FileAttributes Attributes { get; }
    public string Extension { get; }

    // Color rule results
    public Brush? RowForeground { get; }
    public Brush? RowBackground { get; }
    public bool HasColorRule => RowForeground != null || RowBackground != null;

    public bool IsHidden => Attributes.HasFlag(FileAttributes.Hidden);
    public bool IsSystem => Attributes.HasFlag(FileAttributes.System);

    /// <summary>Opacity for the file icon: hidden files are ghosted, system files are dimmed.</summary>
    public double IconOpacity => IsHidden ? 0.4 : IsSystem ? 0.65 : 1.0;

    /// <summary>
    /// Size column text: nothing for "..", the formatted size of a file (an empty one is "0 B"); a folder
    /// shows its measured size — "≥" before it when a subfolder could not be read —, an ellipsis while
    /// it is being measured, a dash until then.
    /// </summary>
    public string SizeDisplay => IsParentEntry ? string.Empty
        : !IsDirectory ? FormatSize(Size)
        : FolderSize is { } bytes ? (FolderSizeComplete ? string.Empty : "≥ ") + FormatSize(bytes)
        : IsMeasuringSize ? "…"
        : "—";

    // ── Folder size (Space on a folder, «Folder sizes») ─────────────────────────
    // Filled in by FileListViewModel from a background walk; a listing never carries it.

    /// <summary>Bytes in the folder and everything under it, links not followed; null until measured.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeDisplay))]
    private long? _folderSize;

    /// <summary>False when a subfolder could not be read, so <see cref="FolderSize"/> is a lower bound.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeDisplay))]
    private bool _folderSizeComplete = true;

    /// <summary>The folder is waiting for or under measurement.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeDisplay))]
    private bool _isMeasuringSize;

    /// <summary>What the folder or file adds to a total: a folder counts once it was measured.</summary>
    public long KnownSize => IsDirectory ? FolderSize ?? 0 : Size;

    /// <summary>Attributes column text in the familiar "rahs" form; blank for "..".</summary>
    public string AttributesDisplay => IsParentEntry ? string.Empty : FormatAttributes(Attributes);

    /// <summary>Read-only, archive, hidden, system as letters, '-' where unset (as Total Commander shows them).</summary>
    public static string FormatAttributes(FileAttributes attributes) => string.Create(4, attributes, static (span, a) =>
    {
        span[0] = a.HasFlag(FileAttributes.ReadOnly) ? 'r' : '-';
        span[1] = a.HasFlag(FileAttributes.Archive) ? 'a' : '-';
        span[2] = a.HasFlag(FileAttributes.Hidden) ? 'h' : '-';
        span[3] = a.HasFlag(FileAttributes.System) ? 's' : '-';
    });

    // File type description from Shell (lazy)
    private string? _typeNameCache;
    private bool _typeNameLoaded;
    public string TypeName
    {
        get
        {
            if (!_typeNameLoaded)
            {
                _typeNameLoaded = true;
                _typeNameCache = IsParentEntry ? string.Empty
                    : ShellIconService.GetTypeName(Extension, IsDirectory);
            }
            return _typeNameCache ?? string.Empty;
        }
    }

    // Shell icon (lazy)
    private BitmapSource? _smallIconCache;
    private bool _smallIconLoaded;
    public BitmapSource? SmallIcon
    {
        get
        {
            if (!_smallIconLoaded)
            {
                _smallIconLoaded = true;
                // «..» is a folder too (CreateParent), so it gets the folder's icon, not the one blank row.
                _smallIconCache = ShellIconService.GetSmallIcon(Extension, IsDirectory);
            }
            return _smallIconCache;
        }
    }

    // Large icon for Thumbnails mode (lazy)
    private BitmapSource? _largeIconCache;
    private bool _largeIconLoaded;
    public BitmapSource? LargeIcon
    {
        get
        {
            if (!_largeIconLoaded)
            {
                _largeIconLoaded = true;
                _largeIconCache = ShellIconService.GetLargeIcon(Extension, IsDirectory);
            }
            return _largeIconCache;
        }
    }

    /// <summary>
    /// The picture of an image file in the Thumbnails view. Set by the view only while the tile is on
    /// screen (or about to be) and cleared when it scrolls away, so a folder of thousands of photos holds
    /// a screenful of pictures, not all of them; <see cref="Services.ThumbnailService"/> remembers more.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TileImage))]
    private BitmapSource? _thumbnail;

    /// <summary>What a Thumbnails tile shows: the picture when there is one, the type's icon otherwise.</summary>
    public BitmapSource? TileImage => Thumbnail ?? LargeIcon;

    [ObservableProperty]
    private bool _isSelected;

    // ── Renaming in place (FileListViewModel.EditNameInPlaceAsync) ──────────────

    /// <summary>The name is shown as a box in the list, being edited.</summary>
    [ObservableProperty]
    private bool _isEditingName;

    /// <summary>The name as typed in the box.</summary>
    [ObservableProperty]
    private string _editedName = string.Empty;

    /// <summary>Why Enter did not take the typed name (a character Windows refuses), or null.</summary>
    [ObservableProperty]
    private string? _nameEditError;

    /// <summary>
    /// Set when an edit opens: the box selects the name without its extension once, as Explorer does,
    /// and only puts the caret back at the end when it is shown again for the same edit.
    /// </summary>
    public bool SelectBaseNameOnEdit { get; set; }

    /// <summary>How much of the typed name the box selects first: up to the extension, a folder whole.</summary>
    public int BaseNameLength
    {
        get
        {
            var dot = IsDirectory ? -1 : EditedName.LastIndexOf('.');
            return dot > 0 ? dot : EditedName.Length;
        }
    }

    // The message answered the name that was refused; a new keystroke is a new name.
    partial void OnEditedNameChanged(string value) => NameEditError = null;

    // ── Dev columns (6.19) ────────────────────────────────────────────────────
    // Filled in by FileListViewModel from a background pass, and only while the corresponding
    // column is switched on; until then the cells stay blank rather than showing a wrong zero.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LineCountDisplay))]
    private int? _lineCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeOnDiskDisplay))]
    private long? _sizeOnDisk;

    /// <summary>
    /// True once the measured columns (LOC, size on disk) were computed for this row; rows scrolled
    /// into view later are measured on demand, so this tells a pass what is still to do.
    /// </summary>
    public bool MetricsMeasured { get; set; }

    /// <summary>Line count for the LOC column; blank for directories and unmeasurable files.</summary>
    public string LineCountDisplay => LineCount is { } lines ? lines.ToString("N0") : string.Empty;

    /// <summary>Allocated size for the on-disk column, formatted like the size column.</summary>
    public string SizeOnDiskDisplay =>
        IsDirectory || SizeOnDisk is null ? string.Empty : FormatSize(SizeOnDisk.Value);

    // ── Git column ────────────────────────────────────────────────────────────
    // Filled in by FileListViewModel from one background status pass, and only while the column is
    // on; a blank cell means "clean or unknown", never an error.

    [ObservableProperty]
    private string _gitLabel = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GitToolTip))]
    private TCV.Git.GitCellKind _gitKind;

    /// <summary>Plain-language meaning of the label, for hover and for screen readers.</summary>
    public string GitToolTip => GitKind switch
    {
        TCV.Git.GitCellKind.Conflict => TCV.App.Localization.LocalizationManager.Get("Git_CellConflict"),
        TCV.Git.GitCellKind.Modified => TCV.App.Localization.LocalizationManager.Get(
            IsDirectory ? "Git_CellFolderChanged" : "Git_CellModified"),
        TCV.Git.GitCellKind.Staged => TCV.App.Localization.LocalizationManager.Get("Git_CellStaged"),
        TCV.Git.GitCellKind.Untracked => TCV.App.Localization.LocalizationManager.Get("Git_CellUntracked"),
        _ => string.Empty,
    };

    // ── Origin mark (who changed it) ───────────────────────────────────────────
    // Set by FileListViewModel from the project's «Agent changes» verdicts; None outside a project an
    // agent touched, and for anything not on the disk.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOrigin), nameof(OriginToolTip))]
    private TCV.App.Agents.FileOriginMark _originMark;

    private TCV.Ai.Agents.ChangeOrigin _origin;

    public bool HasOrigin => OriginMark != TCV.App.Agents.FileOriginMark.None;

    /// <summary>The verdict and what it rests on, worded as the dashboard words it.</summary>
    public string OriginToolTip => HasOrigin
        ? TCV.App.Agents.AgentOriginText.Name(_origin) + " · " + TCV.App.Agents.AgentOriginText.Hint(_origin)
        : string.Empty;

    public void SetOrigin(TCV.App.Agents.FileOriginMark mark, TCV.Ai.Agents.ChangeOrigin origin)
    {
        var tooltipChanges = _origin != origin;
        _origin = origin;
        OriginMark = mark;
        if (tooltipChanges)
        {
            OnPropertyChanged(nameof(OriginToolTip));
        }
    }

    /// <summary>Which line icon the row shows (<see cref="FileIconClass"/>).</summary>
    public FileIconKind IconKind => FileIconClass.For(Extension, IsDirectory);

    /// <summary>The extension shown on a badge instead of the line icon («PDF»), or null (<see cref="FileIconClass.BadgeFor"/>).</summary>
    public string? TypeBadge => IsParentEntry ? null : FileIconClass.BadgeFor(Extension, IsDirectory);

    // One format for the size and size-on-disk columns, so the two can be compared at a glance.
    private static string FormatSize(long bytes) => TCV.App.Converters.FileSizeConverter.Format(bytes);

    private bool MatchesRule(ColorRule rule)
    {
        if (IsDirectory && !rule.ApplyToDirectories) return false;
        return MatchWildcard(rule.Pattern, Name);
    }

    private static bool MatchWildcard(string pattern, string name)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        // Simple wildcard: * and ?
        return WildcardMatch(pattern.AsSpan(), name.AsSpan());
    }

    private static bool WildcardMatch(ReadOnlySpan<char> pattern, ReadOnlySpan<char> input)
    {
        if (pattern.IsEmpty) return input.IsEmpty;
        if (pattern[0] == '*')
        {
            for (int i = 0; i <= input.Length; i++)
                if (WildcardMatch(pattern[1..], input[i..]))
                    return true;
            return false;
        }
        if (input.IsEmpty) return false;
        if (pattern[0] == '?' || char.ToUpperInvariant(pattern[0]) == char.ToUpperInvariant(input[0]))
            return WildcardMatch(pattern[1..], input[1..]);
        return false;
    }
}
