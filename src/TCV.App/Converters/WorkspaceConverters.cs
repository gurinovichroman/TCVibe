using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using TCV.App.Workspaces;

namespace TCV.App.Converters;

/// <summary>
/// (workspace name, active workspace name) → true when they are the same workspace. Names are
/// unique case-insensitively, so the comparison is too. Drives the sidebar's "you are here" mark.
/// </summary>
public sealed class WorkspaceIsActiveConverter : IMultiValueConverter
{
    public static readonly WorkspaceIsActiveConverter Instance = new();

    public static bool IsSame(string? name, string? activeName) =>
        !string.IsNullOrEmpty(name)
        && string.Equals(name, activeName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Answers in whatever the bound property wants, so the sidebar needs no triggers (a trigger
    /// cannot watch a multi-binding): <c>Visibility</c> for the marker bar (hidden, not collapsed,
    /// so names stay aligned), <c>FontWeight</c> for the label, a plain <c>bool</c> otherwise.
    /// </summary>
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var active = values.Length >= 2 && IsSame(values[0] as string, values[1] as string);

        if (targetType == typeof(Visibility))
        {
            return active ? Visibility.Visible : Visibility.Hidden;
        }

        if (targetType == typeof(FontWeight))
        {
            return active ? FontWeights.SemiBold : FontWeights.Normal;
        }

        return active;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Workspace name → the letter on its plate on the rail (<see cref="WorkspaceTag.Monogram"/>).</summary>
public sealed class WorkspaceMonogramConverter : IValueConverter
{
    public static readonly WorkspaceMonogramConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        WorkspaceTag.Monogram(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// A workspace → the brush of its monogram plate (<see cref="WorkspaceColor.PlateFor"/>); with
/// <c>ConverterParameter=fg</c> → the letter's brush, dark or white for contrast on that plate.
/// </summary>
public sealed class WorkspacePlateConverter : IValueConverter
{
    public static readonly WorkspacePlateConverter Instance = new();

    private static readonly Brush DarkText = Frozen(Color.FromRgb(0x1B, 0x1B, 0x1F));

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not Workspace workspace
            || !WorkspaceColor.TryParseRgb(WorkspaceColor.PlateFor(workspace.Color, workspace.Name), out var r, out var g, out var b))
        {
            return null;
        }

        if (parameter as string == "fg")
        {
            return WorkspaceColor.WantsDarkText(r, g, b) ? DarkText : Brushes.White;
        }

        return Frozen(Color.FromRgb(r, g, b));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>Workspace name → the caption under its rail tile, with breaks between words (<see cref="WorkspaceTag.WithBreaks"/>).</summary>
public sealed class WorkspaceBreakableNameConverter : IValueConverter
{
    public static readonly WorkspaceBreakableNameConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        WorkspaceTag.WithBreaks(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// A workspace's picture (<see cref="Workspace.IconImage"/>) → the stored image, or null when there is
/// none or the file is gone. With <c>ConverterParameter=has</c> → whether there is one, for the triggers
/// that put the picture in place of the emoji and the letters.
/// </summary>
public sealed class WorkspaceImageConverter : IValueConverter
{
    public static readonly WorkspaceImageConverter Instance = new();

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var image = WorkspaceIconStore.Default.Load(value as string);
        return parameter as string == "has" ? image is not null : image;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Workspace colour text ("#4F9CF9") → brush for the sidebar dot. A workspace without a colour (or
/// with a hand-edited, unparsable one) gets no brush, so the dot simply is not drawn.
/// </summary>
public sealed class WorkspaceColorToBrushConverter : IValueConverter
{
    public static readonly WorkspaceColorToBrushConverter Instance = new();

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (!WorkspaceColor.TryParseRgb(value as string, out var r, out var g, out var b))
        {
            return null;
        }

        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
