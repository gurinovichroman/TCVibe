using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TCV.App.ViewModels;

namespace TCV.App.Views.Panels;

public partial class PanelView : UserControl
{
    // Drag-and-drop state
    private Point _tabDragStart;
    private TabViewModel? _tabDragSource;

    public PanelView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    // ── Tab strip overflow (6.52) ────────────────────────────────────────────

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is PanelViewModel oldVm)
        {
            oldVm.PropertyChanged -= OnPanelPropertyChanged;
            oldVm.Tabs.CollectionChanged -= OnTabsChanged;
        }

        if (e.NewValue is PanelViewModel newVm)
        {
            newVm.PropertyChanged += OnPanelPropertyChanged;
            newVm.Tabs.CollectionChanged += OnTabsChanged;
        }
    }

    private void OnPanelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PanelViewModel.ActiveTab))
        {
            BringActiveTabIntoViewLater();
        }
    }

    private void OnTabsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        BringActiveTabIntoViewLater();

    private void OnTabStripSizeChanged(object sender, SizeChangedEventArgs e) => BringActiveTabIntoViewLater();

    /// <summary>
    /// Scrolls the strip so the active tab is in view — a new tab is added at the end, Ctrl+Tab
    /// walks off the edge. Deferred until the tab's container exists and has been laid out.
    /// </summary>
    private void BringActiveTabIntoViewLater() =>
        Dispatcher.InvokeAsync(() =>
        {
            if (DataContext is PanelViewModel { ActiveTab: { } tab }
                && TabBar.ItemContainerGenerator.ContainerFromItem(tab) is FrameworkElement container)
            {
                container.BringIntoView();
            }
        }, System.Windows.Threading.DispatcherPriority.Loaded);

    /// <summary>The ▾ button is only there while some tabs are out of view.</summary>
    private void OnTabStripScrollChanged(object sender, ScrollChangedEventArgs e) =>
        TabOverflowButton.Visibility = TabScroller.ScrollableWidth > 0.5 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The wheel scrolls the strip sideways; the strip has no vertical scrolling to give it to.</summary>
    private void OnTabStripMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (TabScroller.ScrollableWidth <= 0)
        {
            return;
        }

        TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    /// <summary>Lists every tab of the panel, the active one checked; picking one switches to it.</summary>
    private void OnTabOverflowClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not PanelViewModel vm)
        {
            return;
        }

        var menu = new ContextMenu
        {
            PlacementTarget = TabOverflowButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        foreach (var tab in vm.Tabs)
        {
            // A TextBlock header, not a string: a menu item reads "_" as an access key, and a folder
            // called "my_project" must keep its underscore.
            var item = new MenuItem
            {
                Header = new TextBlock { Text = tab.DisplayTitle },
                ToolTip = tab.Path,
                IsCheckable = false,
                IsChecked = tab.IsActive,
            };
            var target = tab;
            item.Click += (_, _) => vm.SwitchTabCommand.Execute(target);
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    // ── Status line: view modes ──────────────────────────────────────────────

    private void OnViewModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Settings.ViewMode mode })
        {
            FileListView.ShowAs(mode);
        }
    }

    // ── Filter box ───────────────────────────────────────────────────────────

    /// <summary>
    /// The box takes the keys when it opens, by its button or by a letter typed into the list. The list
    /// used to keep them and lose them: the grid drops its focused row when the filter refills it, and the
    /// third letter went nowhere. Closing with the keys in it (Esc, a folder opened with Enter) gives them
    /// back to the list, on the cursor.
    /// </summary>
    private void OnFilterBoxVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            FocusFilterBoxLater();
        }
        else if (FilterBox.IsKeyboardFocusWithin)
        {
            FileListView.FocusList(scrollIntoView: true);
        }
    }

    /// <summary>
    /// A letter typed into the list went into the filter; the next ones go into the box. At once, not
    /// later: the letter refilled the list, the grid's focused row went with the old rows, and letters
    /// already on their way would land on that row and vanish.
    /// </summary>
    private void OnQuickFilterTyped(object? sender, EventArgs e)
    {
        if (FilterBox.IsVisible)
        {
            FilterBox.TakeKeys();
        }
        else
        {
            FocusFilterBoxLater();
        }
    }

    /// <summary>
    /// Ctrl+S (6.87): the filter box with the keys in it, before any letter is typed — Total Commander's
    /// quick search key. Open already, it just takes the keys back. The tree has no filter.
    /// </summary>
    public void OpenQuickFilter()
    {
        if (DataContext is not PanelViewModel { FileList.CanQuickFilter: true } vm)
        {
            return;
        }

        if (FilterBox.IsVisible)
        {
            FilterBox.TakeKeys();
        }
        else
        {
            // Shown, the box takes the keys itself (OnFilterBoxVisibleChanged).
            vm.FileList.IsFilterOpen = true;
        }
    }

    // After the keys already queued: they still reach the list, which adds them to the filter too.
    private void FocusFilterBoxLater() =>
        Dispatcher.InvokeAsync(FilterBox.TakeKeys, System.Windows.Threading.DispatcherPriority.Input);

    /// <summary>
    /// The keys of the box that are not text (<see cref="Controls.QuickFilterKeys"/>): ↑ ↓ and the page
    /// keys walk the cursor through the matches (with Shift they mark, as in the list), Esc drops the
    /// filter, and Backspace in an empty box closes it. Enter, F3, F5 and the other commands reach the
    /// list through MainWindow before the box sees them.
    /// </summary>
    private void OnFilterBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not PanelViewModel vm)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
            case Key.Back when FilterBox.Text.Length == 0 && Keyboard.Modifiers == ModifierKeys.None:
                vm.FileList.IsFilterOpen = false;
                FileListView.FocusList(scrollIntoView: true);
                e.Handled = true;
                break;
            case Key.Up or Key.Down or Key.PageUp or Key.PageDown:
                e.Handled = FileListView.MoveCursorFromFilter(e.Key, Keyboard.Modifiers);
                break;
            case Key.Return:
                // Only when Enter opens nothing (it was rebound): back to the list with the filter kept.
                FileListView.FocusList(scrollIntoView: true);
                e.Handled = true;
                break;
        }
    }

    private void OnFileListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not PanelViewModel panelVm) return;

        // In the box of a name being renamed a double-click selects a word; it opens nothing.
        if (e.OriginalSource is DependencyObject source && IsInNameBox(source))
        {
            return;
        }

        // Only a double-click on a row opens it. The event arrives from the whole list, so a
        // double-click on a column divider (auto-fit), a header, a scroll bar or the empty area
        // under the rows used to open whatever the cursor stood on.
        var clicked = e.OriginalSource switch
        {
            FrameworkElement fe => fe.DataContext,
            FrameworkContentElement fce => fce.DataContext,
            _ => null,
        };
        // A folder in the tree is entered by selecting it; by the second click the cursor is on
        // its ".." and opening that would go straight back up.
        if (clicked is TreeNodeViewModel { IsDirectory: true } or not (FileEntryViewModel or TreeNodeViewModel))
        {
            return;
        }

        var item = panelVm.FileList.SelectedItem;
        if (item is null) return;

        if (item.IsParentEntry)
        {
            panelVm.FileList.NavigateUpCommand.Execute(null);
            return;
        }

        if (item.IsDirectory)
            // Mounted file systems keep POSIX separators; only the local disk uses backslashes
            panelVm.CurrentPath = panelVm.FileList.IsMounted
                ? item.Entry.Path.Value
                : FileSystem.WindowsFileSystem.ToWindowsPath(item.Entry.Path);
        else
        {
            var mainVm = Window.GetWindow(this)?.DataContext as MainViewModel;
            mainVm?.Files.OpenFileCommand.Execute(null);
        }
    }

    private static bool IsInNameBox(DependencyObject source)
    {
        for (DependencyObject? current = source; current is System.Windows.Media.Visual; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
        {
            if (current is Controls.NameEditBox)
            {
                return true;
            }
        }

        return false;
    }



    private void OnTabMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not PanelViewModel vm) return;
        if (sender is Border border && border.DataContext is TabViewModel tab)
        {
            // ── Tab double-click → start rename ─────────────────────────────────────
            if (e.ClickCount == 2)
            {
                if (e.OriginalSource is Button) return;
                vm.StartRenameTabCommand.Execute(tab);
                e.Handled = true;
            }
            // ── Tab click → switch tab ───────────────────────────────────────────────
            else
            {
                // Don't switch if clicking the close button or rename box
                if (e.OriginalSource is Button || e.OriginalSource is TextBox) return;
                if (tab.IsRenaming) return;
                vm.SwitchTabCommand.Execute(tab);

                // Record drag start position
                _tabDragStart = e.GetPosition(null);
                _tabDragSource = tab;
            }
        }
    }



    /// <summary>Middle click closes the tab (browser and editor convention); a locked tab ignores it,
    /// exactly like the close button and Ctrl+W. Any release also ends a pending tab drag.</summary>
    private void OnTabMouseUp(object sender, MouseButtonEventArgs e)
    {
        _tabDragSource = null;

        if (e.ChangedButton != MouseButton.Middle)
        {
            return;
        }

        if (DataContext is PanelViewModel vm && sender is Border { DataContext: TabViewModel tab })
        {
            vm.CloseTabCommand.Execute(tab);
            e.Handled = true;
        }
    }

    // ── Close button: stop propagation to avoid extra switch ────────────────

    private void OnCloseTabButtonClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
    }

    // ── Rename TextBox: focus on show ────────────────────────────────────────

    private void OnRenameBoxVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox tb && (bool)e.NewValue)
        {
            tb.Dispatcher.InvokeAsync(() =>
            {
                tb.SelectAll();
                tb.Focus();
            }, System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void OnRenameBoxPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Prevent tab switching when clicking inside rename box
        e.Handled = false;
    }

    private void OnRenameBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is TabViewModel vm)
        {
            if (e.Key == Key.Return)
            {
                CommitRename(vm, tb.Text);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                vm.IsRenaming = false;
                e.Handled = true;
            }
        }
    }

    private void OnRenameBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is TabViewModel vm && vm.IsRenaming)
            CommitRename(vm, tb.Text);
    }

    private static void CommitRename(TabViewModel vm, string text)
    {
        var trimmed = text.Trim();
        vm.CustomTitle = string.IsNullOrEmpty(trimmed) ? null : trimmed;
        vm.IsRenaming = false;
    }

    // ── Drag-and-drop tab reordering ─────────────────────────────────────────

    private void OnTabBarPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _tabDragSource is null) return;

        var pos = e.GetPosition(null);
        var delta = pos - _tabDragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        DragDrop.DoDragDrop(TabBar, _tabDragSource, DragDropEffects.Move);
        _tabDragSource = null;
    }

    private void OnTabBarDrop(object sender, DragEventArgs e)
    {
        HandleTabDrop(e, targetTab: null);
    }

    private void OnTabDrop(object sender, DragEventArgs e)
    {
        var targetTab = (sender as Border)?.DataContext as TabViewModel;
        HandleTabDrop(e, targetTab);
    }

    private void HandleTabDrop(DragEventArgs e, TabViewModel? targetTab)
    {
        if (DataContext is not PanelViewModel vm) return;
        if (!e.Data.GetDataPresent(typeof(TabViewModel))) return;

        var source = (TabViewModel)e.Data.GetData(typeof(TabViewModel));
        if (source == targetTab) return;

        int targetIndex = targetTab is not null
            ? vm.Tabs.IndexOf(targetTab)
            : vm.Tabs.Count - 1;

        vm.MoveTab(source, targetIndex);
        e.Handled = true;
    }
}
