namespace TCV.Git;

/// <summary>How a panel row should be coloured. The App maps each kind to a theme brush, so this
/// layer never names a colour.</summary>
public enum GitCellKind
{
    None,
    /// <summary>Not tracked by git (<c>??</c>).</summary>
    Untracked,
    /// <summary>Everything changed is already in the index.</summary>
    Staged,
    /// <summary>Has working-tree changes not yet staged.</summary>
    Modified,
    /// <summary>Unresolved merge conflict.</summary>
    Conflict,
}

/// <summary>What the git column shows for one row: a <c>git status --short</c> style label and the
/// kind that colours it.</summary>
public readonly record struct GitCell(GitCellKind Kind, string Label);

/// <summary>
/// Git status of the direct children of one directory, keyed by <em>name</em> (files) or by the name
/// of the child directory that contains changes. Built by a single status pass over the repository —
/// the panel needs one answer per listing, not one per row.
/// </summary>
public sealed class GitDirectoryStatus
{
    public static GitDirectoryStatus None { get; } =
        new(false, string.Empty, new Dictionary<string, GitCell>());

    public GitDirectoryStatus(bool isRepository, string branch, IReadOnlyDictionary<string, GitCell> cells)
    {
        IsRepository = isRepository;
        Branch = branch;
        Cells = cells;
    }

    public bool IsRepository { get; }
    public string Branch { get; }

    /// <summary>Cells by child name (case-insensitive, as NTFS is).</summary>
    public IReadOnlyDictionary<string, GitCell> Cells { get; }

    public GitCell Get(string name) => Cells.TryGetValue(name, out var cell) ? cell : default;

    /// <summary>
    /// Folds a repository-relative change list into the cells of one directory.
    /// A file gets its own two-letter label (<c>M </c>, <c> M</c>, <c>MM</c>, <c>??</c>, <c>UU</c>);
    /// a directory containing changes gets a dot coloured by the worst of them, because a folder has
    /// no status of its own but hiding a dirty subtree behind a clean-looking folder is how changes
    /// get forgotten. Keyed by the full relative path, so a file <c>a/x.cs</c> can never paint
    /// <c>./x.cs</c>.
    /// </summary>
    /// <param name="files">Changes with repository-relative, forward-slashed paths. A trailing
    /// slash marks a whole untracked directory.</param>
    /// <param name="directoryPrefix">Repository-relative path of the listed directory with a trailing
    /// slash, or empty for the repository root.</param>
    public static IReadOnlyDictionary<string, GitCell> BuildCells(
        IEnumerable<GitChangedFile> files, string directoryPrefix)
    {
        var prefix = NormalizePrefix(directoryPrefix);
        var accumulators = new Dictionary<string, Accumulator>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var path = file.RelativePath.Replace('\\', '/');
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var rest = path[prefix.Length..];
            if (rest.Length == 0) continue;

            var slash = rest.IndexOf('/');
            var name = slash < 0 ? rest : rest[..slash];
            if (name.Length == 0) continue;

            // "dir/" (an untracked directory reported whole) and "dir/file" both make `dir` a folder row.
            var isFolder = slash >= 0;

            if (!accumulators.TryGetValue(name, out var acc)) acc = new Accumulator();
            acc.Add(file, isFolder);
            accumulators[name] = acc;
        }

        var cells = new Dictionary<string, GitCell>(accumulators.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, acc) in accumulators) cells[name] = acc.ToCell();
        return cells;
    }

    private static string NormalizePrefix(string directoryPrefix)
    {
        var prefix = directoryPrefix.Replace('\\', '/').TrimStart('/');
        if (prefix == "." || prefix.Length == 0) return string.Empty;
        return prefix.EndsWith('/') ? prefix : prefix + "/";
    }

    private static char Letter(GitFileStatus status) => status switch
    {
        GitFileStatus.Added => 'A',
        GitFileStatus.Modified => 'M',
        GitFileStatus.Deleted => 'D',
        GitFileStatus.Renamed => 'R',
        GitFileStatus.Conflict => 'U',
        _ => ' ',
    };

    private struct Accumulator
    {
        private GitFileStatus? _staged;
        private GitFileStatus? _unstaged;
        private bool _folder;
        private bool _conflict;
        private bool _untrackedOnly;
        private bool _any;

        public void Add(GitChangedFile file, bool isFolder)
        {
            if (isFolder) _folder = true;

            if (!_any) _untrackedOnly = true;
            _any = true;

            if (file.Status == GitFileStatus.Conflict) _conflict = true;
            if (file.Status != GitFileStatus.Untracked) _untrackedOnly = false;

            if (file.IsStaged) _staged = file.Status;
            else _unstaged = file.Status;
        }

        public readonly GitCell ToCell()
        {
            if (_folder)
            {
                var kind = _conflict ? GitCellKind.Conflict
                    : _untrackedOnly ? GitCellKind.Untracked
                    : _unstaged is not null ? GitCellKind.Modified
                    : GitCellKind.Staged;
                return new GitCell(kind, _untrackedOnly ? "??" : "●");
            }

            if (_conflict) return new GitCell(GitCellKind.Conflict, "UU");
            if (_unstaged == GitFileStatus.Untracked) return new GitCell(GitCellKind.Untracked, "??");

            var label = string.Concat(
                _staged is { } s ? Letter(s) : ' ',
                _unstaged is { } u ? Letter(u) : ' ');
            return new GitCell(_unstaged is not null ? GitCellKind.Modified : GitCellKind.Staged, label);
        }
    }
}
