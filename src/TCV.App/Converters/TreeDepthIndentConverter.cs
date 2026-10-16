using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace TCV.App.Converters;

/// <summary>
/// A tree item → the left margin of its own row: 12px, plus 15.5 per level (the design's 12 / 27 / 43).
/// The side bar's rows are full width and indent their content, not their subtree, so the selection
/// slab reaches both edges at every depth.
/// </summary>
public sealed class TreeDepthIndentConverter : IValueConverter
{
    public static readonly TreeDepthIndentConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var depth = 0;
        for (var item = value as TreeViewItem;
             item is not null && ItemsControl.ItemsControlFromItemContainer(item) is TreeViewItem parent;
             item = parent)
        {
            depth++;
        }

        return new Thickness(Math.Round(12 + 15.5 * depth), 0, 8, 0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
