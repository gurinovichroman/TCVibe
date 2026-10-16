namespace TCV.MultiRename;

/// <summary>
/// One definition of where a file name's stem ends and its extension begins, shared by the
/// template placeholders and the name-only rules. Without it a single Multi-Rename run applies
/// two contradictory notions of "extension" to the same file.
/// </summary>
internal static class FileNameParts
{
    /// <summary>
    /// Splits a name into stem and extension, the extension keeping its leading dot (or empty).
    /// A leading dot belongs to the stem: ".gitignore" is a name with no extension, not an
    /// extension with no name — which is how <see cref="System.IO.Path.GetExtension"/> reads it.
    /// </summary>
    public static (string Stem, string Extension) Split(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return (string.Empty, string.Empty);

        var dot = fileName.LastIndexOf('.');
        return dot <= 0
            ? (fileName, string.Empty)
            : (fileName[..dot], fileName[dot..]);
    }
}
