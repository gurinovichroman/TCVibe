using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TCV.App.Localization;
using TCV.App.Viewer;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

/// <summary>
/// The internal file viewer window (F3 Lister): code / markdown / text / hex / image over any
/// VFS stream.
/// </summary>
public partial class FileViewerDialog : Window
{
    private readonly FileViewerDialogViewModel _viewModel;

    public FileViewerDialog(FileViewerDialogViewModel viewModel)
    {
        InitializeComponent();
        Controls.TitleBarTheme.Follow(this);
        DataContext = viewModel;
        _viewModel = viewModel;

        // Building the document is deferred to Loaded so the theme dictionary is merged and the
        // token brushes resolve; the ViewModel then asks for a rebuild on every mode change.
        _viewModel.DocumentInvalidated += (_, _) => RebuildDocument();
        Loaded += (_, _) =>
        {
            RebuildDocument();
            FocusContent();
        };

        // The window is non-modal and outlives the command that opened it: closing it releases the
        // mount it was reading from and stops a block that is still being read.
        Closed += (_, _) => _viewModel.Dispose();
    }

    /// <summary>
    /// Rebuilds the flow document for the current rich mode. Code and markdown are rendered from
    /// the same decoded text, so switching modes never re-reads the file.
    /// </summary>
    private void RebuildDocument()
    {
        if (!_viewModel.IsRichMode)
        {
            return;
        }

        var theme = Application.Current?.Resources ?? Resources;
        RichView.Document = _viewModel.IsMarkdownMode
            ? ViewerDocumentBuilder.BuildMarkdown(_viewModel.TextContent, _viewModel.WordWrap, theme)
            : ViewerDocumentBuilder.BuildCode(
                _viewModel.TextContent, _viewModel.Language, _viewModel.IsDiffFile,
                _viewModel.WordWrap, theme, _viewModel.ShowLineNumbers);

    }

    /// <summary>
    /// Keyboard focus goes to the content so that arrows, PageUp/PageDown and Home/End scroll it as
    /// soon as the window opens — nothing in a freshly opened window has focus on its own.
    /// </summary>
    private void FocusContent()
    {
        Control target = _viewModel.IsRichMode ? RichView
            : _viewModel.IsHexMode ? HexView
            : _viewModel.IsImageMode ? ImageScroller
            : TextView;
        target.Focus();
    }

