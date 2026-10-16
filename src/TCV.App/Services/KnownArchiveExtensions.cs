using System.IO;

namespace TCV.App.Services;

/// <summary>
/// Extensions Enter opens as a virtual folder. Only these: the archive detector also recognises the
/// ZIP container inside .docx, .xlsx, .jar and .apk, and Enter on a Word document must open Word,
/// not a list of XML parts (F3 and Alt+F9 still work on those).
/// </summary>
public static class KnownArchiveExtensions
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".tbz", ".tbz2", ".xz", ".txz", ".iso", ".cab",
    };

    public static bool IsArchiveFileName(string fileName) =>
        Extensions.Contains(Path.GetExtension(fileName));
}
