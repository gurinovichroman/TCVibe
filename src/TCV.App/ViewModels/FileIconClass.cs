namespace TCV.App.ViewModels;

/// <summary>Which line icon a row of the file list gets (docs/DESIGN.md, «Список файлов»).</summary>
public enum FileIconKind
{
    /// <summary>Any other file: a plain page in the dim text colour.</summary>
    File,

    /// <summary>A folder (and ".."): the folder outline in the folder colour.</summary>
    Folder,

    /// <summary>Source code: the code page in the code colour.</summary>
    Code,

    /// <summary>Build and project files: the code page, but quiet — they are read less than code.</summary>
    Project,

    /// <summary>Markdown, plain text and office documents: the text page in the info colour.</summary>
    Document,

    /// <summary>Shell scripts: a page with a prompt, in the code colour — they run, unlike the rest of the code.</summary>
    Script,

    /// <summary>Settings files (ini, env, reg…): a page with sliders, quiet.</summary>
    Config,

    /// <summary>Packed files: the zipped folder, as Explorer draws a compressed folder — the panel enters them like one.</summary>
    Archive,

    /// <summary>Tables (csv, xlsx…): the grid page in the success green.</summary>
    Spreadsheet,

    /// <summary>Slides: the chart page in amber.</summary>
    Presentation,

    /// <summary>PDF: the text page in red, the colour everyone knows it by.</summary>
    Pdf,

    /// <summary>Pictures: the picture page in the media colour.</summary>
    Image,

    /// <summary>Video: the play page in the media colour.</summary>
    Video,

    /// <summary>Sound: the note page in the media colour.</summary>
    Audio,

    /// <summary>Programs and installers: a window in the success green.</summary>
    Executable,

    /// <summary>Libraries and drivers (dll, sys): the cog page, quiet.</summary>
    Binary,

    /// <summary>Fonts: the letter page, quiet.</summary>
    Font,

    /// <summary>Keys and certificates: the key page in amber — handle with care.</summary>
    Key,

    /// <summary>Database files: the cylinder in the info colour.</summary>
    Database,

    /// <summary>Disk images (iso, vhdx…): the disc, quiet.</summary>
    DiskImage,
}

