using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TCV.App.Localization;
using TCV.App.ViewModels;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

public partial class SearchDialog : TcvDialog
{
    private SearchDialogViewModel Vm => (SearchDialogViewModel)DataContext;

    public SearchDialogViewModel? Result { get; private set; }

    public SearchDialog(SearchDialogViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;

        // F3 = start search
        InputBindings.Add(new KeyBinding(vm.StartCommand, new KeyGesture(Key.F3)));

        // The window is non-modal: closing it must also stop a search that is still walking the disk.
        Closed += (_, _) => vm.Dispose();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedResult();

    // Enter in the grid opens the row (the grid swallows it, so the default button never sees it).
    private void ResultsGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }
        OpenSelectedResult();
        e.Handled = true;
    }

    private void OpenSelectedResult()
    {
        if (Vm.SelectedResult is { } result)
        {
            // Expose result so the caller can navigate to the file
            Result = Vm;
            // Raise a custom event instead of closing, so search stays open
            NavigateRequested?.Invoke(this, result);
        }
    }

    /// <summary>
    /// Mirrors the grid's multi-selection into the ViewModel. <c>DataGrid.SelectedItems</c> is not
    /// a dependency property and cannot be bound, and the result actions (copy/move/delete) act on
    /// everything the user ticked, not just the focused row.
    /// </summary>
    private void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Apply only the delta: rebuilding the whole list on every change is quadratic for Ctrl+A over thousands of rows.
        foreach (var item in e.RemovedItems.OfType<SearchResultViewModel>())
        {
            Vm.SelectedResults.Remove(item);
        }
        foreach (var item in e.AddedItems.OfType<SearchResultViewModel>())
        {
            Vm.SelectedResults.Add(item);
        }
    }

    private void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        var suggested = Vm.SelectedPreset?.Name ?? string.Empty;
        var vm = new InputDialogViewModel(
            LocalizationManager.Get("Search_PresetSave"),
            LocalizationManager.Get("Search_PresetName"),
            suggested);
        var dlg = new InputDialog(vm) { Owner = this };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(vm.Value))
            Vm.SavePreset(vm.Value);
    }

    private void DeletePreset_Click(object sender, RoutedEventArgs e) => Vm.DeleteSelectedPreset();

    /// <summary>Raised when the user double-clicks a result — the app should navigate the active panel.</summary>
    public event EventHandler<SearchResultViewModel>? NavigateRequested;
}
