using System.Globalization;
using System.Windows.Data;
using TCV.App.Localization;
using TCV.Git;

namespace TCV.App.Converters;

/// <summary>Localised label for a worktree row's state (bind the row itself, no path).</summary>
public sealed class WorktreeStateConverter : IValueConverter
{
    public static WorktreeStateConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not GitWorktreeInfo info
            ? string.Empty
            : info.State switch
            {
                GitWorktreeState.Missing => LocalizationManager.Get("Git_WtStateMissing"),
                GitWorktreeState.Locked => LocalizationManager.Get("Git_WtStateLocked"),
                GitWorktreeState.Dirty => string.Format(LocalizationManager.Get("Git_WtStateDirty"), info.DirtyCountLabel),
                _ => LocalizationManager.Get("Git_WtStateClean"),
            };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
