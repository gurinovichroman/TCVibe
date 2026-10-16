using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Localization;
using TCV.App.Operations;
using TCV.App.Services;

namespace TCV.App.ViewModels.Dialogs;

/// <summary>
/// The saved-operations manager (6.50): add, edit, rename and delete what the Ctrl+K Ctrl+O picker
/// runs. Until then an operation could only be created — the first one from the picker, the rest
/// from the operation log — and changed or removed only by editing <c>saved-operations.json</c>.
/// </summary>
/// <remarks>
/// Like the user-tools manager it edits <b>copies</b> and writes only on OK, so Cancel really
/// cancels. OK writes entry by entry: <see cref="SavedOperationStore.Delete"/> for what was removed
/// or renamed, <see cref="SavedOperationStore.Save"/> for what was added or changed, nothing for the
/// rest. Rewriting the whole list instead would also drop an operation another window saved to the
/// same file while this one was open.
/// </remarks>
public sealed partial class SavedOperationsDialogViewModel : ObservableObject
{
    private readonly SavedOperationStore _store;
    private readonly List<SavedOperation> _removed = new();
    private bool _loading;

    /// <param name="store">Where the operations live.</param>
    /// <param name="draft">An operation to start with, already added and selected — what the picker
    /// offers when there is nothing saved yet. It is written only if the user presses OK.</param>
    public SavedOperationsDialogViewModel(SavedOperationStore store, SavedOperation? draft = null)
    {
        _store = store;
        foreach (var operation in store.LoadAll())
        {
            Items.Add(new SavedOperationItemViewModel(operation, isNew: false));
        }

        if (draft is not null)
        {
            var copy = draft.Clone();
            copy.Name = UniqueName(string.IsNullOrWhiteSpace(draft.Name)
                ? LocalizationManager.Get("SavedOp_NewName")
                : draft.Name.Trim());
            var item = new SavedOperationItemViewModel(copy, isNew: true);
            Items.Add(item);
            SelectedItem = item;
        }
        else
        {
            SelectedItem = Items.FirstOrDefault();
        }

        Validate();
    }

    // ── List and selection ────────────────────────────────────────────────────

    public ObservableCollection<SavedOperationItemViewModel> Items { get; } = new();

    /// <summary>The kinds offered in the form, named in the user's language.</summary>
    public IReadOnlyList<SavedOperationKindOption> KindOptions { get; } =
        Enum.GetValues<SavedOperationKind>()
            .Select(k => new SavedOperationKindOption(k, SavedOperationText.KindLabel(k)))
            .ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    private SavedOperationItemViewModel? _selectedItem;

    public bool HasSelection => SelectedItem is not null;

    /// <summary>True when OK wrote the changes; the caller has nothing else to do.</summary>
    public bool Saved { get; private set; }

    // ── The form (bound to the selected copy) ─────────────────────────────────

