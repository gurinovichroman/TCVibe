using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using TCV.App.Navigation;

namespace TCV.App.Controls;

/// <summary>
/// A name in the file list with the part the quick filter found drawn out (6.87), as VS Code's quick
/// open does: the text is set through <c>MatchHighlight.Text</c> instead of <c>TextBlock.Text</c>, and
/// <c>MatchHighlight.Query</c> follows the panel's filter. The found part is semibold in the accent;
/// <see cref="QuickFilter.MatchSpan"/> says which part. With no filter, a mask or no match, the block
/// gets the plain text, so a list that is not being filtered builds no runs at all.
/// </summary>
public static class MatchHighlight
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(MatchHighlight), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty QueryProperty = DependencyProperty.RegisterAttached(
        "Query", typeof(string), typeof(MatchHighlight), new PropertyMetadata(null, OnChanged));

    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);

    public static string? GetQuery(DependencyObject element) => (string?)element.GetValue(QueryProperty);

    public static void SetQuery(DependencyObject element, string? value) => element.SetValue(QueryProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
        {
            return;
        }

        var text = GetText(block) ?? string.Empty;
        var query = GetQuery(block);
        if (string.IsNullOrWhiteSpace(query) || QuickFilter.Parse(query).MatchSpan(text) is not { } span)
        {
            block.Text = text;
            return;
        }

        block.Inlines.Clear();
        if (span.Start > 0)
        {
            block.Inlines.Add(new Run(text[..span.Start]));
        }

        var found = new Run(text.Substring(span.Start, span.Length)) { FontWeight = FontWeights.SemiBold };
        found.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
        block.Inlines.Add(found);

        var end = span.Start + span.Length;
        if (end < text.Length)
        {
            block.Inlines.Add(new Run(text[end..]));
        }
    }
}
