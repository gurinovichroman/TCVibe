using TCV.App.Settings;

namespace TCV.App.Columns;

/// <summary>How a column's text is aligned in its cell.</summary>
public enum ColumnAlignment { Left, Right }

/// <summary>
/// One column the details view can show: its stable id, localised heading, default width and
/// which <see cref="SortColumn"/> (if any) clicking its header sorts by.
/// </summary>
/// <param name="Id">Stable key written to <c>settings.json</c>; never localise it.</param>
/// <param name="TitleEn">Heading in English.</param>
/// <param name="TitleRu">Heading in Russian.</param>
/// <param name="DefaultWidth">Width in pixels, or 0 for the star-sized name column.</param>
/// <param name="MinWidth">Smallest width the user can drag the column to.</param>
/// <param name="Sort">Sort key applied when the header is clicked, or null for unsortable.</param>
/// <param name="Alignment">Text alignment inside the cell.</param>
/// <param name="IsComputed">
/// True when the value is not in the directory listing and must be measured per file
/// (<see cref="FileMetricsService"/>). Such columns cost IO, so they are off by default.
/// </param>
public sealed record ColumnDescriptor(
    string Id,
    string TitleEn,
    string TitleRu,
    double DefaultWidth,
    double MinWidth,
    SortColumn? Sort = null,
    ColumnAlignment Alignment = ColumnAlignment.Left,
    bool IsComputed = false)
{
    public string Title(string lang) => lang == "ru" ? TitleRu : TitleEn;
}

/// <summary>
/// Registry of the columns the details view knows how to render. Ids are the contract between
/// <see cref="PanelSettings.Columns"/> and the grid, so an unknown id in a hand-edited settings
/// file is dropped rather than allowed to break the panel.
/// </summary>
public static class ColumnCatalog
{
    public const string Name = "name";
    public const string Extension = "ext";
    public const string Size = "size";
    public const string Date = "date";
    public const string Attributes = "attr";
    public const string Type = "type";
    public const string Lines = "loc";
    public const string SizeOnDisk = "ondisk";
    public const string Git = "git";

    public static IReadOnlyList<ColumnDescriptor> All { get; } =
    [
        // The name column is star-sized: DefaultWidth 0 means "take the remaining space".
        new(Name, "Name", "Имя", 0, 120, SortColumn.Name),
        new(Extension, "Ext.", "Расш.", 50, 40, SortColumn.Extension),
        new(Size, "Size", "Размер", 80, 60, SortColumn.Size, ColumnAlignment.Right),
        new(Date, "Modified", "Изменён", 120, 90, SortColumn.Date),
        new(Attributes, "Attr.", "Атр.", 60, 50, SortColumn.Attributes),
        new(Type, "Type", "Тип", 120, 60),
        // ── Dev columns (6.19): measured, not listed ──────────────────────────
        new(Lines, "LOC", "Строк", 70, 50, Alignment: ColumnAlignment.Right, IsComputed: true),
        new(SizeOnDisk, "On disk", "На диске", 90, 70, Alignment: ColumnAlignment.Right, IsComputed: true),
        // Git status (Pillar II): a `git status --short` style cell per row, filled by one background
        // status pass per listing, not per row. On by default since the design (ADR-0030): what changed
        // is what this program is for.
        new(Git, "Git", "Git", 44, 34),
    ];

    /// <summary>Columns a fresh profile shows — the design's set: name, size, modified, git.</summary>
    public static IReadOnlyList<string> DefaultVisible { get; } =
        [Name, Size, Date, Git];

    public static ColumnDescriptor? Find(string id) =>
        All.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The layout a fresh profile starts from.</summary>
    public static List<ColumnLayout> DefaultLayout() =>
        DefaultVisible
            .Select(id => new ColumnLayout { Id = id, Width = Find(id)!.DefaultWidth, Visible = true })
            .ToList();

    /// <summary>
    /// Reconciles a stored layout with the catalog: unknown ids are dropped, columns added to the
    /// catalog since the file was written are appended hidden, and an empty or missing layout
    /// falls back to the defaults. The result always covers every known column exactly once, so
    /// the chooser can present it directly.
    /// </summary>
    public static List<ColumnLayout> Normalize(IReadOnlyList<ColumnLayout>? stored)
    {
        if (stored is null || stored.Count == 0) return AppendMissing(DefaultLayout());

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ColumnLayout>(stored.Count);

        foreach (var layout in stored)
        {
            var descriptor = Find(layout.Id);
            if (descriptor is null || !seen.Add(descriptor.Id)) continue;   // unknown or duplicate

            result.Add(new ColumnLayout
            {
                Id = descriptor.Id,
                // A zero or negative width would render the column invisible with no way back.
                Width = layout.Width > 0 ? layout.Width : descriptor.DefaultWidth,
                Visible = layout.Visible,
            });
        }

        // Losing every column would leave an empty grid; the name column always survives.
        if (!result.Any(c => c.Visible)) return AppendMissing(DefaultLayout());

        return AppendMissing(result);
    }

    private static List<ColumnLayout> AppendMissing(List<ColumnLayout> layout)
    {
        var present = layout.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in All)
        {
            if (present.Contains(descriptor.Id)) continue;
            layout.Add(new ColumnLayout
            {
                Id = descriptor.Id,
                Width = descriptor.DefaultWidth,
                Visible = false,
            });
        }
        return layout;
    }
}
