using System.IO;
using TCV.Ai.Agents;

namespace TCV.App.Agents;

/// <summary>Who changed a row of the file list, as the mark beside its name shows it.</summary>
public enum FileOriginMark
{
    /// <summary>No mark: unchanged, outside the repository, or no agent touched the project.</summary>
    None,

    /// <summary>An agent: a write it made through the gate, or a change inside an agent session's window.</summary>
    Agent,

    /// <summary>This file manager, i.e. the person at the keyboard (the operation log has it).</summary>
    Human,

    /// <summary>Changed, and nothing says by whom.</summary>
    Unknown,
}

/// <summary>
/// The «Agent changes» verdicts of one repository, looked up by path for the file list's origin marks.
/// A folder carries the strongest mark of what changed under it: an agent beats a person, a person
/// beats unknown — a folder is where you go looking for the agent's edits.
/// <para>
/// Marks appear only in a project an agent has touched. Without any agent verdict every change of the
/// user's own (an editor, a terminal) would be «unknown», and a question mark on every edited file says
/// nothing anyone needs.
/// </para>
/// </summary>
public sealed class FileOriginIndex
{
    public static FileOriginIndex Empty { get; } = new(string.Empty, new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase));

    private readonly string _root;
    private readonly Dictionary<string, (FileOriginMark Mark, ChangeOrigin Origin)> _files;
    private readonly Dictionary<string, (FileOriginMark Mark, ChangeOrigin Origin)> _folders;

    private FileOriginIndex(
        string root,
        Dictionary<string, (FileOriginMark, ChangeOrigin)> files,
        Dictionary<string, (FileOriginMark, ChangeOrigin)> folders)
    {
        _root = root;
        _files = files;
        _folders = folders;
    }

    /// <summary>The working-tree root the index belongs to; empty for <see cref="Empty"/>.</summary>
    public string RepositoryRoot => _root;

    public bool IsEmpty => _files.Count == 0;

    public static FileOriginIndex Build(string repositoryRoot, IEnumerable<AgentChangeRow> rows) =>
        Build(repositoryRoot, rows.Select(r => (r.RelativePath, r.Origin)));

    /// <param name="changes">Repository-relative paths (either separator) and their verdicts.</param>
    public static FileOriginIndex Build(string repositoryRoot, IEnumerable<(string RelativePath, ChangeOrigin Origin)> changes)
    {
        var list = changes.ToList();
        if (string.IsNullOrEmpty(repositoryRoot) || !list.Any(c => IsAgent(c.Origin)))
        {
            return Empty;
        }

        var files = new Dictionary<string, (FileOriginMark, ChangeOrigin)>(StringComparer.OrdinalIgnoreCase);
        var folders = new Dictionary<string, (FileOriginMark, ChangeOrigin)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (relative, origin) in list)
        {
            var path = Normalize(relative);
            if (path.Length == 0)
            {
                continue;
            }

            var entry = (MarkOf(origin), origin);
            files[path] = Stronger(files.GetValueOrDefault(path), entry);

            // Every folder on the way up carries the strongest mark below it.
            for (var slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
            {
                var folder = path[..slash];
                folders[folder] = Stronger(folders.GetValueOrDefault(folder), entry);
            }
        }

        return new FileOriginIndex(NormalizeRoot(repositoryRoot), files, folders);
    }

    /// <summary>The mark of a file or folder on disk, and the verdict behind it (for the tooltip).</summary>
    public (FileOriginMark Mark, ChangeOrigin Origin) Lookup(string fullPath, bool isDirectory)
    {
        if (IsEmpty || string.IsNullOrEmpty(fullPath))
        {
            return (FileOriginMark.None, ChangeOrigin.External);
        }

        var full = fullPath.Replace('\\', '/').TrimEnd('/');
        if (!full.StartsWith(_root + "/", StringComparison.OrdinalIgnoreCase))
        {
            return (FileOriginMark.None, ChangeOrigin.External);
        }

        var relative = full[(_root.Length + 1)..];
        var map = isDirectory ? _folders : _files;
        return map.TryGetValue(relative, out var found) ? found : (FileOriginMark.None, ChangeOrigin.External);
    }

    private static bool IsAgent(ChangeOrigin origin) => origin is ChangeOrigin.Agent or ChangeOrigin.Supervised;

    private static FileOriginMark MarkOf(ChangeOrigin origin) => origin switch
    {
        ChangeOrigin.Agent or ChangeOrigin.Supervised => FileOriginMark.Agent,
        ChangeOrigin.Manager => FileOriginMark.Human,
        _ => FileOriginMark.Unknown,
    };

    private static int Rank(FileOriginMark mark) => mark switch
    {
        FileOriginMark.Agent => 3,
        FileOriginMark.Human => 2,
        FileOriginMark.Unknown => 1,
        _ => 0,
    };

    private static (FileOriginMark, ChangeOrigin) Stronger(
        (FileOriginMark Mark, ChangeOrigin Origin) current, (FileOriginMark Mark, ChangeOrigin Origin) candidate)
    {
        if (Rank(candidate.Mark) != Rank(current.Mark))
        {
            return Rank(candidate.Mark) > Rank(current.Mark) ? candidate : current;
        }

        // Two agent verdicts: a logged write is the stronger evidence.
        return candidate.Origin == ChangeOrigin.Supervised ? candidate : current;
    }

    private static string Normalize(string relative) => relative.Replace('\\', '/').Trim('/');

    private static string NormalizeRoot(string root) => Path.GetFullPath(root).Replace('\\', '/').TrimEnd('/');
}