    [ObservableProperty] private string _editName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesMask))]
    [NotifyPropertyChangedFor(nameof(UsesTarget))]
    [NotifyPropertyChangedFor(nameof(UsesCreateTarget))]
    [NotifyPropertyChangedFor(nameof(ConfirmIsOptional))]
    private SavedOperationKind _editKind = SavedOperationKind.Copy;

    [ObservableProperty] private string _editSourceMask = string.Empty;
    [ObservableProperty] private string _editTarget = string.Empty;
    [ObservableProperty] private bool _editCreateTarget = true;
    [ObservableProperty] private bool _editConfirm = true;

    /// <summary>A new folder takes no files from the panel.</summary>
    public bool UsesMask => EditKind != SavedOperationKind.CreateDirectory;

    /// <summary>A delete has nowhere to go.</summary>
    public bool UsesTarget => EditKind != SavedOperationKind.Delete;

    /// <summary>Only a copy or a move sends files into a folder that may need creating first.</summary>
    public bool UsesCreateTarget => EditKind is SavedOperationKind.Copy or SavedOperationKind.Move;

    /// <summary>Moving and deleting always ask before running (INV-11); only the others may skip it.</summary>
    public bool ConfirmIsOptional => !IsDestructive(EditKind);

    /// <summary>The first problem with the list (blocks OK), or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorText = string.Empty;

    public bool HasError => ErrorText.Length > 0;

    partial void OnSelectedItemChanged(SavedOperationItemViewModel? value)
    {
        _loading = true;
        try
        {
            var operation = value?.Operation;
            EditName = operation?.Name ?? string.Empty;
            EditKind = operation?.Kind ?? SavedOperationKind.Copy;
            EditSourceMask = operation?.SourceMask ?? string.Empty;
            EditTarget = operation?.Target ?? string.Empty;
            EditCreateTarget = operation?.CreateTarget ?? true;
            EditConfirm = operation?.Confirm ?? true;
        }
        finally
        {
            _loading = false;
        }
    }

    // Every keystroke goes into the selected copy, so switching rows cannot lose an edit.
    partial void OnEditNameChanged(string value) => Push(o => o.Name = value);
    partial void OnEditSourceMaskChanged(string value) => Push(o => o.SourceMask = value);
    partial void OnEditTargetChanged(string value) => Push(o => o.Target = value);
    partial void OnEditCreateTargetChanged(bool value) => Push(o => o.CreateTarget = value);
    partial void OnEditConfirmChanged(bool value) => Push(o => o.Confirm = value);

    partial void OnEditKindChanged(SavedOperationKind value)
    {
        Push(o => o.Kind = value);

        // A destructive kind is confirmed whatever the box says; show it ticked rather than lie.
        if (!_loading && IsDestructive(value))
        {
            EditConfirm = true;
        }
    }

    private void Push(Action<SavedOperation> apply)
    {
        if (_loading || SelectedItem is null)
        {
            return;
        }

        apply(SelectedItem.Operation);
        SelectedItem.Refresh();
        Validate();
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Add()
    {
        // Copy the marked files into the other panel's folder: the most common shape, and a valid
        // operation as it stands, so the list never starts out broken.
        var item = new SavedOperationItemViewModel(new SavedOperation
        {
            Name = UniqueName(LocalizationManager.Get("SavedOp_NewName")),
            Kind = SavedOperationKind.Copy,
            Target = "%O",
        }, isNew: true);
        Items.Add(item);
        SelectedItem = item;
        Validate();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Remove()
    {
        if (SelectedItem is not { } item)
        {
            return;
        }

        if (item.Original is { } original)
        {
            _removed.Add(original);
        }

        var index = Items.IndexOf(item);
        Items.Remove(item);
        SelectedItem = Items.Count > 0 ? Items[Math.Max(0, index - 1)] : null;
        Validate();
    }

    [RelayCommand]
    private void Save()
    {
        Validate();
        if (HasError)
        {
            return;
        }

        if (!Apply())
        {
            ErrorText = LocalizationManager.Get("SavedOp_WriteFailed");
            return;
        }

        Saved = true;
        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    /// <summary>Raised when the dialog should close; true after a successful save (TRAP-39).</summary>
    public event EventHandler<bool>? CloseRequested;

    // ── Writing ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes what changed. Removals and the old names of renamed entries go first, so a name freed
    /// in this session can be taken by another entry. Returns false when a write failed.
    /// </summary>
    private bool Apply()
    {
        var toSave = Items.Where(i => i.IsChanged).Select(i => Finished(i.Operation)).ToList();
        var savedNames = toSave.Select(o => o.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A name that is saved again needs no delete: Save replaces it by name anyway.
        var toDelete = _removed.Select(o => o.Name)
            .Concat(Items.Where(i => i.Original is { } o && !string.Equals(o.Name, i.Operation.Name, StringComparison.Ordinal))
                .Select(i => i.Original!.Name))
            .Where(name => !savedNames.Contains(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var name in toDelete)
        {
            // False also means "already gone" (another window removed it), so the result is checked
            // below by reading the file back rather than here.
            _store.Delete(name);
        }

        var ok = true;
        foreach (var operation in toSave)
        {
            ok &= _store.Save(operation);
        }

        if (toDelete.Count > 0)
        {
            var left = _store.LoadAll().Select(o => o.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            ok &= !toDelete.Any(left.Contains);
        }

        return ok;
    }

    private static SavedOperation Finished(SavedOperation operation)
    {
        var copy = operation.Clone();
        copy.Name = copy.Name.Trim();
        copy.SourceMask = copy.SourceMask.Trim();
        copy.Target = copy.Target.Trim();
        if (copy.IsDestructive)
        {
            copy.Confirm = true;
        }

        return copy;
    }

    // ── Validation ────────────────────────────────────────────────────────────

    /// <summary>
    /// Re-checks the whole list; the first problem blocks OK. The name is how the picker and the
    /// store find an operation, so it must be there and unique. A new folder needs a name for the
    /// folder: an empty target would mean the other panel's folder, which always exists.
    /// </summary>
    public void Validate() => ErrorText = FindError() ?? string.Empty;

    private string? FindError()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var operation in Items.Select(i => i.Operation))
        {
            var name = operation.Name.Trim();
            if (name.Length == 0)
            {
                return LocalizationManager.Get("SavedOp_ErrorNoName");
            }

            if (!seen.Add(name))
            {
                return string.Format(LocalizationManager.Get("SavedOp_ErrorDuplicate"), name);
            }

            if (operation.Kind == SavedOperationKind.CreateDirectory && string.IsNullOrWhiteSpace(operation.Target))
            {
                return string.Format(LocalizationManager.Get("SavedOp_ErrorNoFolder"), name);
            }
        }

        return null;
    }

    private static bool IsDestructive(SavedOperationKind kind) =>
        kind is SavedOperationKind.Move or SavedOperationKind.Delete;

    private string UniqueName(string baseName)
    {
        var name = baseName;
        for (var i = 2; Items.Any(t => string.Equals(t.Operation.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)); i++)
        {
            name = $"{baseName} {i}";
        }

        return name;
    }
}

/// <summary>One row of the manager: the stored operation and the copy being edited.</summary>
public sealed partial class SavedOperationItemViewModel : ObservableObject
{
    internal SavedOperationItemViewModel(SavedOperation operation, bool isNew)
    {
        Original = isNew ? null : operation;
        Operation = operation.Clone();
        Refresh();
    }

    /// <summary>The operation as it was loaded, or null for one added in this session.</summary>
    public SavedOperation? Original { get; }

    /// <summary>The working copy the form edits.</summary>
    public SavedOperation Operation { get; }

    /// <summary>True when OK has to write this entry.</summary>
    public bool IsChanged => Original is null || !Original.DefinesSameAs(Operation);

    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string _description = string.Empty;

    public void Refresh()
    {
        DisplayName = string.IsNullOrWhiteSpace(Operation.Name) ? "…" : Operation.Name;
        Description = SavedOperationText.Describe(Operation);
    }
}

/// <summary>An entry of the "action" picker.</summary>
public sealed record SavedOperationKindOption(SavedOperationKind Kind, string Label);
