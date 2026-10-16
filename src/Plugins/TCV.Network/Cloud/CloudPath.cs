using TCV.Core.Domain;

namespace TCV.Network.Cloud;

/// <summary>
/// VFS paths of a cloud drive as plain strings: always rooted with <c>/</c>, no trailing slash, no
/// empty segments. <see cref="VfsPath"/> builds on <c>System.IO.Path</c>, whose separators are the
/// platform's, so the arithmetic here stays on strings.
/// </summary>
internal static class CloudPath
{
    public static string Normalize(VfsPath path) => Normalize(path.Value);

    public static string Normalize(string path)
    {
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? "/" : "/" + string.Join('/', segments);
    }

    public static bool IsRoot(string path) => path == "/";

    public static string Parent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash <= 0 ? "/" : path[..slash];
    }

    public static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];

    public static string Combine(string parent, string name) => parent == "/" ? "/" + name : parent + "/" + name;

    /// <summary>True when <paramref name="path"/> is <paramref name="ancestor"/> or lies under it.</summary>
    public static bool IsSameOrUnder(string path, string ancestor, StringComparison comparison) =>
        ancestor == "/"
        || path.Equals(ancestor, comparison)
        || (path.Length > ancestor.Length && path.StartsWith(ancestor, comparison) && path[ancestor.Length] == '/');
}
