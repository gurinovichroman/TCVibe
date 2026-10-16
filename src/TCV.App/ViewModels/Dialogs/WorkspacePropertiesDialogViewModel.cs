using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Localization;
using TCV.App.Services;
using TCV.App.Workspaces;

namespace TCV.App.ViewModels.Dialogs;

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
/// </summary>
public partial class WorkspacePropertiesDialogViewModel : ObservableObject
{
    private readonly HashSet<string> _otherNames;

    /// <param name="workspace">The workspace being edited; it is not modified here.</param>
    /// <param name="allNames">Every saved workspace name, so a rename cannot take one that exists.</param>
    /// <param name="groups">Groups already in use, offered in the drop-down.</param>
    /// <param name="isNew">The workspace does not exist yet: the window creates it rather than edits it.</param>
    public WorkspacePropertiesDialogViewModel(
        Workspace workspace, IEnumerable<string> allNames, IEnumerable<string> groups, bool isNew = false)
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
        SyncSwatches();
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

    private bool CanConfirm() => NameError.Length == 0 && ColorError.Length == 0;

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        WorkspaceColor.TryNormalize(ColorText, out var color);
        Result = new WorkspacePropertiesResult(
            (Name ?? string.Empty).Trim(),
            (Icon ?? string.Empty).Trim(),
            color,
            (Group ?? string.Empty).Trim(),
            Pinned);
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
