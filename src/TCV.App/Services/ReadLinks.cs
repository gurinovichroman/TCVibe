using System.Globalization;
using TCV.App.FileSystem;
using TCV.Core.Interfaces;
using TCV.Network;

namespace TCV.App.Services;

/// <summary>
/// «Copy link» on files in S3 or Azure Blob (6.81): the lifetimes on offer and what goes on the clipboard.
/// Anyone who has the link can read the file until it expires, so a call that names no lifetime (the
/// palette) gets the shortest.
/// </summary>
public static class ReadLinks
{
    /// <summary>
    /// The lifetimes the menus offer, shortest first, with their item's text. A week is the longest
    /// S3 signs. The main menu passes the same hours as its items' parameters (MainWindow.xaml).
    /// </summary>
    public static IReadOnlyList<(int Hours, string HeaderKey)> Lifetimes { get; } =
        [(1, "Link_ForHour"), (24, "Link_ForDay"), (168, "Link_ForWeek")];

    /// <summary>
    /// The store that signs links for the files a panel shows, or null. Seen through the search-results
    /// view and through a connection opened read-only: a link only reads, and a read-only S3 bucket is
    /// just what one shares (the wrapper hides the store's other interfaces).
    /// </summary>
    public static IReadLinkSource? SourceOf(IVirtualFileSystem fs) => FileSystemIdentity.Underlying(fs) switch
    {
        IReadLinkSource source => source,
        ReadOnlyFileSystem { Inner: IReadLinkSource source } => source,
        _ => null,
    };

    /// <summary>The lifetime a menu item asks for with its parameter (hours); the shortest for anything else.</summary>
    public static TimeSpan Lifetime(string? hours)
    {
        var asked = int.TryParse(hours, NumberStyles.None, CultureInfo.InvariantCulture, out var h)
                    && Lifetimes.Any(l => l.Hours == h)
            ? h
            : Lifetimes[0].Hours;
        return TimeSpan.FromHours(asked);
    }

    /// <summary>One link per line for the files among <paramref name="entries"/>; a folder has none.</summary>
    public static string Text(IReadLinkSource source, IEnumerable<IFileEntry> entries, TimeSpan lifetime) =>
        string.Join(Environment.NewLine, entries
            .Where(e => !e.IsDirectory)
            .Select(e => source.CreateReadLink(e.Path, lifetime).AbsoluteUri));
}
