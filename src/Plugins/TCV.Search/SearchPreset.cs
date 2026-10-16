namespace TCV.Search;

/// <summary>
/// A named, reusable set of search settings. Serialised as-is into
/// <c>search-presets.json</c> next to the executable (INV-03), so a preset survives a portable copy.
/// </summary>
/// <remarks>
/// A flat DTO on purpose: <see cref="SearchCriteria"/> carries compiled regexes and lazily
/// resolved encodings, none of which belong in a settings file. <see cref="ToCriteria"/> and
/// <see cref="FromCriteria"/> are the only conversion points, so a new knob is added in one place.
/// </remarks>
public sealed class SearchPreset
{
    /// <summary>Name shown in the preset list; the identity used for upsert and delete.</summary>
    public string Name { get; set; } = string.Empty;

    public string NamePattern { get; set; } = "*";
    public bool NameIsRegex { get; set; }
    public bool CaseSensitive { get; set; }

    public string ContentText { get; set; } = string.Empty;
    public bool ContentIsRegex { get; set; }
    public bool WholeWord { get; set; }
    public int ContentCodePage { get; set; }
    public bool SkipBinary { get; set; } = true;
    public int ContextLines { get; set; }

    public string ExcludePatterns { get; set; } = string.Empty;
    public bool RespectGitignore { get; set; } = true;

    public bool IncludeHidden { get; set; }
    public bool IncludeSystem { get; set; }
    public bool FilesOnly { get; set; }
    public bool DirectoriesOnly { get; set; }
    public bool SearchInsideArchives { get; set; }

    public long MinSizeBytes { get; set; }
    public long MaxSizeBytes { get; set; }

    /// <summary>Only entries modified after this instant; null for no bound. Older preset files lack it.</summary>
    public DateTime? ModifiedAfter { get; set; }

    /// <summary>Only entries modified before this instant; null for no bound.</summary>
    public DateTime? ModifiedBefore { get; set; }

    /// <summary>Builds the criteria this preset describes. Size bounds of 0 mean "no bound".</summary>
    public SearchCriteria ToCriteria() => new()
    {
        NamePattern = string.IsNullOrWhiteSpace(NamePattern) ? null : NamePattern,
        NameIsRegex = NameIsRegex,
        CaseSensitive = CaseSensitive,
        ContentText = string.IsNullOrEmpty(ContentText) ? null : ContentText,
        ContentIsRegex = ContentIsRegex,
        WholeWord = WholeWord,
        ContentCodePage = ContentCodePage,
        SkipBinary = SkipBinary,
        ContextLines = ContextLines,
        ExcludePatterns = string.IsNullOrWhiteSpace(ExcludePatterns) ? null : ExcludePatterns,
        RespectGitignore = RespectGitignore,
        IncludeHidden = IncludeHidden,
        IncludeSystem = IncludeSystem,
        FilesOnly = FilesOnly,
        DirectoriesOnly = DirectoriesOnly,
        MinSizeBytes = MinSizeBytes > 0 ? MinSizeBytes : null,
        MaxSizeBytes = MaxSizeBytes > 0 ? MaxSizeBytes : null,
        ModifiedAfter = ModifiedAfter,
        ModifiedBefore = ModifiedBefore,
    };

    /// <summary>Captures the given criteria under <paramref name="name"/>.</summary>
    public static SearchPreset FromCriteria(string name, SearchCriteria c, bool searchInsideArchives = false) => new()
    {
        Name = name,
        NamePattern = c.NamePattern ?? "*",
        NameIsRegex = c.NameIsRegex,
        CaseSensitive = c.CaseSensitive,
        ContentText = c.ContentText ?? string.Empty,
        ContentIsRegex = c.ContentIsRegex,
        WholeWord = c.WholeWord,
        ContentCodePage = c.ContentCodePage,
        SkipBinary = c.SkipBinary,
        ContextLines = c.ContextLines,
        ExcludePatterns = c.ExcludePatterns ?? string.Empty,
        RespectGitignore = c.RespectGitignore,
        IncludeHidden = c.IncludeHidden,
        IncludeSystem = c.IncludeSystem,
        FilesOnly = c.FilesOnly,
        DirectoriesOnly = c.DirectoriesOnly,
        SearchInsideArchives = searchInsideArchives,
        MinSizeBytes = c.MinSizeBytes ?? 0,
        MaxSizeBytes = c.MaxSizeBytes ?? 0,
        ModifiedAfter = c.ModifiedAfter,
        ModifiedBefore = c.ModifiedBefore,
    };

    /// <summary>
    /// Presets a fresh portable copy offers before the user saves any: the three searches a
    /// developer actually runs. Only used when <c>search-presets.json</c> does not exist yet.
    /// </summary>
    public static IReadOnlyList<SearchPreset> Builtin { get; } =
    [
        new()
        {
            Name = "TODO / FIXME",
            NamePattern = "*",
            ContentText = @"\b(TODO|FIXME|HACK|XXX)\b",
            ContentIsRegex = true,
            CaseSensitive = true,
        },
        new()
        {
            Name = "Source code",
            NamePattern = "*.cs;*.xaml;*.ts;*.tsx;*.js;*.py;*.go;*.rs;*.java;*.cpp;*.h",
            ExcludePatterns = "bin/;obj/;node_modules/;dist/",
        },
        new()
        {
            Name = "Config & docs",
            NamePattern = "*.json;*.yml;*.yaml;*.toml;*.ini;*.md;*.props;*.targets",
        },
    ];
}