    private bool FindHasFocus => FindBox.IsKeyboardFocusWithin;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        switch (e.Key)
        {
            case Key.Escape:
                // Esc leaves the find box first (back to the text), and closes the window on the next press.
                if (FindHasFocus)
                {
                    FocusContent();
                }
                else
                {
                    Close();
                }

                e.Handled = true;
                break;

            case Key.F3:
                // With something to look for, F3 is "find next" (Shift+F3 previous), as in every viewer;
                // with nothing typed it keeps its old job of closing the window.
                if (FindBox.Text.Length > 0)
                {
                    Find(forward: !shift);
                }
                else
                {
                    Close();
                }

                e.Handled = true;
                break;

            // F7 is the Lister's own search key; Ctrl+F the one every other program uses.
            case Key.F7 when Keyboard.Modifiers == ModifierKeys.None:
            case Key.F when Keyboard.Modifiers == ModifierKeys.Control:
                FindBox.Focus();
                FindBox.SelectAll();
                e.Handled = true;
                break;

            case Key.G when Keyboard.Modifiers == ModifierKeys.Control && _viewModel.CanNavigate:
                OffsetBox.Focus();
                OffsetBox.SelectAll();
                e.Handled = true;
                break;

            // N / P: the panel's next / previous file, as in Total Commander's Lister. In the find or the
            // offset box they are letters being typed.
            case Key.N when Keyboard.Modifiers == ModifierKeys.None && !TypingHasFocus:
                OpenSibling(_viewModel.OpenNextFileCommand);
                e.Handled = true;
                break;

            case Key.P when Keyboard.Modifiers == ModifierKeys.None && !TypingHasFocus:
                OpenSibling(_viewModel.OpenPreviousFileCommand);
                e.Handled = true;
                break;
        }
    }

    private bool TypingHasFocus => FindHasFocus || OffsetBox.IsKeyboardFocusWithin;

    private async void OpenSibling(CommunityToolkit.Mvvm.Input.IAsyncRelayCommand command)
    {
        if (!command.CanExecute(null))
        {
            return;
        }

        await command.ExecuteAsync(null);

        // The new file may open in another mode, whose pane was hidden a moment ago and has no focus;
        // after layout, so the pane is visible by then.
        if (!TypingHasFocus)
        {
            _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(FocusContent));
        }
    }

    private void OnFindBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        Find(forward: (Keyboard.Modifiers & ModifierKeys.Shift) == 0);
        e.Handled = true;
    }

    private void OnOffsetBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        _viewModel.GoToOffsetCommand.Execute(null);
        e.Handled = true;
    }

    private void OnFindNext(object sender, RoutedEventArgs e) => Find(forward: true);

    private void OnFindPrevious(object sender, RoutedEventArgs e) => Find(forward: false);

    // Incremental search: the first match appears while typing, starting from where the last one was.
    // It stays inside the window: reading the rest of a big file on every keystroke would stall typing.
    private void OnFindTextChanged(object sender, TextChangedEventArgs e)
    {
        if (FindBox.Text.Length == 0)
        {
            _viewModel.FindStatus = string.Empty;
            return;
        }

        Find(forward: true, fromSelectionStart: true, withinWindow: true);
    }

    /// <summary>
    /// Finds the next (or previous) match in the pane being shown, wrapping around, and says so in the
    /// status line when it wrapped or found nothing. The caret stays in the find box, so pressing
    /// Enter again goes on to the following match instead of typing into the read-only text.
    /// </summary>
    private async void Find(bool forward, bool fromSelectionStart = false, bool withinWindow = false)
    {
        var query = FindBox.Text;
        if (string.IsNullOrEmpty(query))
        {
            return;
        }

        // The colour views are flow documents with no text cursor to search in. Say so and use the
        // plain-text pane over the same window, rather than switching without a word.
        var switched = _viewModel.IsRichMode;
        if (switched)
        {
            _viewModel.ShowTextCommand.Execute(null);
            _viewModel.FindStatus = LocalizationManager.Get("Viewer_FindSwitched");
        }

        if (_viewModel.IsImageMode)
        {
            _viewModel.FindStatus = LocalizationManager.Get("Viewer_FindNoText");
            return;
        }

        var target = _viewModel.IsHexMode ? HexView : TextView;
        var text = target.Text;
        if (string.IsNullOrEmpty(text))
        {
            _viewModel.FindStatus = string.Format(LocalizationManager.Get("Viewer_NotFound"), query);
            return;
        }

        var from = forward
            ? (fromSelectionStart ? target.SelectionStart : target.SelectionStart + target.SelectionLength)
            : target.SelectionStart;

        var index = TextFinder.Find(text, query, from, forward, out var wrapped);

        // Nothing further in this window of a bigger file: go on into the rest of the file before
        // wrapping to the top of the window or saying "not found".
        if (forward && !withinWindow && (index < 0 || wrapped) && _viewModel.CanFindBeyondWindow)
        {
            var next = await _viewModel.FindBeyondWindowAsync(query);
            if (next >= 0)
            {
                target = TextView;
                text = target.Text;
                index = TextFinder.Find(text, query, next, forward: true, out _);
                wrapped = false;
            }
        }

        // The same backwards: nothing earlier in this window, so go on into the part of the file before
        // it, and search back from where the old window began.
        if (!forward && !withinWindow && (index < 0 || wrapped) && _viewModel.CanFindBeforeWindow)
        {
            var limit = await _viewModel.FindBeforeWindowAsync(query);
            if (limit >= 0)
            {
                target = TextView;
                text = target.Text;
                index = TextFinder.Find(text, query, limit, forward: false, out _);
                wrapped = false;
            }
        }

        if (index < 0)
        {
            _viewModel.FindStatus = string.Format(LocalizationManager.Get("Viewer_NotFound"), query);
            return;
        }

        if (wrapped || !switched)
        {
            _viewModel.FindStatus = wrapped ? LocalizationManager.Get(forward ? "Viewer_WrappedDown" : "Viewer_WrappedUp") : string.Empty;
        }

        target.Select(index, query.Length);
        try
        {
            target.ScrollToLine(target.GetLineIndexFromCharacterIndex(index));
        }
        catch
        {
            // GetLineIndexFromCharacterIndex can throw before layout; the selection is enough.
        }
    }
}