/// <summary>
/// Picks the icon of a row by its extension. The list draws line icons, not the shell's: they take
/// the theme's colours, read the same at every size, and do not cost a shell call per type. Each kind
/// has its own shape, but for a project file (the code page, quiet); a file with no shape of its own,
/// PDF and office documents wear their extension on a badge instead (<see cref="BadgeFor"/>).
/// </summary>
public static class FileIconClass
{
    private static readonly Dictionary<string, FileIconKind> ByExtension = Build(
        (FileIconKind.Code,
        [
            "cs", "xaml", "json", "js", "jsx", "ts", "tsx", "mjs", "cjs", "py", "go", "rs", "java", "kt", "kts",
            "c", "h", "cpp", "hpp", "cc", "fs", "fsx", "vb", "swift", "rb", "php", "lua", "dart", "scala",
            "sql", "html", "htm", "css", "scss", "less", "sass", "vue", "svelte", "razor", "cshtml",
            "yaml", "yml", "toml", "xml", "graphql", "proto", "r", "pl", "ex", "exs", "erl", "hs", "clj", "zig",
        ]),
        (FileIconKind.Project,
        [
            "csproj", "fsproj", "vbproj", "vcxproj", "props", "targets", "slnx", "editorconfig", "lock",
        ]),
        (FileIconKind.Document,
        [
            "md", "markdown", "mdx", "txt", "rst", "adoc", "log", "doc", "docx", "odt", "rtf", "pages", "epub",
        ]),
        (FileIconKind.Script,
        [
            "ps1", "psm1", "psd1", "sh", "bash", "zsh", "fish", "bat", "cmd", "vbs", "ahk",
        ]),
        (FileIconKind.Config,
        [
            "ini", "cfg", "conf", "config", "env", "properties", "reg", "inf", "gitignore", "gitattributes",
            "dockerignore", "npmrc", "nvmrc",
        ]),
        (FileIconKind.Archive,
        [
            "zip", "7z", "rar", "tar", "gz", "tgz", "bz2", "tbz2", "xz", "txz", "zst", "lz", "lzma", "cab",
            "arj", "z", "jar", "war", "nupkg", "apk", "whl",
        ]),
        (FileIconKind.Spreadsheet, ["csv", "tsv", "xls", "xlsx", "xlsm", "xlsb", "ods", "numbers"]),
        // Keynote's .key is left to the keys: in a developer's folders it is a private key.
        (FileIconKind.Presentation, ["ppt", "pptx", "pps", "ppsx", "odp"]),
        (FileIconKind.Pdf, ["pdf", "xps"]),
        (FileIconKind.Image,
        [
            "png", "jpg", "jpeg", "gif", "bmp", "webp", "svg", "ico", "tif", "tiff", "heic", "heif", "avif",
            "psd", "ai", "raw", "cr2", "nef", "dng", "jxl",
        ]),
        (FileIconKind.Video, ["mp4", "mkv", "avi", "mov", "wmv", "webm", "m4v", "flv", "mpg", "mpeg", "3gp"]),
        (FileIconKind.Audio, ["mp3", "wav", "flac", "ogg", "oga", "m4a", "aac", "wma", "opus", "mid", "midi", "aiff"]),
        (FileIconKind.Executable, ["exe", "msi", "msix", "msixbundle", "appx", "appinstaller", "com", "scr"]),
        (FileIconKind.Binary, ["dll", "sys", "so", "dylib", "lib", "a", "o", "obj", "pdb", "bin"]),
        (FileIconKind.Font, ["ttf", "otf", "woff", "woff2", "eot", "fon"]),
        (FileIconKind.Key, ["pem", "key", "crt", "cer", "der", "pfx", "p12", "pub", "ppk", "gpg", "asc", "kdbx", "jks"]),
        (FileIconKind.Database, ["db", "sqlite", "sqlite3", "mdb", "accdb", "mdf", "ldf", "dbf", "parquet"]),
        (FileIconKind.DiskImage, ["iso", "img", "vhd", "vhdx", "vmdk", "dmg", "qcow2"]));

    /// <summary>Office documents: the text page alone would not tell a .docx from a .txt.</summary>
    private static readonly HashSet<string> BadgedDocuments = new(StringComparer.OrdinalIgnoreCase)
    {
        "doc", "docx", "odt", "rtf", "pages", "epub",
    };

    /// <summary>Longer extensions do not fit the badge and keep the plain page.</summary>
    public const int MaxBadgeLength = 4;

    /// <summary>
    /// The extension printed on a badge in place of the icon («PDF», «DOCX», «SLN»), or null for a row
    /// that keeps its line icon. Badged: PDF, office documents and any file with no icon of its own —
    /// a plain page told them apart from nothing. A badge is the extension in capitals, up to
    /// <see cref="MaxBadgeLength"/> letters; no extension or a longer one keeps the page.
    /// </summary>
    public static string? BadgeFor(string? extension, bool isDirectory)
    {
        var ext = extension ?? string.Empty;
        if (isDirectory || ext.Length == 0 || ext.Length > MaxBadgeLength)
        {
            return null;
        }

        var kind = For(ext, isDirectory: false);
        var badged = kind is FileIconKind.File or FileIconKind.Pdf
            || (kind == FileIconKind.Document && BadgedDocuments.Contains(ext));
        return badged ? ext.ToUpperInvariant() : null;
    }

    /// <param name="extension">Without the dot, as <see cref="FileEntryViewModel.Extension"/> has it.</param>
    public static FileIconKind For(string? extension, bool isDirectory)
    {
        if (isDirectory)
        {
            return FileIconKind.Folder;
        }

        return ByExtension.TryGetValue(extension ?? string.Empty, out var kind) ? kind : FileIconKind.File;
    }

    private static Dictionary<string, FileIconKind> Build(params (FileIconKind Kind, string[] Extensions)[] groups)
    {
        var map = new Dictionary<string, FileIconKind>(StringComparer.OrdinalIgnoreCase);
        foreach (var (kind, extensions) in groups)
        {
            foreach (var extension in extensions)
            {
                // Each extension belongs to one kind: a second claim is a slip in the table above.
                map.Add(extension, kind);
            }
        }

        return map;
    }
}
