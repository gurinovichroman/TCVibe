namespace TCV.MultiRename.Rules;

/// <summary>
/// Applies an inner rule to the stem only, leaving the extension untouched.
/// Total Commander's Insert/Delete range operations behave this way. A folder has no extension,
/// so for one the inner rule sees the whole name.
/// </summary>
public sealed class NameOnlyRule : IEntryAwareRenameRule
{
    private readonly IRenameRule _inner;

    public NameOnlyRule(IRenameRule inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public string Description => $"{_inner.Description} (name only)";

    public string Apply(string name) => Apply(name, isDirectory: false);

    public string Apply(string name, bool isDirectory)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        if (isDirectory)
        {
            return _inner.Apply(name);
        }

        var (stem, extension) = FileNameParts.Split(name);
        return _inner.Apply(stem) + extension;
    }
}
