using System.Collections.ObjectModel;
using TCV.Core.Interfaces;

namespace TCV.App.Services;

/// <summary>
/// A basket of files gathered across several directories, to be copied or moved somewhere in one
/// go (6.24). Total Commander calls it the drop stack; the point is that a selection normally dies
/// the moment you navigate away, and collecting from five folders otherwise means five operations.
/// </summary>
/// <remarks>
/// Session-scoped on purpose — nothing is persisted. A stack that survived a restart would quietly
/// accumulate paths that no longer exist, and the whole value of the feature is that it is a
/// short-lived scratch list.
/// </remarks>
public sealed class DropStack
{
    private readonly ObservableCollection<IFileEntry> _entries = new();

    public DropStack() => Entries = new ReadOnlyObservableCollection<IFileEntry>(_entries);

    /// <summary>What is currently on the stack, in the order it was added.</summary>
    public ReadOnlyObservableCollection<IFileEntry> Entries { get; }

    public int Count => _entries.Count;

    public bool IsEmpty => _entries.Count == 0;

    /// <summary>Raised whenever the contents change, so the status bar can show the count.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Adds entries, skipping ones already on the stack. Returns how many were actually added, so
    /// the caller can say "3 added, 2 already there" instead of a bare confirmation.
    /// </summary>
    public int Add(IEnumerable<IFileEntry> entries)
    {
        var added = 0;
        foreach (var entry in entries)
        {
            if (entry is null) continue;
            if (_entries.Any(e => PathEquals(e, entry))) continue;
            _entries.Add(entry);
            added++;
        }

        if (added > 0) Changed?.Invoke(this, EventArgs.Empty);
        return added;
    }

    public bool Remove(IFileEntry entry)
    {
        var existing = _entries.FirstOrDefault(e => PathEquals(e, entry));
        if (existing is null) return false;

        _entries.Remove(existing);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Clear()
    {
        if (_entries.Count == 0) return;
        _entries.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A snapshot to hand to an operation — the stack may be cleared while it runs.</summary>
    public IReadOnlyList<IFileEntry> Snapshot() => _entries.ToList();

    private static bool PathEquals(IFileEntry a, IFileEntry b) =>
        string.Equals(a.Path.Value, b.Path.Value, StringComparison.OrdinalIgnoreCase);
}
