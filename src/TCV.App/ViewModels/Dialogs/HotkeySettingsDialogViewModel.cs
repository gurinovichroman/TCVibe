using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TCV.App.Commands;
using TCV.App.Localization;
using TCV.App.Settings;

namespace TCV.App.ViewModels.Dialogs;

/// <summary>
/// Edits the keyboard shortcuts. Everything happens on a copy: nothing reaches
/// <see cref="AppSettings.Hotkeys"/> or the live <see cref="HotkeyManager"/> until Save, so Cancel
/// (or closing the window) really discards every edit, reset and import.
/// </summary>
public partial class HotkeySettingsDialogViewModel : ObservableObject
{
    private readonly HotkeyManager _hotkeyManager;
    private readonly AppSettings _settings;
    private readonly SettingsSerializer _serializer;
    private readonly Func<string?>? _pickExportPath;
    private readonly Func<string?>? _pickImportPath;

    public HotkeySettingsDialogViewModel(
        HotkeyManager hotkeyManager,
        AppSettings settings,
        SettingsSerializer serializer,
        Func<string?>? pickExportPath = null,
        Func<string?>? pickImportPath = null)
    {
        _hotkeyManager = hotkeyManager;
        _settings = settings;
        _serializer = serializer;
        _pickExportPath = pickExportPath ?? PickExportPathWithDialog;
        _pickImportPath = pickImportPath ?? PickImportPathWithDialog;
        LoadBindings();
    }

    public ObservableCollection<HotkeyBindingViewModel> Bindings { get; } = new();

    [ObservableProperty] private HotkeyBindingViewModel? _selectedBinding;
    [ObservableProperty] private string _searchText = string.Empty;

    /// <summary>Feedback on the last Apply / Import: an error in red, a confirmation in the normal colour.</summary>
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private bool _hasError;

    /// <summary>The gesture being edited, in the same text form settings.json uses: <c>Ctrl+K Ctrl+S</c>.</summary>
    [ObservableProperty] private string _editGesture = string.Empty;

    /// <summary>True in a guest window (<c>--workspace</c>): changes last for the session only (TRAP-22).</summary>
    public bool IsGuestWindow => _serializer.ReadOnly;

    /// <summary>Raised after a successful save; the view closes on it.</summary>
    public event EventHandler? CloseRequested;

    [ObservableProperty] private bool _savedSuccessfully;

    partial void OnSearchTextChanged(string value) => ApplyFilter(value);

    partial void OnSelectedBindingChanged(HotkeyBindingViewModel? value)
    {
        if (value is null)
        {
            return;
        }

        EditGesture = value.DisplayText;
        Message = string.Empty;
        HasError = false;
    }

    /// <summary>
    /// Turns a key press in the "record" box into gesture text: modifiers and key, the way the
    /// gesture is stored. Typing a name by hand ("OemPlus", "Return") is no way to pick a shortcut.
    /// </summary>
    public void RecordKeyPress(Key key, ModifierKeys modifiers)
    {
        // A lone modifier press is not a gesture yet: wait for the key that goes with it.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System or Key.None)
        {
            return;
        }

        EditGesture = new KeyStroke(key, modifiers).ToString();
    }

    [RelayCommand]
    private void ApplyHotkey() => TryApplyEditedGesture();

    /// <summary>Applies <see cref="EditGesture"/> to the selected row; false (with the reason shown) when it cannot.</summary>
    private bool TryApplyEditedGesture()
    {
        if (SelectedBinding is null)
        {
            return false;
        }

        var text = EditGesture.Trim();
        if (text.Length == 0)
        {
            Fail(LocalizationManager.Get("Hotkeys_NoKey"));
            return false;
        }

        if (!HotkeyManager.TryParseBinding(SelectedBinding.CommandId, text, out var parsed) || parsed is null)
        {
            Fail(string.Format(LocalizationManager.Get("Hotkeys_BadKey"), text));
            return false;
        }

        if (FindConflict(SelectedBinding, parsed) is { } conflict)
        {
            Fail(string.Format(LocalizationManager.Get("Hotkeys_Conflict"), conflict.Title));
            return false;
        }

        SelectedBinding.SetGesture(parsed);

        // Only gestures that differ from the default are persisted as overrides
        var def = GetDefaultBinding(SelectedBinding.CommandId);
        SelectedBinding.IsModified = def is null || def.ToString() != parsed.ToString();
        HasError = false;
        Message = string.Format(LocalizationManager.Get("Hotkeys_Applied"), SelectedBinding.DisplayText);
        return true;
    }

