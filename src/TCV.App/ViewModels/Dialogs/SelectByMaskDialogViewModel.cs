using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Localization;
using TCV.App.Selection;

namespace TCV.App.ViewModels.Dialogs;

/// <summary>One row of the template list: the template and the name it reads under.</summary>
public sealed record SelectionTemplateItem(SelectionTemplate Template, string Display)
{
    public override string ToString() => Display;
}

/// <summary>
/// Backs the "select / unselect by mask" dialog (TC's Num+ / Num-): a mask with its history, the
/// conditions a template can add (folders, age, Git, agent), and the templates themselves — pick one,
/// save the fields under a name, delete one.
/// </summary>
/// <remarks>
/// The template list is the live one from the settings: saving and deleting change it at once and call
/// <c>templatesChanged</c>, which persists it and rebinds the gestures. Cancel does not undo them —
/// they are explicit buttons, not part of the selection being asked for.
/// </remarks>
public partial class SelectByMaskDialogViewModel : ObservableObject
{
    private readonly List<SelectionTemplate> _templates;
    private readonly Action _templatesChanged;
    private bool _loadingTemplate;

    public SelectByMaskDialogViewModel(
        bool select,
        List<SelectionTemplate> templates,
        IReadOnlyList<string> history,
        bool includeFolders,
        Action templatesChanged,
        bool canEditTemplates = true)
    {
        _templates = templates;
        CanEditTemplates = canEditTemplates;
        _templatesChanged = templatesChanged;

        WindowTitle = LocalizationManager.Get(select ? "Mask_TitleSelect" : "Mask_TitleUnselect");
        Prompt = LocalizationManager.Get(select ? "Mask_PromptSelect" : "Mask_PromptUnselect");
        History = history;
        _mask = history.Count > 0 ? history[0] : "*.*";
        _includeFolders = includeFolders;

        AgeModes = [Loc("Sel_AgeAny"), Loc("Sel_AgeOlder"), Loc("Sel_AgeNewer")];
        AgeUnits = [Loc("Sel_UnitMinutes"), Loc("Sel_UnitHours"), Loc("Sel_UnitDays")];
        _ageUnitIndex = (int)SelectionAgeUnit.Days;

        RefreshTemplates(selected: null);
    }

    /// <summary>The name a template reads under: its own, or the dictionary's for a shipped one.</summary>
    public static string DisplayName(SelectionTemplate template) =>
        template.Name.Length > 0 ? template.Name : Loc("SelTpl_" + template.Builtin);

    public string WindowTitle { get; }

    /// <summary>False in a workspace window, which does not write <c>settings.json</c> (TRAP-22).</summary>
    public bool CanEditTemplates { get; }
    public string Prompt { get; }

    /// <summary>The masks typed before, newest first — the editable box's drop-down.</summary>
    public IReadOnlyList<string> History { get; }

    public ObservableCollection<SelectionTemplateItem> Templates { get; } = new();

