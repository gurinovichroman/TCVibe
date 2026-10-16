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

/// <summary>Workspace name → the two letters of its rail button (<see cref="WorkspaceTag"/>).</summary>
public sealed class WorkspaceTagConverter : IValueConverter
{
    public static readonly WorkspaceTagConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        WorkspaceTag.For(value as string);

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
