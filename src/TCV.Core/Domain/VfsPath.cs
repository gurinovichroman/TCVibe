namespace TCV.Core.Domain;

/// <summary>Normalized virtual file system path.</summary>
public sealed class VfsPath : IEquatable<VfsPath>
{
    public static readonly VfsPath Root = new("/");

    public string Value { get; }

    public VfsPath(string path)
    {
        Value = Normalize(path);
    }

    public string FileName => Path.GetFileName(Value);
    public VfsPath? Parent
    {
        get
        {
            var dir = Path.GetDirectoryName(Value);
            return dir is null ? null : new VfsPath(dir);
        }
    }

    public VfsPath Combine(string segment) => new(Path.Combine(Value, segment));

    private static string Normalize(string path)
    {
        var trimmed = path.Replace('\\', '/').TrimEnd('/');
        if (trimmed.Length == 0) return "/";
        // "D:" alone is drive-*relative* on Windows and resolves against the process
        // current directory — keep the separator so a drive root stays a drive root.
        if (trimmed.Length == 2 && trimmed[1] == ':') return trimmed + '/';
        return trimmed;
    }

    public override string ToString() => Value;

    public bool Equals(VfsPath? other) =>
        other is not null &&
        string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => obj is VfsPath other && Equals(other);
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);

    public static implicit operator string(VfsPath p) => p.Value;
    public static implicit operator VfsPath(string s) => new(s);
}
