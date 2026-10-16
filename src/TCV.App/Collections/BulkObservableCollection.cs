using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace TCV.App.Collections;

/// <summary>
/// ObservableCollection with a <see cref="Reset"/> method that replaces all items
/// and fires a single <see cref="NotifyCollectionChangedAction.Reset"/> event,
/// instead of N individual Add events.
/// </summary>
public class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>
    /// Replaces the entire contents with <paramref name="newItems"/> and raises
    /// a single Reset notification so that bound controls do one full refresh.
    /// </summary>
    public void Reset(IEnumerable<T> newItems)
    {
        Items.Clear();
        foreach (var item in newItems)
            Items.Add(item);

        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
    }

    /// <summary>
    /// Replaces the entire contents only if the new list differs from the current one
    /// (compared by reference). Returns true if a reset was performed.
    /// Use this to avoid spurious redraws when the content hasn't actually changed.
    /// </summary>
    public bool ResetIfChanged(IList<T> newItems)
    {
        if (newItems.Count == Count)
        {
            bool same = true;
            for (int i = 0; i < newItems.Count; i++)
            {
                if (!ReferenceEquals(newItems[i], this[i])) { same = false; break; }
            }
            if (same) return false;
        }

        Reset(newItems);
        return true;
    }
}
