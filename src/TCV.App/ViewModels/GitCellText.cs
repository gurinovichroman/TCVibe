namespace TCV.App.ViewModels;

/// <summary>
/// The Git column's one letter (the design's «M», «A», «?»). <see cref="TCV.Git.GitCell"/> keeps the
/// two columns of <c>git status --short</c> (<c>" M"</c>, <c>"A "</c>, <c>"MM"</c>, <c>"??"</c>) and a
/// dot for a folder with changes inside; the list shows the letter that says the most at a glance —
/// the index's when there is one, else the working tree's — and a folder with changes reads «M». The
/// tooltip still says exactly what is staged and what is not.
/// </summary>
public static class GitCellText
{
    public static string Short(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return string.Empty;
        }

        var text = label.Trim();
        return text switch
        {
            "??" => "?",
            "●" => "M",
            _ => text[..1],
        };
    }
}
