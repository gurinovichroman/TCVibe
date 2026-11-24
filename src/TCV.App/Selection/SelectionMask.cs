using TCV.Search;

namespace TCV.App.Selection;

/// <summary>
/// The mask of Num+ and its templates, in Total Commander's grammar: <c>*.cs;*.md | *.g.cs</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Masks are separated by <c>;</c> (or a comma, as in <see cref="FileMaskSet"/>), never by a
/// space: <c>my report*.docx</c> is one mask.</item>
/// <item>Everything after the first <c>|</c> excludes. Nothing before it means "everything":
/// <c>| *.bak</c> is every file but the backups.</item>
/// <item>A mask ending in <c>\</c> or <c>/</c> names folders (<c>bin\</c>, <c>*\</c> for every
/// folder). A plain mask names files, and folders too only when the caller asks for them — marking
/// <c>*.*</c> must not drag every subfolder into a delete.</item>
/// </list>
/// </remarks>
public sealed class SelectionMask
{
    private static readonly char[] Separators = [';', ','];

    private readonly FileMaskSet _files;
    private readonly FileMaskSet _folders;
    private readonly FileMaskSet _excludeFiles;
    private readonly FileMaskSet _excludeFolders;

    private SelectionMask(FileMaskSet files, FileMaskSet folders, FileMaskSet excludeFiles, FileMaskSet excludeFolders)
    {
        _files = files;
        _folders = folders;
        _excludeFiles = excludeFiles;
        _excludeFolders = excludeFolders;
    }

    /// <summary>True when the text names nothing to include or exclude (blank, or only separators).</summary>
    public bool IsEmpty => _files.IsEmpty && _folders.IsEmpty && _excludeFiles.IsEmpty && _excludeFolders.IsEmpty;

    public static SelectionMask Parse(string? text)
    {
        text ??= string.Empty;
        var bar = text.IndexOf('|');
        var include = bar < 0 ? text : text[..bar];
        var exclude = bar < 0 ? string.Empty : text[(bar + 1)..];

        var (files, folders) = Split(include);
        var (excludeFiles, excludeFolders) = Split(exclude);

        // "| *.bak": an exclusion alone means "everything else".
        if (files.Count == 0 && folders.Count == 0 && (excludeFiles.Count > 0 || excludeFolders.Count > 0))
        {
            files.Add("*");
        }

        return new SelectionMask(Compile(files), Compile(folders), Compile(excludeFiles), Compile(excludeFolders));
    }

    /// <summary>
    /// True when an entry with this name is named by the mask. Plain masks reach folders only with
    /// <paramref name="includeFolders"/>; folder masks (<c>bin\</c>) always do.
    /// </summary>
    public bool Matches(string name, bool isDirectory, bool includeFolders)
    {
        if (!isDirectory)
        {
            return !_files.IsEmpty && _files.Matches(name) && !(!_excludeFiles.IsEmpty && _excludeFiles.Matches(name));
        }

        var plain = includeFolders && !_files.IsEmpty && _files.Matches(name);
        var named = !_folders.IsEmpty && _folders.Matches(name);
        if (!plain && !named)
        {
            return false;
        }

        if (!_excludeFolders.IsEmpty && _excludeFolders.Matches(name))
        {
            return false;
        }
        return !(includeFolders && !_excludeFiles.IsEmpty && _excludeFiles.Matches(name));
    }

    private static (List<string> Files, List<string> Folders) Split(string part)
    {
        var files = new List<string>();
        var folders = new List<string>();
        foreach (var token in part.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.EndsWith('\\') || token.EndsWith('/'))
            {
                var folder = token.TrimEnd('\\', '/');
                folders.Add(folder.Length == 0 ? "*" : folder);
            }
            else
            {
                files.Add(token);
            }
        }

        return (files, folders);
    }

    private static FileMaskSet Compile(List<string> masks) =>
        masks.Count == 0 ? FileMaskSet.Empty : FileMaskSet.Parse(string.Join(';', masks));
}
