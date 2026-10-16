using System.IO;
using TCV.App.Settings;
using TCV.Core.Interfaces;

namespace TCV.App.Collections;

/// <summary>
/// Order of a panel listing: folders first (when asked, in either direction), then the sort
/// column, then the name in natural ascending order — so files of equal size or date do not
/// come out in whatever order the directory lister happened to return them.
/// </summary>
public sealed class FileEntryComparer : IComparer<IFileEntry>
{
    private readonly SortColumn _column;
    private readonly bool _descending;
    private readonly bool _foldersFirst;
    private readonly Func<IFileEntry, long> _sizeOf;

    /// <param name="sizeOf">
    /// The size to sort by. A listing knows a folder only as 0; the panel passes the sizes it measured,
    /// so «Folder sizes» followed by a sort by size puts the heaviest folder first.
    /// </param>
    public FileEntryComparer(SortColumn column, bool descending, bool foldersFirst, Func<IFileEntry, long>? sizeOf = null)
    {
        _column = column;
        _descending = descending;
        _foldersFirst = foldersFirst;
        _sizeOf = sizeOf ?? (e => e.Size);
    }

    public int Compare(IFileEntry? x, IFileEntry? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        if (_foldersFirst && x.IsDirectory != y.IsDirectory)
            return x.IsDirectory ? -1 : 1;

        var byColumn = CompareColumn(x, y);
        if (byColumn != 0) return _descending ? -byColumn : byColumn;

        // Tie-break on the name. For the name column itself it only settles case/zero variants.
        return NaturalStringComparer.Instance.Compare(x.Name, y.Name);
    }

    private int CompareColumn(IFileEntry x, IFileEntry y) => _column switch
    {
        SortColumn.Extension => NaturalStringComparer.Instance.Compare(
            Path.GetExtension(x.Name), Path.GetExtension(y.Name)),
        SortColumn.Size => _sizeOf(x).CompareTo(_sizeOf(y)),
        SortColumn.Date => x.LastModified.CompareTo(y.LastModified),
        SortColumn.Attributes => string.CompareOrdinal(x.Attributes.ToString(), y.Attributes.ToString()),
        _ => NaturalStringComparer.Instance.Compare(x.Name, y.Name),
    };
}
