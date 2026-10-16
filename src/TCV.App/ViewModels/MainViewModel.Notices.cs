using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TCV.App.ViewModels;

/// <summary>
/// A one-line notice that rides on the window caption: the place for "uploaded to the server" or
/// "Ctrl+K — waiting for the second key", things that deserve to be seen without a dialog. The caption
/// is visible whatever panel has focus and shows in the taskbar too.
/// </summary>
public partial class MainViewModel
{
    private int _noticeVersion;

    /// <summary>The notice currently shown after the caption, or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    private string _transientNotice = string.Empty;

    /// <summary>The caption the window shows: the workspace title plus any current notice.</summary>
    public string DisplayTitle =>
        TransientNotice.Length == 0 ? WindowTitle : $"{WindowTitle}  ·  {TransientNotice}";

    /// <summary>
    /// Shows <paramref name="text"/> after the caption and removes it again after
    /// <paramref name="duration"/> (or leaves it until <see cref="ClearNotice"/> when null). A newer
    /// notice replaces an older one, and only the newest one's timer may clear it. Safe from any thread.
    /// </summary>
    public void ShowNotice(string text, TimeSpan? duration = null)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.InvokeAsync(() =>
        {
            var version = ++_noticeVersion;
            TransientNotice = text;

            if (duration is not { } after)
            {
                return;
            }

            _ = Task.Delay(after).ContinueWith(_ => dispatcher.InvokeAsync(() =>
            {
                if (_noticeVersion == version)
                {
                    TransientNotice = string.Empty;
                }
            }), TaskScheduler.Default);
        });
    }

    /// <summary>
    /// Removes the current notice at once. With <paramref name="onlyIf"/>, only while that text is
    /// still the one shown: a caller clearing its own "in progress" notice must not wipe a newer one.
    /// </summary>
    public void ClearNotice(string? onlyIf = null)
    {
        var dispatcher = Application.Current?.Dispatcher;
        dispatcher?.InvokeAsync(() =>
        {
            if (onlyIf is not null && !string.Equals(TransientNotice, onlyIf, StringComparison.Ordinal))
            {
                return;
            }

            _noticeVersion++;
            TransientNotice = string.Empty;
        });
    }
}
