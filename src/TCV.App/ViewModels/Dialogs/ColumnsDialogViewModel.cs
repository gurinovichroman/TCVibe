using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Columns;
using TCV.App.Localization;
using TCV.App.Settings;

namespace TCV.App.ViewModels.Dialogs;

/// <summary>One row of the column chooser: a catalog column with its on/off state and width.</summary>
public sealed partial class ColumnRowViewModel : ObservableObject
{
    private readonly ColumnDescriptor _descriptor;

    public ColumnRowViewModel(ColumnDescriptor descriptor, ColumnLayout layout)
    {
        _descriptor = descriptor;
        Id = descriptor.Id;
        _visible = layout.Visible;
        _width = layout.Width > 0 ? layout.Width : descriptor.DefaultWidth;
    }

    public string Id { get; }

    public string Title => _descriptor.Title(LocalizationManager.CurrentLanguage);

    /// <summary>True for LOC and size-on-disk, which are read per file rather than listed.</summary>
    public bool IsComputed => _descriptor.IsComputed;

    /// <summary>The name column carries the icon and is never optional.</summary>
    public bool CanHide => !string.Equals(Id, ColumnCatalog.Name, StringComparison.Ordinal);

    /// <summary>The name column is star-sized, so there is no width to edit for it.</summary>
    public bool HasWidth => _descriptor.DefaultWidth > 0;

    [ObservableProperty] private bool _visible;
    [ObservableProperty] private double _width;

    public ColumnLayout ToLayout() => new() { Id = Id, Width = Width, Visible = Visible };
}

/// <summary>
/// Backs the column chooser (Ctrl+K Ctrl+C): which columns the details view shows, in what order
/// and how wide (6.19). The same layout the header context menu edits, with reordering added.
/// </summary>
public sealed partial class ColumnsDialogViewModel : ObservableObject
{
    public ColumnsDialogViewModel(IReadOnlyList<ColumnLayout> layout)
    {
        Load(layout);
    }

    public ObservableCollection<ColumnRowViewModel> Rows { get; } = new();

    [ObservableProperty] private ColumnRowViewModel? _selected;

    public bool Confirmed { get; private set; }

    /// <summary>The edited layout, in the order shown.</summary>
    public List<ColumnLayout> Result => Rows.Select(r => r.ToLayout()).ToList();

    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void MoveUp() => Move(-1);

    [RelayCommand]
    private void MoveDown() => Move(1);

    [RelayCommand]
    private void Reset() => Load(ColumnCatalog.Normalize(ColumnCatalog.DefaultLayout()));

    [RelayCommand]
    private void Confirm()
    {
        Confirmed = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void Load(IReadOnlyList<ColumnLayout> layout)
    {
        Rows.Clear();
        foreach (var entry in ColumnCatalog.Normalize(layout.ToList()))
        {
            var descriptor = ColumnCatalog.Find(entry.Id);
            if (descriptor is not null) Rows.Add(new ColumnRowViewModel(descriptor, entry));
        }
        Selected = Rows.FirstOrDefault();
    }

    private void Move(int delta)
    {
        if (Selected is null) return;
        var index = Rows.IndexOf(Selected);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Rows.Count) return;

        Rows.Move(index, target);
        Selected = Rows[target];
    }
}
