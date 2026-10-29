using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Localization;
using TCV.App.Services;
using TCV.App.Workspaces;

namespace TCV.App.ViewModels.Dialogs;

/// <summary>
/// What the properties window needs for the picture: where pictures are kept, the folder the project
/// lives in (its repository is searched), and a file picker owned by the window.
/// </summary>
public sealed record WorkspacePictureContext(
    WorkspaceIconStore Store, string? ProjectFolder, Func<string?, string?> PickFile);

/// <summary>A picture found in the project, drawn as the rail will show it.</summary>
public sealed partial class WorkspacePictureCandidate : ObservableObject
{
    public WorkspacePictureCandidate(string path, BitmapSource picture)
    {
        Path = path;
        Picture = picture;
    }

    /// <summary>Where it was found — the tooltip.</summary>
    public string Path { get; }

    public BitmapSource Picture { get; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>One ready-made colour in the properties dialog; selected when it is the current colour.</summary>
public sealed partial class WorkspaceColorSwatch : ObservableObject
{
    public WorkspaceColorSwatch(string hex)
    {
        Hex = hex;
    }

    public string Hex { get; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// Everything that identifies a workspace in one window: name, icon, colour, sidebar group and
/// pinning. These used to be five separate prompts, each a bare text box — a colour had to be typed
/// as hex and nothing said what a group was for. The dialog shows the result as the sidebar will.
/// The terminal the project is driven with lives here too: it used to sit under the notes, where
/// nothing said that the workspace terminal command was what ran it.
/// </summary>
public partial class WorkspacePropertiesDialogViewModel : ObservableObject
{
    private readonly HashSet<string> _otherNames;

    /// <param name="workspace">The workspace being edited; it is not modified here.</param>
    /// <param name="allNames">Every saved workspace name, so a rename cannot take one that exists.</param>
    /// <param name="groups">Groups already in use, offered in the drop-down.</param>
    /// <param name="isNew">The workspace does not exist yet: the window creates it rather than edits it.</param>
    /// <param name="pictures">Null leaves the picture out (the window was opened without a store).</param>
    public WorkspacePropertiesDialogViewModel(
        Workspace workspace, IEnumerable<string> allNames, IEnumerable<string> groups, bool isNew = false,
        WorkspacePictureContext? pictures = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        OriginalName = workspace.Name;
        _otherNames = allNames
            .Where(n => !string.Equals(n, workspace.Name, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Groups = groups
            .Select(g => (g ?? string.Empty).Trim())
            .Where(g => g.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Swatches = WorkspaceColor.Presets.Select(h => new WorkspaceColorSwatch(h)).ToList();
        WindowTitle = isNew
            ? LocalizationManager.Get("WsProps_NewTitle")
            : string.Format(LocalizationManager.Get("WsProps_WindowTitle"), workspace.Name);
        ConfirmText = LocalizationManager.Get(isNew ? "WsProps_Create" : "OK");

        _name = workspace.Name;
        _icon = workspace.Icon;
        _colorText = workspace.Color;
        _group = workspace.Group;
        _pinned = workspace.Pinned;
        _terminalCommand = workspace.TerminalCommand;
        _terminalDirectory = workspace.TerminalDirectory;
        SyncSwatches();

        _pictures = pictures;
        _iconImage = workspace.IconImage;
        _autoIconImage = workspace.AutoIconImage;
        _iconImageSource = pictures?.Store.Load(workspace.IconImage);

        // A new workspace from a folder takes the project's best picture straight away, as the search
        // would give it later; the window shows it, and it can be changed or taken away before Create.
        _preselectFound = isNew && WorkspacePicture.WantsSearch(workspace);
        if (pictures?.ProjectFolder is { } folder)
        {
            _ = FindCandidatesAsync(folder);
        }
    }

    private readonly WorkspacePictureContext? _pictures;
    private readonly bool _preselectFound;

    /// <summary>The picture can be chosen here (false for a window opened without a store).</summary>
    public bool CanChoosePicture => _pictures is not null;

    /// <summary>The picture in the store, or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIconImage))]
    private string _iconImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIconImage))]
    private ImageSource? _iconImageSource;

    /// <summary>The project may still be searched for a picture: the user has not chosen one, an emoji or none.</summary>
    [ObservableProperty]
    private bool _autoIconImage;

    public bool HasIconImage => IconImageSource is not null;

    /// <summary>Pictures found in the project's repository, best first.</summary>
    public ObservableCollection<WorkspacePictureCandidate> Candidates { get; } = new();

    /// <summary>A picture is being drawn or stored: Create/OK waits for it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private bool _isPictureBusy;

    /// <summary>Why the chosen file could not be used, or empty.</summary>
    [ObservableProperty]
    private string _pictureError = string.Empty;

    /// <summary>An emoji typed or picked is a choice too: the project is no longer searched for a picture.</summary>
    partial void OnIconChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            AutoIconImage = false;
        }
    }

    private async Task FindCandidatesAsync(string folder)
    {
        var paths = await Task.Run(() => ProjectIconFinder.FindRepositoryRoot(folder) is { } root
            ? ProjectIconFinder.Find(root)
            : []);
        // Near-square pictures first, in the finder's order; a wide wordmark is still offered, after them.
        var wide = new List<WorkspacePictureCandidate>();
        foreach (var path in paths)
        {
            if (await WorkspaceIconStore.RenderAsync(path) is { } drawn)
            {
                var candidate = new WorkspacePictureCandidate(path, drawn.Bitmap);
                if (drawn.IsSquarish)
                {
                    Candidates.Add(candidate);
                }
                else
                {
                    wide.Add(candidate);
                }
            }
        }

        var firstSquare = Candidates.FirstOrDefault();
        foreach (var candidate in wide)
        {
            Candidates.Add(candidate);
        }

        if (_preselectFound && firstSquare is not null && IconImage.Length == 0 && string.IsNullOrWhiteSpace(Icon))
        {
            await UseAsync(firstSquare, byUser: false);
        }
    }

    [RelayCommand]
    private Task UseCandidate(WorkspacePictureCandidate? candidate) =>
        candidate is null ? Task.CompletedTask : UseAsync(candidate, byUser: true);

    /// <summary>Any picture on disk — in the project or not.</summary>
    [RelayCommand]
    private async Task PickPictureFile()
    {
        if (_pictures is null)
        {
            return;
        }

        // Off the UI thread: finding the repository walks up the disk, which may be a share (INV-06).
        var start = _pictures.ProjectFolder is { } folder
            ? await Task.Run(() => ProjectIconFinder.FindRepositoryRoot(folder) ?? folder)
            : null;
        if (_pictures.PickFile(start) is not { } path)
        {
            return;
        }

        BeginPictureWork();
        try
        {
            if (await WorkspaceIconStore.RenderAsync(path) is not { } drawn)
            {
                PictureError = LocalizationManager.Get("WsProps_PictureUnreadable");
                return;
            }

            await StoreAsync(drawn.Bitmap, byUser: true);
            foreach (var candidate in Candidates)
            {
                candidate.IsSelected = false;
            }
        }
        finally
        {
            EndPictureWork();
        }
    }

    /// <summary>Pictures being drawn or stored at once: the window's preselection and the user's own pick.</summary>
    private int _pictureWork;

    private void BeginPictureWork() => IsPictureBusy = ++_pictureWork > 0;

    private void EndPictureWork() => IsPictureBusy = --_pictureWork > 0;

    /// <summary>No picture — and none looked for again: the emoji or the letters stay.</summary>
    [RelayCommand]
    private void RemovePicture()
    {
        IconImage = string.Empty;
        IconImageSource = null;
        AutoIconImage = false;
        PictureError = string.Empty;
        foreach (var candidate in Candidates)
        {
            candidate.IsSelected = false;
        }
    }

    private async Task UseAsync(WorkspacePictureCandidate chosen, bool byUser)
    {
        BeginPictureWork();
        try
        {
            if (await StoreAsync(chosen.Picture, byUser))
            {
                foreach (var candidate in Candidates)
                {
                    candidate.IsSelected = ReferenceEquals(candidate, chosen);
                }
            }
        }
        finally
        {
            EndPictureWork();
        }
    }

    private async Task<bool> StoreAsync(BitmapSource picture, bool byUser)
    {
        if (_pictures is null || await _pictures.Store.SaveAsync(picture) is not { } name)
        {
            PictureError = LocalizationManager.Get("WsProps_PictureUnsaved");
            return false;
        }

        // The window's own preselection yields to a choice made while it was drawing: a picture picked,
        // an emoji typed or the picture removed.
        if (!byUser && (!AutoIconImage || IconImage.Length > 0 || !string.IsNullOrWhiteSpace(Icon)))
        {
            return false;
        }

        PictureError = string.Empty;
        IconImage = name;
        IconImageSource = picture;
        if (byUser)
        {
            AutoIconImage = false;
        }

        return true;
    }

    public string OriginalName { get; }
    public string WindowTitle { get; }

    /// <summary>"Create" for a new workspace, "OK" for an existing one.</summary>
    public string ConfirmText { get; }

    public IReadOnlyList<WorkspaceColorSwatch> Swatches { get; }
    public IReadOnlyList<string> Icons => Workspace.SuggestedIcons;
    public IReadOnlyList<string> Groups { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameError), nameof(PreviewName))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewName))]
    private string _icon;

    /// <summary>The colour as typed or picked; blank means no colour.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ColorError), nameof(NormalizedColor))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private string _colorText;

    [ObservableProperty]
    private string _group;

    [ObservableProperty]
    private bool _pinned;

    /// <summary>What the workspace terminal command runs; empty opens a plain shell.</summary>
    [ObservableProperty]
    private string _terminalCommand;

    /// <summary>Where it runs; empty means the folder of the workspace's active panel.</summary>
    [ObservableProperty]
    private string _terminalDirectory;

    partial void OnColorTextChanged(string value) => SyncSwatches();

    /// <summary>Why the name cannot be used, or empty when it can.</summary>
    public string NameError
    {
        get
        {
            var name = (Name ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                return LocalizationManager.Get("WsProps_NameEmpty");
            }

            return _otherNames.Contains(name)
                ? string.Format(LocalizationManager.Get("Ws_RenameTaken"), name)
                : string.Empty;
        }
    }

    /// <summary>Why the colour cannot be used, or empty when it can (blank is fine: no colour).</summary>
    public string ColorError =>
        WorkspaceColor.TryNormalize(ColorText, out _) ? string.Empty : LocalizationManager.Get("WsColor_Invalid");

    /// <summary><c>#RRGGBB</c>, or empty while there is no colour or it does not parse.</summary>
    public string NormalizedColor =>
        WorkspaceColor.TryNormalize(ColorText, out var normalized) ? normalized : string.Empty;

    /// <summary>The name as the menu and the sidebar will show it.</summary>
    public string PreviewName
    {
        get
        {
            var name = (Name ?? string.Empty).Trim();
            var icon = (Icon ?? string.Empty).Trim();
            return icon.Length == 0 ? name : $"{icon} {name}";
        }
    }

    public WorkspacePropertiesResult? Result { get; private set; }

    public bool DialogResult { get; private set; }
    public event EventHandler? CloseRequested;

    /// <summary>Picks a ready-made colour; null or empty clears it.</summary>
    [RelayCommand]
    private void PickColor(string? hex) => ColorText = hex ?? string.Empty;

    [RelayCommand]
    private void PickIcon(string? icon) => Icon = icon ?? string.Empty;

    private bool CanConfirm() => NameError.Length == 0 && ColorError.Length == 0 && !IsPictureBusy;

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        WorkspaceColor.TryNormalize(ColorText, out var color);
        Result = new WorkspacePropertiesResult(
            (Name ?? string.Empty).Trim(),
            (Icon ?? string.Empty).Trim(),
            color,
            (Group ?? string.Empty).Trim(),
            Pinned,
            IconImage ?? string.Empty,
            AutoIconImage,
            (TerminalCommand ?? string.Empty).Trim(),
            (TerminalDirectory ?? string.Empty).Trim());
        DialogResult = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        DialogResult = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SyncSwatches()
    {
        var current = NormalizedColor;
        foreach (var swatch in Swatches)
        {
            swatch.IsSelected = string.Equals(swatch.Hex, current, StringComparison.OrdinalIgnoreCase);
        }
    }
}
