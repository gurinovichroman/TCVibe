using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Services;
using TCV.Core.Interfaces;

namespace TCV.App.ViewModels.Dialogs;

/// <summary>What the user chose to do with the drop stack.</summary>
public enum DropStackAction
{
    None,
    /// <summary>Copy the ticked entries into the active panel.</summary>
    CopyHere,
    /// <summary>Move the ticked entries into the active panel.</summary>
    MoveHere,
    /// <summary>Empty the stack without touching any file.</summary>
    Clear,
}

/// <summary>One row of the drop stack: an entry with a tick box.</summary>
public sealed partial class DropStackRowViewModel : ObservableObject
{
    public DropStackRowViewModel(IFileEntry entry)
    {
        Entry = entry;
        Path = entry.Path.Value;
        Name = entry.Name;
        Directory = System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
        IsDirectory = entry.IsDirectory;
    }

    public IFileEntry Entry { get; }
    public string Path { get; }
    public string Name { get; }
    public string Directory { get; }
    public bool IsDirectory { get; }

    [ObservableProperty] private bool _isChecked = true;
}

/// <summary>
/// Backs the drop-stack window (Ctrl+K Ctrl+D, task 6.24): what has been collected, and what to do
/// with it. The dialog decides; <c>MainViewModel</c> performs, through the operation queue.
/// </summary>
public sealed partial class DropStackDialogViewModel : ObservableObject
{
    private readonly DropStack _stack;

    public DropStackDialogViewModel(DropStack stack)
    {
        _stack = stack;
        foreach (var entry in stack.Snapshot()) Rows.Add(new DropStackRowViewModel(entry));
    }

    public ObservableCollection<DropStackRowViewModel> Rows { get; } = new();

    [ObservableProperty] private DropStackRowViewModel? _selected;

    /// <summary>What the user asked for; <see cref="DropStackAction.None"/> when they cancelled.</summary>
    public DropStackAction Action { get; private set; } = DropStackAction.None;

    /// <summary>The ticked entries, in stack order.</summary>
    public IReadOnlyList<IFileEntry> Chosen =>
        Rows.Where(r => r.IsChecked).Select(r => r.Entry).ToList();

    public bool Confirmed { get; private set; }

    public event EventHandler? CloseRequested;

    [RelayCommand] private void CopyHere() => Finish(DropStackAction.CopyHere);
    [RelayCommand] private void MoveHere() => Finish(DropStackAction.MoveHere);
    [RelayCommand] private void ClearStack() => Finish(DropStackAction.Clear);

    /// <summary>Drops the selected row from the stack without touching the file.</summary>
    [RelayCommand]
    private void RemoveSelected()
    {
        if (Selected is null) return;
        _stack.Remove(Selected.Entry);
        Rows.Remove(Selected);
        Selected = Rows.FirstOrDefault();
    }

    [RelayCommand]
    private void Cancel()
    {
        Action = DropStackAction.None;
        Confirmed = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Finish(DropStackAction action)
    {
        Action = action;
        Confirmed = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
