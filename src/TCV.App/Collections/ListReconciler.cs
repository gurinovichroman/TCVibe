using System.Collections.ObjectModel;

namespace TCV.App.Collections;

/// <summary>
/// Turns a bound list into a new one in place, item by item, instead of clearing and refilling it.
/// An item present in both lists stays the same object and only moves, so whatever the view keeps
/// for it survives: an expanded tree node keeps its subtree, the drive in the drive selector stays
/// selected. Items are compared by reference. Clearing a list bound two-way to <c>SelectedItem</c>
/// writes null back to the source (TRAP-56).
/// </summary>
public static class ListReconciler
{
    /// <summary>
    /// Makes <paramref name="current"/> hold exactly <paramref name="target"/>, in its order: items
    /// missing from the target are removed, new ones inserted, the rest moved. Returns true when
    /// anything changed. <paramref name="target"/> must not hold the same item twice.
    /// </summary>
    public static bool Reconcile<T>(IList<T> current, IReadOnlyList<T> target) where T : class
    {
        var changed = false;
        var wanted = new HashSet<T>(target, ReferenceEqualityComparer.Instance);
        for (var i = current.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(current[i]))
            {
                current.RemoveAt(i);
                changed = true;
            }
        }

        for (var i = 0; i < target.Count; i++)
        {
            if (i < current.Count && ReferenceEquals(current[i], target[i]))
            {
                continue;
            }

            changed = true;
            var at = IndexOf(current, target[i], i + 1);
            if (at < 0)
            {
                current.Insert(i, target[i]);
            }
            else if (current is ObservableCollection<T> observable)
            {
                observable.Move(at, i);   // one Move notification: the view keeps the container
            }
            else
            {
                var item = current[at];
                current.RemoveAt(at);
                current.Insert(i, item);
            }
        }

        return changed;
    }

    private static int IndexOf<T>(IList<T> list, T item, int start) where T : class
    {
        for (var i = start; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], item))
            {
                return i;
            }
        }

        return -1;
    }
}
