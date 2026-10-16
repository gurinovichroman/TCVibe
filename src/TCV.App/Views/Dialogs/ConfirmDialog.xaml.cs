using System.Windows;
using TCV.App.Localization;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

public partial class ConfirmDialog : TcvDialog
{
    public ConfirmDialog(ConfirmDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => CloseWithResult(viewModel.DialogResult);
    }

    /// <summary>
    /// Asks on the UI thread and returns true only for the confirm button. <paramref name="confirmText"/>
    /// names the action ("Remove", "Clear"); without one the buttons read Yes / No in the UI language.
    /// The owner defaults to the active window, so a question raised from inside a dialog stays on top of it.
    /// </summary>
    public static bool Ask(
        string title,
        string message,
        string? confirmText = null,
        string? cancelText = null,
        bool isWarning = false,
        Window? owner = null)
    {
        var vm = new ConfirmDialogViewModel(
            title,
            message,
            confirmText ?? LocalizationManager.Get("Confirm_Yes"),
            cancelText ?? LocalizationManager.Get("Confirm_No"),
            isWarning);

        return Show(vm, owner) && vm.DialogResult;
    }

    /// <summary>
    /// Asks with two ways to go ahead and one way back ("Merge" / "Replace" / "Cancel"). Cancel is the
    /// default button and the answer for a window closed any other way.
    /// </summary>
    public static ConfirmChoice Choose(
        string title,
        string message,
        string confirmText,
        string alternateText,
        bool isWarning = false,
        Window? owner = null)
    {
        var vm = new ConfirmDialogViewModel(
            title, message, confirmText, LocalizationManager.Get("Cancel"), isWarning, alternateText);

        return Show(vm, owner) ? vm.Choice : ConfirmChoice.Cancel;
    }

    private static bool Show(ConfirmDialogViewModel vm, Window? owner)
    {
        var dlg = new ConfirmDialog(vm);
        var ownerWindow = owner ?? ActiveWindow();
        if (ownerWindow is not null && !ReferenceEquals(ownerWindow, dlg) && ownerWindow.IsVisible)
            dlg.Owner = ownerWindow;
        else
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        return dlg.ShowDialog() == true;
    }

    private static Window? ActiveWindow()
    {
        var app = Application.Current;
        if (app is null) return null;
        return app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? app.MainWindow;
    }
}
