using CommunityToolkit.Mvvm.ComponentModel;
using TCV.App.Localization;
using TCV.App.Services;

namespace TCV.App.ViewModels;

/// <summary>
/// The drop-stack count in the title bar, after the menu (6.51). The stack is session state that
/// nothing else on screen shows: files collected with "add to stack" were easy to forget, and the
/// only way to see how many there were was to open the stack. Hidden while the stack is empty.
/// </summary>
/// <remarks>
/// <see cref="DropStack.Changed"/> is raised on whichever thread changed the stack, and these
/// properties are bound to the window, so every update goes through <c>post</c> — the window's
/// dispatcher in the app (TRAP-47). The count is read when the update runs, not when the event was
/// raised, so a burst of changes still ends on the latest number. The badge lives as long as the
/// window's view model, which is the whole process, so its static subscriptions are never removed.
/// </remarks>
public sealed partial class DropStackBadge : ObservableObject
{
    private readonly DropStack _stack;
    private readonly Action<Action> _post;

    /// <param name="post">Runs an update on the thread the bindings live on.</param>
    public DropStackBadge(DropStack stack, Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(post);
        _stack = stack;
        _post = post;

        _stack.Changed += (_, _) => _post(Update);
        // The text is formatted in code (it has a plural form and a shortcut), so a language switch
        // or a rebind has to re-read it.
        LocalizationManager.LanguageChanged += (_, _) => _post(Update);
        LocalizationManager.GesturesChanged += (_, _) => _post(Update);
        Update();
    }

    /// <summary>How many entries are on the stack.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible))]
    private int _count;

    /// <summary>Shown only while there is something on the stack.</summary>
    public bool IsVisible => Count > 0;

    /// <summary>The short label in the menu bar.</summary>
    [ObservableProperty] private string _text = string.Empty;

    /// <summary>The long form, for the tooltip and screen readers.</summary>
    [ObservableProperty] private string _toolTip = string.Empty;

    private void Update()
    {
        Count = _stack.Count;
        Text = Count == 0 ? string.Empty : LocalizationManager.Format("Drop_Badge", Count);
        ToolTip = Count == 0 ? string.Empty : LocalizationManager.Format("Drop_BadgeTip", Count);
    }

    /// <summary>The app's <c>post</c>: straight through on the UI thread, queued from any other.</summary>
    public static void PostToDispatcher(Action update)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;   // shutting down, or headless: nothing is bound
        }

        if (dispatcher.CheckAccess())
        {
            update();
        }
        else
        {
            dispatcher.BeginInvoke(update);
        }
    }
}
