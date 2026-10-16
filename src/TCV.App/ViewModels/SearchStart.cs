namespace TCV.App.ViewModels;

/// <summary>Which file system a search opened from a panel walks.</summary>
public enum SearchScope
{
    /// <summary>The local disk, through the application's disk file system.</summary>
    Disk,

    /// <summary>The file system the panel has mounted (archive, server, plugin), through that file system.</summary>
    Mount,
}

/// <summary>Where Ctrl+F starts: the scope, and the folder in that scope's own path form.</summary>
/// <param name="Scope">Disk or the panel's mount.</param>
/// <param name="Path">A Windows path for <see cref="SearchScope.Disk"/>, a VFS path (<c>/pub/docs</c>) for <see cref="SearchScope.Mount"/>.</param>
public readonly record struct SearchStart(SearchScope Scope, string Path)
{
    /// <summary>
    /// Decides where a search opened from a panel looks (6.55). A panel on disk searches its folder. A
    /// mounted panel — server, archive, any plugin file system — searches the folder it shows, through
    /// that file system (INV-02, TRAP-02): the disk folder underneath the mount has nothing to do with
    /// what the user is looking at, and quietly searching it there was the bug. The one exception is the
    /// search-results list (TRAP-28): it is a list of disk files rather than a folder, so a search
    /// opened on it starts in the disk folder the panel showed before the results.
    /// </summary>
    /// <param name="isMounted">The panel shows something other than the local disk.</param>
    /// <param name="showsSearchResults">The mount is the search-results list.</param>
    /// <param name="currentPath">The folder the panel shows.</param>
    /// <param name="rootLocalPath">The disk folder underneath every mount.</param>
    public static SearchStart For(bool isMounted, bool showsSearchResults, string currentPath, string rootLocalPath)
    {
        if (!isMounted)
        {
            return new SearchStart(SearchScope.Disk, currentPath);
        }

        if (showsSearchResults)
        {
            return new SearchStart(SearchScope.Disk, rootLocalPath);
        }

        return new SearchStart(SearchScope.Mount, string.IsNullOrEmpty(currentPath) ? "/" : currentPath);
    }
}
