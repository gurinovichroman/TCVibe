using System.Collections.ObjectModel;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Services;

/// <summary>
/// The operations whose progress window was sent to the background: the work goes on with the window
/// hidden, the status bar says how many there are and how far they have got, and a click there lists
/// them, each to be shown or cancelled; the command <c>BackgroundOperations</c> brings every window
/// back (ADR-0038). UI thread only.
/// </summary>
public sealed class BackgroundProgress
{
    private readonly ObservableCollection<ProgressDialogViewModel> _items = [];

    public BackgroundProgress() => Items = new ReadOnlyObservableCollection<ProgressDialogViewModel>(_items);

    /// <summary>An operation went to the background or came back from it, or ended there.</summary>
    public event EventHandler? Changed;

    /// <summary>A window was just sent to the background: the main window says where its progress went.</summary>
    public event EventHandler<ProgressDialogViewModel>? Hidden;

    /// <summary>
    /// An operation ended while its window was hidden. <see cref="ProgressDialogViewModel.IsCompleted"/>
    /// tells a finished one from a cancelled or failed one (those have their own error box, or none).
    /// </summary>
    public event EventHandler<ProgressDialogViewModel>? Ended;

    /// <summary>The hidden ones, in the order they were hidden; the status bar's list shows them.</summary>
    public ReadOnlyObservableCollection<ProgressDialogViewModel> Items { get; }

    /// <summary>How many operations run with their window hidden.</summary>
    public int Count => _items.Count;

    /// <summary>How far they have got together, in percent: the mean of their own percentages.</summary>
    public int Percent => _items.Count == 0 ? 0 : (int)Math.Round(_items.Average(p => Math.Clamp(p.Percentage, 0, 100)));

    /// <summary>Starts counting <paramref name="progress"/>, whose window was hidden. Once only.</summary>
    public void Add(ProgressDialogViewModel progress)
    {
        if (_items.Contains(progress))
        {
            return;
        }

        _items.Add(progress);
        Hidden?.Invoke(this, progress);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops counting <paramref name="progress"/>: its window is shown again.</summary>
    public void Remove(ProgressDialogViewModel progress)
    {
        if (_items.Remove(progress))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Stops counting <paramref name="progress"/>: its window closed, the operation is over.</summary>
    public void End(ProgressDialogViewModel progress)
    {
        if (_items.Remove(progress))
        {
            Ended?.Invoke(this, progress);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Shows every hidden progress window again. Returns how many there were.</summary>
    public int BringBackAll()
    {
        var hidden = _items.ToList();
        foreach (var progress in hidden)
        {
            progress.BringBack();
        }

        return hidden.Count;
    }
}