    /// <summary>
    /// True when the gesture box holds something other than what the selected row has — typed or
    /// recorded, but not applied yet. Save applies it rather than dropping it without a word.
    /// </summary>
    private bool HasPendingGesture()
    {
        if (SelectedBinding is null)
        {
            return false;
        }

        var text = EditGesture.Trim();
        if (text.Length == 0)
        {
            return false;   // a cleared box is not a request: a command cannot be left without a key
        }

        return !HotkeyManager.TryParseBinding(SelectedBinding.CommandId, text, out var parsed)
               || parsed is null
               || parsed.ToString() != SelectedBinding.DisplayText;
    }

    /// <summary>
    /// The binding that would clash with <paramref name="candidate"/>: the same gesture, or a
    /// single-stroke gesture that is also the first stroke of a chord (or the reverse) — the
    /// dispatcher would hold the key for a second stroke and the single command could never fire.
    /// </summary>
    private HotkeyBindingViewModel? FindConflict(HotkeyBindingViewModel editing, HotkeyBinding candidate)
    {
        foreach (var other in Bindings)
        {
            if (!ReferenceEquals(other, editing) && other.Binding is { } binding && Clashes(candidate, binding))
            {
                return other;
            }
        }

        return null;
    }

    /// <summary>Same gesture, or a single stroke that is also the first stroke of a chord.</summary>
    private static bool Clashes(HotkeyBinding a, HotkeyBinding b)
    {
        if (a.ToString() == b.ToString())
        {
            return true;
        }

        if (a.IsChord == b.IsChord)
        {
            return false;
        }

        var single = a.IsChord ? b : a;
        var chord = a.IsChord ? a : b;
        return single.First.Equals(chord.First);
    }

    private void Fail(string text)
    {
        Message = text;
        HasError = true;
    }

    [RelayCommand]
    private void ResetHotkey()
    {
        if (SelectedBinding is null)
        {
            return;
        }

        var defaultBinding = GetDefaultBinding(SelectedBinding.CommandId);
        if (defaultBinding is not null)
        {
            // The default may have been given to another command meanwhile; resetting over it
            // would leave two commands on one key and one of them unreachable.
            if (FindConflict(SelectedBinding, defaultBinding) is { } conflict)
            {
                Fail(string.Format(LocalizationManager.Get("Hotkeys_Conflict"), conflict.Title));
                return;
            }

            SelectedBinding.SetGesture(defaultBinding);
        }
        else
        {
            SelectedBinding.ClearGesture();   // its default is "no key"
        }

        SelectedBinding.IsModified = false;
        EditGesture = SelectedBinding.DisplayText;
        Message = string.Empty;
        HasError = false;
    }

    [RelayCommand]
    private void ResetAll()
    {
        // On the copy only: the saved overrides go when Save is pressed, not before.
        foreach (var b in Bindings)
        {
            if (GetDefaultBinding(b.CommandId) is { } def)
            {
                b.SetGesture(def);
            }
            else
            {
                b.ClearGesture();
            }

            b.IsModified = false;
        }

        if (SelectedBinding is not null)
        {
            EditGesture = SelectedBinding.DisplayText;
        }

        Message = string.Empty;
        HasError = false;
    }

