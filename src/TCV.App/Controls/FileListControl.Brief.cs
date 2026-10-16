using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using TCV.App.Settings;

namespace TCV.App.Controls;

/// <summary>
/// The Brief view: names top to bottom in columns, scrolled sideways, as in Total Commander. Its list
/// has the vertical scroll bar disabled so the vertical WrapPanel gets the viewport's height and wraps
/// into columns; given vertical scrolling it had infinite height and stayed one long column.
/// </summary>
public partial class FileListControl
{
    private ScrollViewer? _briefScroll;
    private VirtualizingWrapPanel? _briefPanel;

    public static readonly DependencyProperty BriefColumnWidthProperty = DependencyProperty.Register(
        nameof(BriefColumnWidth), typeof(double), typeof(FileListControl), new PropertyMetadata(double.NaN));

    /// <summary>The width of every Brief column (the panel's ItemWidth); NaN until the view is first filled.</summary>
    public double BriefColumnWidth
    {
        get => (double)GetValue(BriefColumnWidthProperty);
        set => SetValue(BriefColumnWidthProperty, value);
    }

    // The list's font, followed through bindings: the panel font is a resource the settings swap.
    private static readonly DependencyProperty BriefFontSizeProperty = DependencyProperty.Register(
        "BriefFontSize", typeof(double), typeof(FileListControl), new PropertyMetadata(double.NaN, OnBriefFontChanged));

    private static readonly DependencyProperty BriefFontFamilyProperty = DependencyProperty.Register(
        "BriefFontFamily", typeof(FontFamily), typeof(FileListControl), new PropertyMetadata(null, OnBriefFontChanged));

    private void WatchBriefFont()
    {
        SetBinding(BriefFontSizeProperty, new Binding(nameof(FontSize)) { Source = BriefList });
        SetBinding(BriefFontFamilyProperty, new Binding(nameof(FontFamily)) { Source = BriefList });
    }

    private static void OnBriefFontChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((FileListControl)d).UpdateBriefColumnWidth();

    /// <summary>
    /// Sets the one width of the Brief columns (<see cref="BriefColumns"/>) for the names now listed. Done
    /// when the names change, before the list lays them out: a width set after layout would lay the
    /// columns out twice. Nothing while the view is hidden.
    /// </summary>
    private void UpdateBriefColumnWidth()
    {
        if (ViewModel is not { ViewMode: ViewMode.Brief } vm || !ReferenceEquals(BriefList.ItemsSource, vm.Files))
        {
            return;
        }

        var typeface = new Typeface(BriefList.FontFamily, BriefList.FontStyle, BriefList.FontWeight, BriefList.FontStretch);
        var size = BriefList.FontSize;
        var mode = TextOptions.GetTextFormattingMode(BriefList);
        var pixelsPerDip = VisualTreeHelper.GetDpi(BriefList).PixelsPerDip;

        BriefColumnWidth = BriefColumns.ItemWidth(
            vm.Files.Select(f => f.Name),
            name => new FormattedText(name, CultureInfo.CurrentUICulture, BriefList.FlowDirection, typeface, size,
                Brushes.Black, null, mode, pixelsPerDip).WidthIncludingTrailingWhitespace);
    }

    /// <summary>
    /// Names per column and the columns that are whole in view, as the panel laid them out (6.46: only
    /// the columns in view have items, so the panel's arithmetic is asked, not the items). Null before
    /// the first layout or for an empty list.
    /// </summary>
    private (int RowsPerColumn, int ColumnsInView)? MeasureBriefColumns()
    {
        _briefPanel ??= FindDescendant<VirtualizingWrapPanel>(BriefList);
        return _briefPanel?.Layout is { Count: > 0 } layout ? (layout.PerLine, layout.WholeLinesInView) : null;
    }

    /// <summary>
    /// The wheel scrolls Brief sideways, a column's width per notch. Left to the list it asked for a
    /// vertical scroll the list does not have, and nothing moved.
    /// </summary>
    private void OnBriefMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _briefScroll ??= FindDescendant<ScrollViewer>(BriefList);
        if (_briefScroll is not { ScrollableWidth: > 0 } scroll)
        {
            return;
        }

        _briefPanel ??= FindDescendant<VirtualizingWrapPanel>(BriefList);
        var column = _briefPanel?.Layout is { LineSize: > 0 } layout ? layout.LineSize : 160;
        scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - (e.Delta * column / Mouse.MouseWheelDeltaForOneLine));
        e.Handled = true;
    }
}