    public IReadOnlyList<string> AgeModes { get; }
    public IReadOnlyList<string> AgeUnits { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand), nameof(SaveTemplateCommand))]
    private string _mask;

    [ObservableProperty]
    private bool _includeFolders;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAge))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand), nameof(SaveTemplateCommand))]
    private int _ageModeIndex;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand), nameof(SaveTemplateCommand))]
    private string _ageAmountText = "2";

    [ObservableProperty]
    private int _ageUnitIndex;

    [ObservableProperty]
    private bool _gitChanged;

    [ObservableProperty]
    private bool _agentChanged;

    /// <summary>The name the fields are saved under by «Save template».</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveTemplateCommand))]
    private string _templateName = string.Empty;

    /// <summary>The gesture the saved template gets (<c>Alt+Shift+1</c>); blank for none.</summary>
    [ObservableProperty]
    private string _templateHotkey = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteTemplateCommand))]
    private SelectionTemplateItem? _selectedTemplate;

    public bool HasAge => AgeModeIndex != (int)SelectionAge.Any;

    public bool DialogResult { get; private set; }
    public event EventHandler? CloseRequested;

    partial void OnSelectedTemplateChanged(SelectionTemplateItem? value)
    {
        if (value is null || _loadingTemplate)
        {
            return;
        }

        var t = value.Template;
        Mask = t.Mask;
        IncludeFolders = t.IncludeFolders;
        AgeModeIndex = (int)t.Age;
        AgeAmountText = t.AgeAmount.ToString(System.Globalization.CultureInfo.CurrentCulture);
        AgeUnitIndex = (int)t.AgeUnit;
        GitChanged = t.GitChanged;
        AgentChanged = t.AgentChanged;
        TemplateName = value.Display;
        TemplateHotkey = t.Hotkey ?? string.Empty;
    }

    /// <summary>What the fields describe, as a template without a name.</summary>
    public SelectionTemplate BuildTemplate() => new()
    {
        Mask = Mask.Trim(),
        IncludeFolders = IncludeFolders,
        Age = (SelectionAge)AgeModeIndex,
        AgeAmount = ParseAmount() ?? 0,
        AgeUnit = (SelectionAgeUnit)AgeUnitIndex,
        GitChanged = GitChanged,
        AgentChanged = AgentChanged,
    };

    [RelayCommand(CanExecute = nameof(IsValid))]
    private void Confirm()
    {
        DialogResult = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        DialogResult = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Saves the fields under <see cref="TemplateName"/>: a template that already reads under that name
    /// is updated in place (a shipped one keeps its id, and its dictionary name while the name is the
    /// dictionary's), any other name adds a template.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveTemplate))]
    private void SaveTemplate()
    {
        var name = TemplateName.Trim();
        var rule = BuildTemplate();
        var hotkey = string.IsNullOrWhiteSpace(TemplateHotkey) ? null : TemplateHotkey.Trim();

        var existing = _templates.FirstOrDefault(
            t => string.Equals(DisplayName(t), name, StringComparison.CurrentCultureIgnoreCase));
        if (existing is null)
        {
            existing = new SelectionTemplate { Name = name };
            _templates.Add(existing);
        }
        else if (existing.Builtin.Length == 0 || !string.Equals(DisplayName(existing), name, StringComparison.CurrentCulture))
        {
            existing.Name = name;
        }

        existing.Mask = rule.Mask;
        existing.IncludeFolders = rule.IncludeFolders;
        existing.Age = rule.Age;
        existing.AgeAmount = rule.AgeAmount;
        existing.AgeUnit = rule.AgeUnit;
        existing.GitChanged = rule.GitChanged;
        existing.AgentChanged = rule.AgentChanged;
        existing.Hotkey = hotkey;

        _templatesChanged();
        RefreshTemplates(existing);
    }

    [RelayCommand(CanExecute = nameof(CanDeleteTemplate))]
    private void DeleteTemplate()
    {
        if (SelectedTemplate is not { } item)
        {
            return;
        }

        _templates.Remove(item.Template);
        _templatesChanged();
        RefreshTemplates(selected: null);
    }

    private bool IsValid() =>
        !SelectionMask.Parse(Mask).IsEmpty && (!HasAge || ParseAmount() is > 0);

    private bool CanSaveTemplate() => CanEditTemplates && IsValid() && !string.IsNullOrWhiteSpace(TemplateName);

    private bool CanDeleteTemplate() => CanEditTemplates && SelectedTemplate is not null;

    private int? ParseAmount() =>
        int.TryParse(AgeAmountText?.Trim(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.CurrentCulture, out var n) ? n : null;

    private void RefreshTemplates(SelectionTemplate? selected)
    {
        _loadingTemplate = true;
        try
        {
            Templates.Clear();
            foreach (var t in _templates)
            {
                Templates.Add(new SelectionTemplateItem(t, DisplayName(t)));
            }

            SelectedTemplate = selected is null ? null : Templates.FirstOrDefault(i => ReferenceEquals(i.Template, selected));
        }
        finally
        {
            _loadingTemplate = false;
        }
    }

    private static string Loc(string key) => LocalizationManager.Get(key);
}
