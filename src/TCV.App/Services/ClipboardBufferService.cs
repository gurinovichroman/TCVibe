using TCV.Core.Interfaces;

namespace TCV.App.Services;

/// <summary>In-memory clipboard buffer for file operations.</summary>
public sealed class ClipboardBufferService : IClipboardBuffer
{
    private List<ClipboardEntry> _entries = new();
    private ClipboardOperation _operation = ClipboardOperation.None;

    public ClipboardOperation Operation => _operation;
    public IReadOnlyList<ClipboardEntry> Entries => _entries;
    public bool HasEntries => _entries.Count > 0 && _operation != ClipboardOperation.None;

    public long Stamp { get; set; }

    public bool IsStale(long currentSystemStamp) =>
        HasEntries && Stamp != 0 && currentSystemStamp != Stamp;

    public void SetCopy(IReadOnlyList<ClipboardEntry> entries)
    {
        _entries = new List<ClipboardEntry>(entries);
        _operation = ClipboardOperation.Copy;
    }

    public void SetCut(IReadOnlyList<ClipboardEntry> entries)
    {
        _entries = new List<ClipboardEntry>(entries);
        _operation = ClipboardOperation.Cut;
    }

    public void Clear()
    {
        _entries.Clear();
        _operation = ClipboardOperation.None;
        Stamp = 0;
    }

    public IReadOnlyList<ClipboardEntry> GetEntriesToDelete()
    {
        if (_operation != ClipboardOperation.Cut)
            return Array.Empty<ClipboardEntry>();

        var entries = new List<ClipboardEntry>(_entries);
        return entries;
    }
}
