using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

public partial class WorkspacePropertiesDialog : TcvDialog
{
    /// <summary>The hint of a field (its label and its control): one of the texts in the strip above the buttons.</summary>
    public static readonly DependencyProperty HintProperty = DependencyProperty.RegisterAttached(
        "Hint", typeof(UIElement), typeof(WorkspacePropertiesDialog));

    public static UIElement? GetHint(DependencyObject element) => (UIElement?)element.GetValue(HintProperty);

    public static void SetHint(DependencyObject element, UIElement? value) => element.SetValue(HintProperty, value);

    private readonly WorkspacePropertiesDialogViewModel _viewModel;

    public WorkspacePropertiesDialog(WorkspacePropertiesDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;
        // Closed by the view model, not by a Click handler: Click runs before Command (TRAP-39).
        viewModel.CloseRequested += (_, _) => CloseWithResult(viewModel.DialogResult);

        // The strip follows the keyboard and the mouse, whichever moved to a field last. Between the
        // fields, on the preview and on the buttons the last hint stays.
        Form.AddHandler(Keyboard.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler((_, e) => ShowHintOf(e.NewFocus as DependencyObject)));
        Form.PreviewMouseMove += (_, e) => ShowHintOf(e.OriginalSource as DependencyObject);

        Loaded += (_, _) =>
        {
            // Renaming is the most common reason to come here (it used to be its own prompt on the
            // same shortcut), so the name is ready to be typed over.
            FocusAndSelect(NameBox);
        };
    }

    private void ShowHintOf(DependencyObject? source)
    {
        for (var node = source; node is not null; node = ParentOf(node))
        {
            if (GetHint(node) is { } hint)
            {
                // Every mouse move over a field ends here: only a change of field touches the strip.
                if (hint.Visibility == Visibility.Visible)
                {
                    return;
                }

                foreach (UIElement each in Hints.Children)
                {
                    each.Visibility = ReferenceEquals(each, hint) ? Visibility.Visible : Visibility.Hidden;
                }

                return;
            }
        }
    }

    private static DependencyObject? ParentOf(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);

    /// <summary>Any colour outside the swatches: the standard Windows colour dialog.</summary>
    private void OnPickOtherColor(object sender, RoutedEventArgs e)
    {
        if (SystemColorPicker.Pick(this, _viewModel.NormalizedColor) is { } picked)
        {
            _viewModel.ColorText = picked;
        }
    }
}
