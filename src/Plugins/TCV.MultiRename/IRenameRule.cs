namespace TCV.MultiRename;

/// <summary>
/// A post-processing rule applied to the file name produced by the template.
/// Rules are applied in order after template expansion.
/// </summary>
public interface IRenameRule
{
    /// <summary>Short description shown in the UI.</summary>
    string Description { get; }

    /// <summary>Applies the rule to <paramref name="name"/> and returns the modified name.</summary>
    string Apply(string name);
}

/// <summary>
/// A rule whose result depends on whether the entry is a folder. A folder name such as
/// <c>release.v2</c> has no extension, so a rule that protects "the extension" must not cut it.
/// </summary>
public interface IEntryAwareRenameRule : IRenameRule
{
    /// <summary>Applies the rule to the name of a file or a folder.</summary>
    string Apply(string name, bool isDirectory);
}
