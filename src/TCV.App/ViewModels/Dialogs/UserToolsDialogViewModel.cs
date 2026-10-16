using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Localization;
using TCV.App.Settings;
using TCV.Tools;

namespace TCV.App.ViewModels.Dialogs;

/// <summary>
/// The user-tools manager. It edits <b>copies</b> of the tools and writes them back only on OK, so
/// Cancel really cancels and a half-typed name never reaches the menu or the hotkey table. Every
/// keystroke goes straight into the selected copy, so switching to another tool cannot lose an edit.
/// </summary>
public partial class UserToolsDialogViewModel : ObservableObject
{
    private readonly UserToolsPlugin _plugin;
    private readonly Func<KeyStroke, KeyStroke?, string?>? _ownerOfGesture;
    private bool _loading;

    /// <param name="plugin">Holds the persisted tool list.</param>
    /// <param name="ownerOfGesture">Optional: which command already owns a gesture (for a warning —
    /// a tool never takes a key a built-in command has, TRAP-21). Null skips the check.</param>
    public UserToolsDialogViewModel(UserToolsPlugin plugin, Func<KeyStroke, KeyStroke?, string?>? ownerOfGesture = null)
    {
        _plugin = plugin;
        _ownerOfGesture = ownerOfGesture;

        foreach (var tool in plugin.Tools)
        {
            ToolItems.Add(new UserToolItemViewModel(tool.Clone()));
        }

        SelectedItem = ToolItems.FirstOrDefault();
    }

    // ── Collections & selection ───────────────────────────────────────────────

