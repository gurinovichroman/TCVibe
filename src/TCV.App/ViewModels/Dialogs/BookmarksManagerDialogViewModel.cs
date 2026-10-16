using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Localization;
using TCV.App.Services;
using TCV.App.Settings;

namespace TCV.App.ViewModels.Dialogs;

/// <summary>
/// Edits the bookmark list. Works on copies: nothing reaches <see cref="AppSettings"/> until OK, so
/// Cancel really cancels (the list used to share its objects with the settings, and "Apply" wrote
/// straight through them). Typing in the form updates the selected copy immediately, so moving to
/// another bookmark can no longer drop what was just typed.
/// </summary>
public partial class BookmarksManagerDialogViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly IDialogService _dialogs;
    private readonly string _currentPath;
    private readonly Action? _persist;
    private bool _loadingForm;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <param name="currentPath">The folder the active panel shows; a new bookmark starts on it.</param>
    /// <param name="persist">Writes <c>settings.json</c> after OK, or null when this window must not
    /// (a <c>--workspace</c> guest window, TRAP-22). The bookmarks then apply to this window only.</param>
    public BookmarksManagerDialogViewModel(
        AppSettings settings, IDialogService dialogs, string currentPath, Action? persist)
    {
        _settings = settings;
        _dialogs = dialogs;
        _currentPath = currentPath;
        _persist = persist;

        foreach (var b in settings.Bookmarks)
        {
            Items.Add(new BookmarkItemViewModel(Clone(b)));
        }
    }

    /// <summary>
    /// True in a <c>--workspace</c> guest window (TRAP-22): the list it saves lasts until that window
    /// closes, and the dialog says so instead of letting a bookmark vanish without a word.
    /// </summary>
    public bool IsGuestWindow => _persist is null;

    private static Bookmark Clone(Bookmark b) => new()
    {
        Name = b.Name ?? string.Empty,
        Path = b.Path ?? string.Empty,
        Group = b.Group ?? string.Empty,
    };

    // ── Collections & selection ───────────────────────────────────────────────

    public ObservableCollection<BookmarkItemViewModel> Items { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    private BookmarkItemViewModel? _selectedItem;

    public bool HasSelection => SelectedItem is not null;

    /// <summary>Problem with the selected bookmark, in words; empty when it is fine.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidationMessage))]
    private string _validationMessage = string.Empty;

    public bool HasValidationMessage => ValidationMessage.Length > 0;

    // ── Edit-form properties ──────────────────────────────────────────────────

    [ObservableProperty] private string _editName = string.Empty;
    [ObservableProperty] private string _editPath = string.Empty;
    [ObservableProperty] private string _editGroup = string.Empty;

    partial void OnSelectedItemChanged(BookmarkItemViewModel? value)
    {
        _loadingForm = true;
        try
        {
            EditName = value?.Bookmark.Name ?? string.Empty;
            EditPath = value?.Bookmark.Path ?? string.Empty;
            EditGroup = value?.Bookmark.Group ?? string.Empty;
        }
        finally
        {
            _loadingForm = false;
        }

        // A bookmark that was never filled in should say what is missing as soon as it is shown.
        ValidationMessage = value is null ? string.Empty : Describe(value.Bookmark);
    }

    partial void OnEditNameChanged(string value) => WriteThrough(b => b.Name = value);
    partial void OnEditPathChanged(string value) => WriteThrough(b => b.Path = value);
    partial void OnEditGroupChanged(string value) => WriteThrough(b => b.Group = value);

    private void WriteThrough(Action<Bookmark> apply)
    {
        if (_loadingForm || SelectedItem is null)
        {
            return;
        }

        apply(SelectedItem.Bookmark);
        SelectedItem.Refresh();
        ValidationMessage = Describe(SelectedItem.Bookmark);
    }

    private static string Describe(Bookmark b) => BookmarkValidator.Validate(b.Name, b.Path) switch
    {
        BookmarkIssue.EmptyName => LocalizationManager.Get("Bookmarks_ErrName"),
        BookmarkIssue.EmptyPath => LocalizationManager.Get("Bookmarks_ErrPathEmpty"),
        BookmarkIssue.NotAbsolute => LocalizationManager.Get("Bookmarks_ErrPathRelative"),
        _ => string.Empty,
    };

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Add()
    {
        var path = _currentPath ?? string.Empty;
        var folder = Path.GetFileName(path.TrimEnd('\\', '/'));
        var b = new Bookmark
        {
            Name = string.IsNullOrEmpty(folder) ? LocalizationManager.Get("Bookmarks_NewName") : folder,
            Path = path,
        };
        var item = new BookmarkItemViewModel(b);
        Items.Add(item);
        SelectedItem = item;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Remove()
    {
        if (SelectedItem is null)
        {
            return;
        }

        var idx = Items.IndexOf(SelectedItem);
        Items.Remove(SelectedItem);
        SelectedItem = Items.Count > 0 ? Items[Math.Max(0, idx - 1)] : null;
    }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp()
    {
        if (SelectedItem is null)
        {
            return;
        }

        var idx = Items.IndexOf(SelectedItem);
        if (idx <= 0)
        {
            return;
        }

        Items.Move(idx, idx - 1);
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown()
    {
        if (SelectedItem is null)
        {
            return;
        }

        var idx = Items.IndexOf(SelectedItem);
        if (idx < 0 || idx >= Items.Count - 1)
        {
            return;
        }

        Items.Move(idx, idx + 1);
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task ImportBookmarks()
    {
        var file = _dialogs.PickOpenFile(
            LocalizationManager.Get("Bookmarks_ImportTitle"), LocalizationManager.Get("Bookmarks_FileFilter"));
        if (file is null)
        {
            return;
        }

        var title = LocalizationManager.Get("Bookmarks_ImportTitle");
        List<Bookmark> imported;
        try
        {
            var json = await File.ReadAllTextAsync(file);

            // The file is hand-editable: entries and fields may be null.
            imported = JsonSerializer.Deserialize<List<Bookmark>>(json, JsonOptions)?
                .Where(b => b is not null).Select(Clone).ToList() ?? new List<Bookmark>();
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync(
                title, string.Format(LocalizationManager.Get("Bookmarks_ImportFailed"), ex.Message));
            return;
        }

        // An empty file used to clear the list without a word.
        if (imported.Count == 0)
        {
            await _dialogs.ShowErrorAsync(title, LocalizationManager.Get("Bookmarks_ImportEmpty"));
            return;
        }

        // Replacing a list that has bookmarks throws them away, so it is asked (INV-11); merging is
        // offered first, as the answer that loses nothing. An empty list has nothing to lose.
        var replace = true;
        if (Items.Count > 0)
        {
            var fresh = BookmarkMerge.NewOnes(Items.Select(i => i.Bookmark), imported);
            var choice = await _dialogs.ChooseAsync(
                title,
                LocalizationManager.Format("Bookmarks_ImportAsk", imported.Count, Path.GetFileName(file), Items.Count, fresh.Count),
                LocalizationManager.Get("Bookmarks_ImportMerge"),
                LocalizationManager.Get("Bookmarks_ImportReplace"),
                isWarning: true);

            if (choice == ConfirmChoice.Cancel)
            {
                return;
            }

            if (choice == ConfirmChoice.Confirm)
            {
                imported = fresh.ToList();
                replace = false;
            }
        }

        if (replace)
        {
            Items.Clear();
        }

        BookmarkItemViewModel? first = null;
        foreach (var b in imported)
        {
            var item = new BookmarkItemViewModel(b);
            Items.Add(item);
            first ??= item;
        }

        // The first imported row, so it is visible what arrived; a merge that brought nothing new
        // keeps the selection where it was.
        SelectedItem = first ?? SelectedItem ?? Items.FirstOrDefault();
    }

    [RelayCommand]
    private async Task ExportBookmarks()
    {
        var file = _dialogs.PickSaveFile(
            LocalizationManager.Get("Bookmarks_ExportTitle"),
            LocalizationManager.Get("Bookmarks_FileFilter"),
            "bookmarks.json");
        if (file is null)
        {
            return;
        }

        try
        {
            var list = Items.Select(i => i.Bookmark).ToList();
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(list, JsonOptions));
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync(
                LocalizationManager.Get("Bookmarks_ExportTitle"),
                string.Format(LocalizationManager.Get("Bookmarks_ExportFailed"), ex.Message));
        }
    }

    [RelayCommand]
    private void Save()
    {
        // A bookmark with no name or no usable path would be a dead menu entry; stop on the first
        // one and show what is wrong with it instead of writing it.
        var bad = Items.FirstOrDefault(
            i => BookmarkValidator.Validate(i.Bookmark.Name, i.Bookmark.Path) != BookmarkIssue.None);
        if (bad is not null)
        {
            SelectedItem = bad;
            return;
        }

        _settings.Bookmarks.Clear();
        foreach (var item in Items)
        {
            var b = item.Bookmark;
            _settings.Bookmarks.Add(new Bookmark
            {
                Name = b.Name.Trim(),
                Path = b.Path.Trim(),
                Group = b.Group.Trim(),
            });
        }

        _persist?.Invoke();
        BookmarksChanged?.Invoke(this, EventArgs.Empty);
        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    // ── Events ────────────────────────────────────────────────────────────────

    public event EventHandler<bool>? CloseRequested;
    public event EventHandler? BookmarksChanged;

    // ── Helpers ──────────────────────────────────────────────────────────────

    private bool CanMoveUp()   => SelectedItem is not null && Items.IndexOf(SelectedItem) > 0;
    private bool CanMoveDown() => SelectedItem is not null && Items.IndexOf(SelectedItem) < Items.Count - 1;
}

public partial class BookmarkItemViewModel : ObservableObject
{
    public BookmarkItemViewModel(Bookmark bookmark)
    {
        Bookmark     = bookmark;
        _displayName = bookmark.Name;
        _displayPath = bookmark.Path;
        _displayGroup = bookmark.Group;
    }

    public Bookmark Bookmark { get; }

    [ObservableProperty] private string _displayName;
    [ObservableProperty] private string _displayPath;
    [ObservableProperty] private string _displayGroup;

    public void Refresh()
    {
        DisplayName  = Bookmark.Name;
        DisplayPath  = Bookmark.Path;
        DisplayGroup = Bookmark.Group;
    }
}