    [RelayCommand]
    private void Save()
    {
        // A gesture typed or recorded but not applied would otherwise be dropped without a word.
        if (HasPendingGesture() && !TryApplyEditedGesture())
        {
            return;
        }

        var hotkeys = new Dictionary<string, string>();
        foreach (var b in Bindings)
        {
            if (b.IsModified)
            {
                hotkeys[b.CommandId] = b.DisplayText;
            }
        }

        // Written from a copy: when the write fails the dialog stays open, and a Cancel after that
        // must leave the live settings as they were rather than let the next save persist them.
        var draft = SettingsCopy.Of(_settings);
        draft.Hotkeys = hotkeys;

        // A guest window keeps the shortcuts for its own session; the serializer refuses to write.
        if (!_serializer.Save(draft) && !_serializer.ReadOnly)
        {
            Fail(string.Format(LocalizationManager.Get("Settings_SaveFailed"), _serializer.LastSaveError));
            return;
        }

        _settings.Hotkeys = hotkeys;
        _hotkeyManager.Reload();   // new gestures take effect without a restart
        SavedSuccessfully = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ExportProfile()
    {
        var path = _pickExportPath?.Invoke();
        if (path is null)
        {
            return;
        }

        try
        {
            // Rows without a key are listed so they can get one; they have nothing to export.
            var profile = Bindings
                .Where(b => b.Binding is not null)
                .ToDictionary(b => b.CommandId, b => b.DisplayText);
            File.WriteAllText(path, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail(string.Format(LocalizationManager.Get("Hotkeys_ExportError"), ex.Message));
        }
    }

    [RelayCommand]
    private void ImportProfile()
    {
        var path = _pickImportPath?.Invoke();
        if (path is null)
        {
            return;
        }

        ImportFrom(path);
    }

    /// <summary>Reads a profile file into the working copy. Unknown commands and unparsable gestures are skipped.</summary>
    public void ImportFrom(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var profile = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (profile is null)
            {
                return;
            }

            var incoming = new Dictionary<HotkeyBindingViewModel, HotkeyBinding>();
            foreach (var (commandId, text) in profile)
            {
                var target = Bindings.FirstOrDefault(b => string.Equals(b.CommandId, commandId, StringComparison.OrdinalIgnoreCase));
                if (target is null || !HotkeyManager.TryParseBinding(target.CommandId, text, out var parsed) || parsed is null)
                {
                    continue;
                }

                incoming[target] = parsed;
            }

            // A profile from another machine or version may put two commands on one key, with each
            // other or with a row it does not mention. Those rows keep their current gesture.
            var skipped = DropClashingImports(incoming);

            foreach (var (target, parsed) in incoming)
            {
                target.SetGesture(parsed);
                var def = GetDefaultBinding(target.CommandId);
                target.IsModified = def is null || def.ToString() != parsed.ToString();
            }

            if (SelectedBinding is not null)
            {
                EditGesture = SelectedBinding.DisplayText;
            }

            if (skipped > 0)
            {
                Fail(string.Format(LocalizationManager.Get("Hotkeys_ImportedSkipped"), incoming.Count, skipped));
                return;
            }

            HasError = false;
            Message = string.Format(LocalizationManager.Get("Hotkeys_Imported"), incoming.Count);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Fail(string.Format(LocalizationManager.Get("Hotkeys_ImportError"), ex.Message));
        }
    }

    /// <summary>
    /// Removes imported gestures that would clash in the resulting set. Repeats until stable: a row
    /// that falls back to its current gesture can clash with another imported one in turn.
    /// </summary>
    private int DropClashingImports(Dictionary<HotkeyBindingViewModel, HotkeyBinding> incoming)
    {
        var skipped = 0;
        bool changed;
        do
        {
            changed = false;
            foreach (var (row, gesture) in incoming.ToList())
            {
                var clash = Bindings.Any(other =>
                    !ReferenceEquals(other, row)
                    && (incoming.TryGetValue(other, out var planned) ? planned : other.Binding) is { } binding
                    && Clashes(gesture, binding));
                if (clash)
                {
                    incoming.Remove(row);
                    skipped++;
                    changed = true;
                }
            }
        }
        while (changed);

        return skipped;
    }

    private void LoadBindings()
    {
        var titles = CommandCatalog.All.ToDictionary(c => c.Id, c => c.Title(LocalizationManager.CurrentLanguage), StringComparer.OrdinalIgnoreCase);

        // An override saved under a merged id ("WorkspaceRename") belongs to the command it moved to:
        // read as unmodified, Save would rebuild the overrides without it and silently drop the key.
        var overridden = _settings.Hotkeys.Keys
            .Select(HotkeyManager.CanonicalCommandId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Bindings.Clear();
        foreach (var (commandId, binding) in _hotkeyManager.AllBindings.OrderBy(b => b.Key, StringComparer.OrdinalIgnoreCase))
        {
            var vm = new HotkeyBindingViewModel
            {
                CommandId = commandId,
                Title = titles.GetValueOrDefault(commandId, commandId),
                IsModified = overridden.Contains(commandId),
            };
            vm.SetGesture(binding);
            Bindings.Add(vm);
        }

        // Commands that ship without a key (pin a workspace, duplicate a tab, checksums…) are listed
        // too, with an empty gesture: listing only bound commands meant these could never get one.
        foreach (var command in CommandCatalog.All
                     .Where(c => !_hotkeyManager.AllBindings.ContainsKey(c.Id))
                     .OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase))
        {
            Bindings.Add(new HotkeyBindingViewModel
            {
                CommandId = command.Id,
                Title = command.Title(LocalizationManager.CurrentLanguage),
            });
        }
    }

    private void ApplyFilter(string text)
    {
        foreach (var b in Bindings)
        {
            b.IsVisible = string.IsNullOrEmpty(text) ||
                          b.Title.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                          b.CommandId.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                          b.DisplayText.Contains(text, StringComparison.OrdinalIgnoreCase);
        }
    }

    // Defaults never change at runtime, so build the throwaway manager once instead of on
    // every keystroke assigned in the dialog.
    private static readonly Lazy<HotkeyManager> _defaultBindings =
        new(() => new HotkeyManager(new AppSettings()));

    private HotkeyBinding? GetDefaultBinding(string commandId) =>
        _defaultBindings.Value.GetBinding(commandId);

    private static string? PickExportPathWithDialog()
    {
        var dlg = new SaveFileDialog
        {
            Title = LocalizationManager.Get("Hotkeys_ExportTitle"),
            Filter = "JSON (*.json)|*.json",
            FileName = "hotkeys-profile.json",
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private static string? PickImportPathWithDialog()
    {
        var dlg = new OpenFileDialog
        {
            Title = LocalizationManager.Get("Hotkeys_ImportTitle"),
            Filter = "JSON (*.json)|*.json",
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }
}

public sealed class HotkeyBindingViewModel : ObservableObject
{
    private string _displayText = string.Empty;
    private bool _isModified;
    private bool _isVisible = true;

    public string CommandId { get; set; } = string.Empty;

    /// <summary>What the command is called in the palette (in the current language); the id if it has no palette entry.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The gesture this row currently holds (edited copy, not yet saved).</summary>
    public HotkeyBinding? Binding { get; private set; }

    public string DisplayText
    {
        get => _displayText;
        private set => SetProperty(ref _displayText, value);
    }

    /// <summary>Sets the gesture and its text together, so the two cannot drift apart.</summary>
    public void SetGesture(HotkeyBinding binding)
    {
        Binding = new HotkeyBinding(CommandId, binding.First, binding.Second);
        DisplayText = binding.ToString();
    }

    /// <summary>Back to no key at all — the default of a command that ships without one.</summary>
    public void ClearGesture()
    {
        Binding = null;
        DisplayText = string.Empty;
    }

    public bool IsModified
    {
        get => _isModified;
        set => SetProperty(ref _isModified, value);
    }

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }
}
