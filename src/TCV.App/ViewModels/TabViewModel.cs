using CommunityToolkit.Mvvm.ComponentModel;

namespace TCV.App.ViewModels;

public partial class TabViewModel : ObservableObject
{
    public TabViewModel(string path, string? customTitle = null)
    {
        Path = path;
        _customTitle = customTitle;
        _isLocked = false;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    [NotifyPropertyChangedFor(nameof(ToolTip))]
    private string _path;

    /// <summary>
    /// What the tab shows when that is not a disk folder: a server, an archive, search results.
    /// Null on the disk. Kept while the tab is parked, so a tab on a server says so even when another
    /// tab is on screen.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    [NotifyPropertyChangedFor(nameof(ToolTip))]
    [NotifyPropertyChangedFor(nameof(Annotation))]
    [NotifyPropertyChangedFor(nameof(HasAnnotation))]
    [NotifyPropertyChangedFor(nameof(AnnotationToolTip))]
    [NotifyPropertyChangedFor(nameof(IsOnServer))]
    [NotifyPropertyChangedFor(nameof(IsInArchive))]
    [NotifyPropertyChangedFor(nameof(IsSearchResults))]
    [NotifyPropertyChangedFor(nameof(IsMounted))]
    [NotifyPropertyChangedFor(nameof(IconKind))]
    [NotifyPropertyChangedFor(nameof(ConnectionColor))]
    private TabLocation? _location;

    /// <summary>
    /// The mounts this tab keeps while another tab is on screen (ADR-0032). The panel parks them on
    /// leaving the tab and restores them on coming back; closing the tab closes them.
    /// </summary>
    internal FileListViewModel.ParkedMounts? Parked { get; set; }

    /// <summary>The tab shows a server (or an archive opened on one): its icon is the server's.</summary>
    public bool IsOnServer => Location?.Connection is not null;

    public bool IsInArchive => Location is { Kind: MountKind.Archive, Connection: null };

    public bool IsSearchResults => Location?.Kind == MountKind.SearchResults;

    public bool IsMounted => Location is not null;

    /// <summary>The tab's icon: a folder, a server, a cloud (object storage, a drive), an archive, search hits.</summary>
    public string IconKind => Location switch
    {
        { Connection: { IsObjectStorage: true } or { IsOAuth: true } } => "cloud",
        { Connection: not null } => "server",
        { Kind: MountKind.Archive } => "archive",
        { Kind: MountKind.SearchResults } => "search",
        _ => "folder",
    };

    /// <summary>The colour tag of the tab's connection, or null: production in red reads at a glance.</summary>
    public System.Windows.Media.Brush? ConnectionColor =>
        Location?.Connection?.Color is { Length: > 0 } hex
            ? Converters.WorkspaceColorToBrushConverter.Instance.Convert(
                hex, typeof(System.Windows.Media.Brush), null!, System.Globalization.CultureInfo.InvariantCulture)
                as System.Windows.Media.Brush
            : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    private string? _customTitle;

    [ObservableProperty]
    private bool _isLocked;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private string _renameText = string.Empty;

    /// <summary>
    /// Branch checked out in this tab's directory, or empty outside a repository. Shown next to the
    /// title rather than folded into it: with several worktrees of one repository open, the folder
    /// names are near-identical and the branch is the only thing that tells the tabs apart.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBranch))]
    [NotifyPropertyChangedFor(nameof(BranchToolTip))]
    [NotifyPropertyChangedFor(nameof(Annotation))]
    [NotifyPropertyChangedFor(nameof(HasAnnotation))]
    [NotifyPropertyChangedFor(nameof(AnnotationToolTip))]
    private string _branch = string.Empty;

    /// <summary>
    /// The small mono text after the title: the folder on the server or in the archive the tab is in,
    /// else the git branch. A tab on a server is titled by the connection, so its folder goes here.
    /// </summary>
    public string Annotation => Location is { } location ? location.Detail ?? string.Empty : Branch;

    public bool HasAnnotation => Annotation.Length > 0;

    public string AnnotationToolTip => Location is { } location ? location.Address : BranchToolTip;

    /// <summary>The tab's hover text: the whole address on a server or in an archive, else the folder path.</summary>
    public string ToolTip => Location?.Address ?? Path;

    /// <summary>Drives the visibility of the branch label — no repository, no decoration.</summary>
    public bool HasBranch => Branch.Length > 0;

    /// <summary>
    /// Hover text for the branch label. The label is trimmed to a short width, and a detached HEAD
    /// is shown as a bare 7-digit id, which reads like a branch name without the hint.
    /// </summary>
    public string BranchToolTip => DescribeBranch(Branch);

    /// <summary>
    /// The head reader reports a detached HEAD as a 7-character abbreviated id; a branch that
    /// happens to be seven hex digits is described the same way, which is the price of not
    /// carrying a flag through the panel.
    /// </summary>
    public static string DescribeBranch(string branch)
    {
        if (string.IsNullOrEmpty(branch))
        {
            return string.Empty;
        }

        var detached = branch.Length == 7 && branch.All(Uri.IsHexDigit);
        return string.Format(
            TCV.App.Localization.LocalizationManager.Get(detached ? "Tab_DetachedTip" : "Tab_BranchTip"),
            branch);
    }

    partial void OnIsRenamingChanged(bool value)
    {
        if (value)
            RenameText = CustomTitle ?? DisplayTitle;
    }

    public string DisplayTitle
    {
        get
        {
            if (CustomTitle is not null) return CustomTitle;
            if (Location is { } location)
            {
                return location.Title;
            }

            var name = System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'));
            return name.Length > 0 ? name : Path;
        }
    }
}

/// <summary>
/// How a tab that is not on a disk folder presents itself. A tab on a server is titled by the
/// connection: with three servers open, three tabs called "logs" would not say which is which.
/// </summary>
/// <param name="Kind">What is mounted on top.</param>
/// <param name="Title">The connection's name, the archive's file name, or "Search results".</param>
/// <param name="Detail">The folder inside it, when not at its root; shown small after the title.</param>
/// <param name="Address">The whole address for the tooltip: <c>sftp://me@host/var/log</c>.</param>
/// <param name="Connection">The server under the location, or null for an archive or search on the disk.</param>
public sealed record TabLocation(
    MountKind Kind, string Title, string? Detail, string Address, TCV.Network.ConnectionInfo? Connection);