    public ObservableCollection<UserToolItemViewModel> ToolItems { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    private UserToolItemViewModel? _selectedItem;

    public bool HasSelection => SelectedItem is not null;

    // ── Edit-form properties (bound to edit panel on the right) ───────────────

    [ObservableProperty] private string _editName = string.Empty;
    [ObservableProperty] private string _editCommand = string.Empty;
    [ObservableProperty] private string _editArguments = string.Empty;
    [ObservableProperty] private string _editWorkingDir = string.Empty;
    [ObservableProperty] private string _editHotkey = string.Empty;

    /// <summary>What is wrong with the tools right now (blocks OK), or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorText = string.Empty;

    /// <summary>A non-blocking remark about the selected tool (its key is taken), or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    private string _warningText = string.Empty;

    public bool HasError => ErrorText.Length > 0;
    public bool HasWarning => WarningText.Length > 0;

    partial void OnSelectedItemChanged(UserToolItemViewModel? value)
    {
        _loading = true;
        try
        {
            if (value is null)
            {
                EditName = EditCommand = EditArguments = EditWorkingDir = EditHotkey = string.Empty;
                return;
            }

            EditName = value.Tool.Name;
            EditCommand = value.Tool.Command;
            EditArguments = value.Tool.Arguments;
            EditWorkingDir = value.Tool.WorkingDir;
            EditHotkey = value.Tool.Hotkey ?? string.Empty;
        }
        finally
        {
            _loading = false;
            Validate();
        }
    }

    // Each field flows into the selected copy as it is typed.
    partial void OnEditNameChanged(string value) => Push(t => t.Name = value);
    partial void OnEditCommandChanged(string value) => Push(t => t.Command = value);
    partial void OnEditArgumentsChanged(string value) => Push(t => t.Arguments = value);
    partial void OnEditWorkingDirChanged(string value) => Push(t => t.WorkingDir = value);
    partial void OnEditHotkeyChanged(string value) =>
        Push(t => t.Hotkey = string.IsNullOrWhiteSpace(value) ? null : value.Trim());

    private void Push(Action<UserTool> apply)
    {
        if (_loading || SelectedItem is null)
        {
            return;
        }

        apply(SelectedItem.Tool);
        SelectedItem.RefreshDisplay();
        Validate();
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Add()
    {
        var tool = new UserTool { Name = UniqueName(LocalizationManager.Get("UserTools_NewName")) };
        var item = new UserToolItemViewModel(tool);
        ToolItems.Add(item);
        SelectedItem = item;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Remove()
    {
        if (SelectedItem is null)
        {
            return;
        }

        var idx = ToolItems.IndexOf(SelectedItem);
        ToolItems.Remove(SelectedItem);
        SelectedItem = ToolItems.Count > 0
            ? ToolItems[Math.Max(0, idx - 1)]
            : null;
        Validate();
    }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp()
    {
        if (SelectedItem is null)
        {
            return;
        }

        var idx = ToolItems.IndexOf(SelectedItem);
        if (idx <= 0)
        {
            return;
        }

        ToolItems.Move(idx, idx - 1);
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

        var idx = ToolItems.IndexOf(SelectedItem);
        if (idx < 0 || idx >= ToolItems.Count - 1)
        {
            return;
        }

        ToolItems.Move(idx, idx + 1);
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Set by the view: opens the file picker and returns the chosen path, or null.</summary>
    public Func<string?>? PickExecutable { get; set; }

    [RelayCommand]
    private void BrowseCommand()
    {
        var picked = PickExecutable?.Invoke();
        if (picked is not null)
        {
            EditCommand = picked;
        }
    }

    [RelayCommand]
    private void Save()
    {
        Validate();
        if (HasError)
        {
            return;
        }

        SyncToPlugin();
        _plugin.SaveTools();
        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    // ── Events ────────────────────────────────────────────────────────────────

    public event EventHandler<bool>? CloseRequested;

    // ── Validation ────────────────────────────────────────────────────────────

    private bool CanMoveUp() => SelectedItem is not null && ToolItems.IndexOf(SelectedItem) > 0;

    private bool CanMoveDown() => SelectedItem is not null &&
                                   ToolItems.IndexOf(SelectedItem) < ToolItems.Count - 1;

    /// <summary>
    /// Re-checks the whole list. The first problem is reported and blocks OK: an empty or duplicate
    /// name (the name is the tool's command id, TRAP-21), an empty command, a hotkey that does not
    /// parse or that two tools claim.
    /// </summary>
    public void Validate()
    {
        ErrorText = FindError() ?? string.Empty;
        WarningText = FindWarning() ?? string.Empty;
    }

    private string? FindError()
    {
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenGestures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in ToolItems)
        {
            var tool = item.Tool;
            var label = string.IsNullOrWhiteSpace(tool.Name) ? LocalizationManager.Get("UserTools_Unnamed") : tool.Name;

            if (string.IsNullOrWhiteSpace(tool.Name))
            {
                return LocalizationManager.Get("UserTools_ErrorNoName");
            }

            if (!seenNames.Add(tool.Name.Trim()))
            {
                return string.Format(LocalizationManager.Get("UserTools_ErrorDuplicate"), tool.Name);
            }

            if (string.IsNullOrWhiteSpace(tool.Command))
            {
                return string.Format(LocalizationManager.Get("UserTools_ErrorNoCommand"), label);
            }

            if (string.IsNullOrWhiteSpace(tool.Hotkey))
            {
                continue;
            }

            if (!TryParseGesture(tool.Hotkey!, out var first, out var second))
            {
                return string.Format(LocalizationManager.Get("UserTools_ErrorHotkey"), label, tool.Hotkey);
            }

            var normalized = second is null ? first.ToString() : $"{first} {second}";
            if (seenGestures.TryGetValue(normalized, out var other))
            {
                return string.Format(LocalizationManager.Get("UserTools_ErrorHotkeyTwice"), normalized, other, label);
            }

            seenGestures[normalized] = label;
        }

        return null;
    }

    private string? FindWarning()
    {
        if (SelectedItem is not { } item || _ownerOfGesture is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(item.Tool.Hotkey))
        {
            return null;
        }

        if (!TryParseGesture(item.Tool.Hotkey!, out var first, out var second))
        {
            return null;
        }

        var owner = _ownerOfGesture(first, second);
        return owner is null || owner.StartsWith("UserTool:", StringComparison.Ordinal)
            ? null
            : string.Format(LocalizationManager.Get("UserTools_WarnHotkeyTaken"), item.Tool.Hotkey, owner);
    }

    /// <summary>
    /// Parses "Ctrl+F1" or a chord "Ctrl+K Ctrl+T" (one or two strokes), the same forms the hotkey
    /// table accepts.
    /// </summary>
    public static bool TryParseGesture(string text, out KeyStroke first, out KeyStroke? second)
    {
        first = default;
        second = null;

        var strokes = text.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (strokes.Length is 0 or > 2)
        {
            return false;
        }

        if (!KeyStroke.TryParse(strokes[0], out first))
        {
            return false;
        }

        if (strokes.Length == 2)
        {
            if (!KeyStroke.TryParse(strokes[1], out var next))
            {
                return false;
            }

            second = next;
        }

        return true;
    }

    private string UniqueName(string baseName)
    {
        var name = baseName;
        for (var i = 2; ToolItems.Any(t => string.Equals(t.Tool.Name, name, StringComparison.OrdinalIgnoreCase)); i++)
        {
            name = $"{baseName} {i}";
        }

        return name;
    }

    private void SyncToPlugin()
    {
        _plugin.Tools.Clear();
        foreach (var item in ToolItems)
        {
            var tool = item.Tool;
            tool.Name = tool.Name.Trim();
            _plugin.Tools.Add(tool);
        }
    }
}

// ── Row view-model ────────────────────────────────────────────────────────────

public partial class UserToolItemViewModel : ObservableObject
{
    public UserToolItemViewModel(UserTool tool)
    {
        Tool = tool;
        _displayName = tool.Name;
        _displayCommand = tool.Command;
    }

    /// <summary>The working copy being edited (never the plugin's live object).</summary>
    public UserTool Tool { get; }

    [ObservableProperty] private string _displayName;
    [ObservableProperty] private string _displayCommand;

    public void RefreshDisplay()
    {
        DisplayName = string.IsNullOrWhiteSpace(Tool.Name) ? "…" : Tool.Name;
        DisplayCommand = Tool.Command;
    }
}
