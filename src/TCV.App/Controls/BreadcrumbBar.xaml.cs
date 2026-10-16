using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TCV.App.ViewModels;

namespace TCV.App.Controls;

public partial class BreadcrumbBar : UserControl
{
    private List<string> _suggestions = [];
    private int _selectedIndex = -1;
    private bool _suppressSuggestions;

    public BreadcrumbBar()
    {
        InitializeComponent();
    }

    private void OnSegmentClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: BreadcrumbSegmentViewModel pathSegment } && DataContext is FileListViewModel vm)
        {
            vm.CurrentPath = pathSegment.Path;
        }
    }

    /// <summary>
    /// A click on the bar's empty part (a segment is a button and keeps its click) starts typing a
    /// path, as in Explorer. It took a double-click before, which nobody found (6.52).
    /// </summary>
    private void OnBorderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || DataContext is not FileListViewModel { IsEditingPath: false } vm)
        {
            return;
        }

        vm.BeginEditPath();
        e.Handled = true;
    }

    /// <summary>
    /// The edit box appeared — after a click or the EditPath command (Ctrl+L): put the caret in it
    /// with the path selected, so typing replaces it. Deferred: a box that has just been made visible
    /// cannot take focus until it is laid out.
    /// </summary>
    private void OnPathTextBoxVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true)
        {
            return;
        }

        Dispatcher.InvokeAsync(() =>
        {
            PathTextBox.Focus();
            PathTextBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnPathTextBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_suggestions.Count == 0) return;

        if (e.Key == Key.Tab || e.Key == Key.Down)
        {
            _selectedIndex = (_selectedIndex + 1) % _suggestions.Count;
        }
        else if (e.Key == Key.Up)
        {
            _selectedIndex = (_selectedIndex - 1 + _suggestions.Count) % _suggestions.Count;
        }
        else
        {
            return;
        }

        ApplySuggestion(_selectedIndex);
        e.Handled = true;
    }

    private void ApplySuggestion(int index)
    {
        string text = PathTextBox.Text;
        int sep = text.LastIndexOfAny(['\\', '/']);
        string basePath = sep < 0 ? "" : text[..(sep + 1)];
        string completed = basePath + _suggestions[index];

        _suppressSuggestions = true;
        if (DataContext is FileListViewModel vm)
        {
            vm.EditablePath = completed;
        }
        PathTextBox.CaretIndex = completed.Length;
        _suppressSuggestions = false;

        SuggestionsList.SelectedIndex = index;
    }

    private async void OnPathTextBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not FileListViewModel vm) return;
        if (e.Key == Key.Return)
        {
            // Handled before the await: the event is over by the time the lookup answers.
            e.Handled = true;
            var typed = vm.EditablePath;

            // The view model leaves an open archive/connection for a disk path and looks a "/pub"
            // path up in the mount, checking existence off the UI thread.
            bool found;
            try { found = await vm.TryNavigateToTypedPathAsync(typed); }
            catch (Exception) { found = false; }   // a dropped connection reads as "not there"

            if (found)
            {
                CloseEditMode(vm);
                ReturnFocusToList();
            }
            else
            {
                MessageBox.Show(string.Format(Localization.LocalizationManager.Get("Breadcrumb_NotFound"), typed), Localization.LocalizationManager.Get("Breadcrumb_NotFoundTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        else if (e.Key == Key.Escape)
        {
            CloseEditMode(vm);
            ReturnFocusToList();
            e.Handled = true;
        }
    }

    private void OnPathTextBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSuggestions) return;
        _selectedIndex = -1;
        UpdateSuggestions(PathTextBox.Text);
    }

    private CancellationTokenSource? _suggestionsCts;

    /// <summary>
    /// Folder completions for the typed path. The disk is read off the UI thread and only once
    /// typing pauses: <c>\\host\</c> on an unreachable server takes the SMB timeout to answer, and
    /// every keystroke must not freeze the window for it (INV-06).
    /// </summary>
    private async void UpdateSuggestions(string text)
    {
        _suggestionsCts?.Cancel();
        var cts = _suggestionsCts = new CancellationTokenSource();

        // A mounted panel shows VFS paths ("/pub"): completing them from the local disk would
        // offer folders of the current drive's root.
        if (DataContext is FileListViewModel { IsMounted: true })
        {
            ShowSuggestions([]);
            return;
        }

        string basePath, prefix;
        if (string.IsNullOrEmpty(text) || text[^1] is '\\' or '/')
        {
            basePath = text;
            prefix = "";
        }
        else
        {
            int sep = text.LastIndexOfAny(['\\', '/']);
            basePath = sep < 0 ? "" : text[..(sep + 1)];
            prefix = sep < 0 ? text : text[(sep + 1)..];
        }

        List<string> found;
        try
        {
            await Task.Delay(150, cts.Token);
            found = await Task.Run(() => ListFolders(basePath, prefix), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;   // superseded by a newer keystroke
        }

        if (cts.IsCancellationRequested) return;
        ShowSuggestions(found);
    }

    private static List<string> ListFolders(string basePath, string prefix)
    {
        try
        {
            if (!Directory.Exists(basePath)) return [];
            return [.. Directory.GetDirectories(basePath)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase)
                .Take(20)];
        }
        catch
        {
            return [];   // access denied or other I/O errors
        }
    }

    private void ShowSuggestions(List<string> suggestions)
    {
        _suggestions = suggestions;
        _selectedIndex = -1;
        SuggestionsList.ItemsSource = _suggestions;
        SuggestionsPopup.IsOpen = _suggestions.Count > 0 && PathTextBox.IsKeyboardFocusWithin;
        SuggestionsList.SelectedIndex = -1;
    }

    private void OnSuggestionClick(object sender, MouseButtonEventArgs e)
    {
        if (SuggestionsList.SelectedItem is string name && DataContext is FileListViewModel vm)
        {
            string text = PathTextBox.Text;
            int sep = text.LastIndexOfAny(['\\', '/']);
            string basePath = sep < 0 ? "" : text[..(sep + 1)];
            _suppressSuggestions = true;
            vm.EditablePath = basePath + name;
            _suppressSuggestions = false;
            PathTextBox.Focus();
            PathTextBox.CaretIndex = vm.EditablePath.Length;
        }
    }

    private void OnPathTextBoxLostFocus(object sender, RoutedEventArgs e)
    {
        // Don't close if focus moved into the suggestions popup
        if (SuggestionsPopup.IsOpen)
        {
            var focused = Keyboard.FocusedElement as DependencyObject;
            if (focused is Visual focusedVisual && SuggestionsPopup.Child is Visual popupChild
                && popupChild.IsAncestorOf(focusedVisual))
            {
                return;
            }
        }
        if (DataContext is FileListViewModel vm)
            CloseEditMode(vm);
    }

    /// <summary>
    /// Enter or Esc hands the keyboard back to the file list of the same panel. Without this the
    /// collapsed box took the focus with it and the arrow keys went nowhere — the path could be
    /// typed from the keyboard (Ctrl+L) but not left from it.
    /// </summary>
    private void ReturnFocusToList()
    {
        for (var node = VisualTreeHelper.GetParent(this); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is Panel panel && panel.Children.OfType<FileListControl>().FirstOrDefault() is { } list)
            {
                list.FocusList();
                return;
            }
        }
    }

    private void CloseEditMode(FileListViewModel vm)
    {
        _suggestionsCts?.Cancel();   // a lookup still in flight must not reopen the popup
        SuggestionsPopup.IsOpen = false;
        _suggestions.Clear();
        _selectedIndex = -1;
        vm.IsEditingPath = false;
    }
}
