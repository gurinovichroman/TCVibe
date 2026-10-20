using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Services;

/// <summary>
/// The operations whose progress window was sent to the background: the work goes on with the window
/// hidden, the status bar says how many there are and how far they have got, and a click there — or the
/// command <c>BackgroundOperations</c> — brings the windows back (ADR-0038). UI thread only.
/// </summary>
public sealed class BackgroundProgress
{
    private readonly List<ProgressDialogViewModel> _items = [];

    /// <summary>An operation went to the background or came back from it, or ended there.</summary>
    public event EventHandler? Changed;

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
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops counting <paramref name="progress"/>: its window is shown again, or the operation ended.</summary>
    public void Remove(ProgressDialogViewModel progress)
    {
        if (_items.Remove(progress))
        {
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
