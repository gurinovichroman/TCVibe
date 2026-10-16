using TCV.Core.Interfaces;

namespace TCV.MultiRename;

/// <summary>Holds all data needed to evaluate a rename template for one file.</summary>
public sealed class RenameContext
{
    public RenameContext(IFileEntry entry, int counter, DateTime referenceDate, int? index = null, int counterWidth = 0)
    {
        Entry = entry;
        Counter = counter;
        ReferenceDate = referenceDate;
        Index = index ?? Math.Max(0, counter - 1);
        CounterWidth = counterWidth;
    }

    /// <summary>The file entry being renamed.</summary>
    public IFileEntry Entry { get; }

    /// <summary>
    /// Value of a plain <c>[C]</c>: the dialog's counter start plus <see cref="Index"/> steps. A
    /// counter that names its own start and step (<c>[C10+2]</c>) ignores this and counts from
    /// <see cref="Index"/> itself.
    /// </summary>
    public int Counter { get; }

    /// <summary>Zero-based position of the file in the batch.</summary>
    public int Index { get; }

    /// <summary>Digits a counter is zero-padded to when the placeholder does not say (0 = no padding).</summary>
    public int CounterWidth { get; }

    /// <summary>Date/time used for [D] and [T] placeholders.</summary>
    public DateTime ReferenceDate { get; }
}
