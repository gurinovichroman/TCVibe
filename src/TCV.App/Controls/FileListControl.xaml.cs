using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using TCV.App.Settings;
using TCV.App.ViewModels;
using TCV.Archives;

namespace TCV.App.Controls;

public partial class FileListControl : UserControl
{
    // Drag source tracking
    private Point _dragStartPoint;
    private bool _isDragging;

    // Deferred until mouse up, so a drag started by the same press still carries every mark: a plain
    // click on one of several marked entries collapses the marks to it, a Ctrl+click on a marked entry
    // unmarks it.
    private FileEntryViewModel? _pendingSelectItem;
    private FileEntryViewModel? _pendingUnmarkItem;

    // Scroll offset saved by OnBeforeDirectoryLoad for state restoration
    private double _savedScrollOffset = double.NaN;

    // Listings that reached the screen (DirectoryLoaded), and whether the cursor and scroll of the last
    // one are still to be put back: the restore runs after the layout, and only for the last listing.
    private int _listingsShown;
    private bool _restorePending;

    public FileListControl()
    {
        InitializeComponent();

        // Enable drop target
        AllowDrop = true;
        Grid.AllowDrop = true;

        DataContextChanged += OnDataContextChanged;

        // The measured columns (LOC, size on disk) are filled for the rows in view only.
        Grid.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnGridScrollChanged));

        // So are the pictures of the Thumbnails view: scrolling, resizing and refilling all report here.
        ThumbnailList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => ScheduleThumbnailRefresh()));

        WatchBriefFont();
    }

    private FileListViewModel? ViewModel => DataContext as FileListViewModel;

    private void OnGridScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // The grid virtualises by row, so its scroll offset and viewport are counted in rows.
        if (e.OriginalSource is not ScrollViewer { ViewportHeight: > 0 } sv)
        {
            return;
        }
        var first = (int)sv.VerticalOffset;
        ViewModel?.SetVisibleRange(first, first + (int)Math.Ceiling(sv.ViewportHeight));
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is FileListViewModel oldVm)
        {
            oldVm.DirectoryLoaded -= OnDirectoryLoaded;
            oldVm.BeforeDirectoryLoad -= OnBeforeDirectoryLoad;
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
            oldVm.ColumnsChanged -= OnColumnsChanged;
            oldVm.ColumnWidthsRequested -= OnColumnWidthsRequested;
            oldVm.Files.CollectionChanged -= OnFilesChanged;
        }
        if (e.NewValue is FileListViewModel newVm)
        {
            newVm.Files.CollectionChanged += OnFilesChanged;
            newVm.DirectoryLoaded += OnDirectoryLoaded;
            newVm.BeforeDirectoryLoad += OnBeforeDirectoryLoad;
            newVm.PropertyChanged += OnViewModelPropertyChanged;
            newVm.ColumnsChanged += OnColumnsChanged;
            newVm.ColumnWidthsRequested += OnColumnWidthsRequested;
            RebuildColumns();
            Dispatcher.InvokeAsync(RestoreSortIndicator, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        SyncWrapViews();
    }

    /// <summary>
    /// Hands the listing to Brief and Thumbnails only while that view is shown. With the stock
    /// WrapPanel a collapsed view built a container per entry on every load: bound while hidden, they
    /// took seven seconds to open a folder of 15 000 files in the details view (TRAP-52). Their panel
    /// virtualises now (6.46), but a hidden view still has no reason to follow every listing, and
    /// Brief's column width is measured only for the view that shows it.
    /// </summary>
    private void SyncWrapViews()
    {
        var vm = ViewModel;
        var thumbnails = vm is { ViewMode: ViewMode.Thumbnails } ? vm.Files : null;

        BindWrapList(BriefList, vm is { ViewMode: ViewMode.Brief } ? vm.Files : null);
        BindWrapList(ThumbnailList, thumbnails);
        UpdateBriefColumnWidth();

        if (thumbnails is null)
        {
            ReleaseThumbnails();
        }
        else
        {
            ScheduleThumbnailRefresh();
        }
    }

    private static void BindWrapList(ListBox list, System.Collections.IEnumerable? items)
    {
        if (ReferenceEquals(list.ItemsSource, items))
        {
            return;
        }

        // The cursor binding leaves before the items and comes back after them: a list emptied
        // under a live two-way binding writes null back into the panel's cursor.
        BindingOperations.ClearBinding(list, Selector.SelectedItemProperty);
        list.ItemsSource = items;
        if (items is not null)
        {
            list.SetBinding(Selector.SelectedItemProperty, new Binding(nameof(FileListViewModel.SelectedItem)));
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileListViewModel.SelectedItem))
        {
            FollowSelectedItem();
            return;
        }

        var vm = ViewModel;
        if (vm is null)
        {
            return;
        }

        // A name opened for renaming may be scrolled away from (or be a marked file far from the
        // cursor): its row must exist for the box to appear in it.
        if (e.PropertyName == nameof(FileListViewModel.NameEditTarget))
        {
            if (vm.NameEditTarget is { } editing)
            {
                BringEntryIntoView(vm, editing);
            }

            return;
        }

        if (e.PropertyName == nameof(FileListViewModel.ViewMode))
        {
            SyncWrapViews();

            // A view switched by a key or the menu takes the keys from here on: the list it replaced
            // is collapsed, and focus left in it went nowhere. After layout, so the rows exist.
            if (vm.IsActive)
            {
                Dispatcher.InvokeAsync(() => FocusCursor(scrollIntoView: true, FocusClaim.Panel),
                    System.Windows.Threading.DispatcherPriority.Background);
            }

            return;
        }

        if (e.PropertyName == nameof(FileListViewModel.IsActive) && vm.IsActive)
        {
            // Every view gets the caret on its cursor. The views without a grid used to be skipped,
            // so Tab moved the frame while the keys still went to the other panel's list.
            Dispatcher.InvokeAsync(() => FocusCursor(scrollIntoView: false, FocusClaim.Panel),
                System.Windows.Threading.DispatcherPriority.Input);
            return;
        }

        // The filter narrowed or widened the list: the cursor's row stays in sight — the match it moved
        // to, or after Esc the file that was found, now among all the others.
        if (e.PropertyName == nameof(FileListViewModel.QuickSearchText))
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (vm.SelectedItem is { } cursor && vm.Files.Contains(cursor))
                {
                    BringEntryIntoView(vm, cursor);
                }
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private static bool MayTakeKeys(FileListViewModel vm, FocusClaim claim)
    {
        var holder = Keyboard.FocusedElement switch
        {
            NameEditBox => KeyHolder.NameEdit,
            QuickFilterBox box => ReferenceEquals(box.DataContext, vm) ? KeyHolder.OwnFilter : KeyHolder.OtherFilter,
            _ => KeyHolder.Other,
        };

        return ListFocus.MayTakeKeys(holder, claim, vm.IsActive);
    }

    /// <summary>
    /// Gives keyboard focus to the cursor of the view that is shown: the grid's current cell, the
    /// Brief row or the Thumbnails tile under the cursor, or the tree. A claim that may not take the
    /// keys (<see cref="FocusClaim"/>) still brings the cursor into view.
    /// </summary>
    private void FocusCursor(bool scrollIntoView, FocusClaim claim)
    {
        var vm = ViewModel;
        if (vm is null)
        {
            return;
        }

        switch (vm.ViewMode)
        {
            case ViewMode.Details:
                if (vm.SelectedItem is { } item)
                {
                    ApplyGridFocus(vm, item, scrollIntoView, claim);
                }
                else if (MayTakeKeys(vm, claim))
                {
                    Grid.Focus();
                }

                break;
            case ViewMode.Brief:
                FocusListCursor(BriefList, vm, scrollIntoView, MayTakeKeys(vm, claim));
                break;
            case ViewMode.Thumbnails:
                FocusListCursor(ThumbnailList, vm, scrollIntoView, MayTakeKeys(vm, claim));
                break;
            case ViewMode.Tree when MayTakeKeys(vm, claim):
                TreeViewPanel.Focus();
                break;
        }
    }

    /// <summary>Focuses the row or tile of the cursor in a Brief/Thumbnails list, or the list itself when it has none.</summary>
    private static void FocusListCursor(ListBox list, FileListViewModel vm, bool scrollIntoView, bool takeKeys = true)
    {
        if (vm.SelectedItem is { } item && ReferenceEquals(list.ItemsSource, vm.Files))
        {
            if (scrollIntoView)
            {
                list.ScrollIntoView(item);
            }

            if (!takeKeys)
            {
                return;
            }

            // The panel virtualises (6.46): an item scrolled to just now gets its container in the next
            // layout pass, so the pass is run here rather than focusing the list instead of the item.
            if (list.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem)
            {
                list.UpdateLayout();
            }

            if (list.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container)
            {
                container.Focus();
                return;
            }
        }

        if (takeKeys)
        {
            list.Focus();
        }
    }

    /// <summary>
    /// Hands the keyboard back to the list, on the row under the cursor — the path bar's edit box
    /// does this when it closes with Enter or Esc, so do the box of a name renamed in place and the
    /// quick filter box. <paramref name="scrollIntoView"/> when the cursor may be out of sight: a
    /// filter just dropped leaves the found file somewhere in the whole listing.
    /// </summary>
    public void FocusList(bool scrollIntoView = false) => FocusCursor(scrollIntoView, FocusClaim.Hand);

    private void BringEntryIntoView(FileListViewModel vm, FileEntryViewModel entry)
    {
        switch (vm.ViewMode)
        {
            case ViewMode.Details:
                Grid.ScrollIntoView(entry);
                break;
            case ViewMode.Brief when ReferenceEquals(BriefList.ItemsSource, vm.Files):
                BriefList.ScrollIntoView(entry);
                break;
            case ViewMode.Thumbnails when ReferenceEquals(ThumbnailList.ItemsSource, vm.Files):
                ThumbnailList.ScrollIntoView(entry);
                break;
        }
    }

    /// <summary>
    /// The cursor was moved by code (Insert / Space advancing to the next row, Home / End, a reload):
    /// bring the view's own focus and scroll position along, so the next arrow key continues from
    /// there and the row is visible. Arrow keys and clicks already leave the two in step, so they do
    /// nothing here.
    /// </summary>
    private void FollowSelectedItem()
    {
        var vm = ViewModel;
        if (vm?.SelectedItem is not { } item)
        {
            return;
        }

        if (vm.ViewMode == ViewMode.Details)
        {
            if (!vm.IsActive || ReferenceEquals(Grid.CurrentItem, item))
            {
                return;
            }

            Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(vm.SelectedItem, item) && !ReferenceEquals(Grid.CurrentItem, item))
                {
                    ApplyGridFocus(vm, item, scrollIntoView: true, FocusClaim.Follow);
                }
            }, System.Windows.Threading.DispatcherPriority.Input);
            return;
        }

        if (vm.ViewMode is not (ViewMode.Brief or ViewMode.Thumbnails))
        {
            return;
        }

        var list = vm.ViewMode == ViewMode.Brief ? BriefList : ThumbnailList;
        if (list.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem { IsKeyboardFocused: true })
        {
            return;
        }

        // Before this the cursor of Brief and Thumbnails moved by code stayed where it was on screen and
        // under the keyboard: Space marked a file and the next arrow went on from the one before.
        Dispatcher.InvokeAsync(() =>
        {
            if (!ReferenceEquals(vm.SelectedItem, item) || !ReferenceEquals(list.ItemsSource, vm.Files))
            {
                return;
            }

            list.ScrollIntoView(item);
            if (MayTakeKeys(vm, FocusClaim.Follow))
            {
                FocusListCursor(list, vm, scrollIntoView: false);
            }
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>
    /// The listing was refilled (a folder opened, the quick filter narrowed it, a sort, hidden files
    /// shown). Thumbnails asks again which pictures it needs; every view puts the focus back on the
    /// cursor, since a refilled list drops its rows and the focused one with them — the keys typed next
    /// went nowhere. The grid was left out and lost the keys whenever the cursor stayed on its row: a
    /// quick search typed into the list stopped at its second letter (TRAP-78).
    /// </summary>
    private void OnFilesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null || vm.ViewMode == ViewMode.Tree)
        {
            return;
        }

        if (vm.ViewMode == ViewMode.Thumbnails)
        {
            ScheduleThumbnailRefresh();
        }

        // Now, before the list lays the new names out.
        UpdateBriefColumnWidth();

        if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
        {
            return;
        }

        // The refill took away the row that had the keys (F8 deleted it): WPF leaves the focus on the
        // removed cell and still counts it as in the list, and the keys went nowhere until the cursor's
        // new row was laid out and focused — longer when the watcher's reload overtook the restore. The
        // list holds them meanwhile and moves the cursor as its rows would. Normal comes before the next
        // key and the layout (TRAP-87).
        Dispatcher.InvokeAsync(() =>
        {
            if (vm.IsActive && IsKeyboardFocusWithin && IsRemoved(Keyboard.FocusedElement))
            {
                ShownList(vm)?.Focus();
            }
        }, System.Windows.Threading.DispatcherPriority.Normal);

        Dispatcher.InvokeAsync(() =>
        {
            // Only focus the list lost: not a path being typed in the breadcrumb or another window's.
            // No scrolling of its own: a cursor that moved is scrolled to by FollowSelectedItem, and a
            // refresh that kept it must not pull a list the user scrolled away back to it. A cursor the
            // refill took away is back already after a refresh (the view model put it on the next row),
            // and after a folder change the load puts it back (OnDirectoryLoaded).
            var focused = Keyboard.FocusedElement;
            if (vm.IsActive
                && (IsKeyboardFocusWithin || IsFocusLost(focused))
                && (vm.SelectedItem is null || vm.Files.Contains(vm.SelectedItem)))
            {
                FocusCursor(scrollIntoView: false, FocusClaim.Follow);
            }
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>
    /// The keys are nowhere: no element, the bare window, or a row the refill took away. WPF leaves the
    /// focus on a grid cell whose row was removed (its item reads as disconnected), and keys typed there
    /// reach nobody (TRAP-78).
    /// </summary>
    private static bool IsFocusLost(IInputElement? focused) => focused is null or Window || IsRemoved(focused);

    /// <summary>The element was taken out of the window: a row of a refilled list (TRAP-78).</summary>
    private static bool IsRemoved(IInputElement? element) => element switch
    {
        FrameworkElement { DataContext: var item } when ReferenceEquals(item, BindingOperations.DisconnectedSource) => true,
        DependencyObject removed => PresentationSource.FromDependencyObject(removed) is null,
        _ => false,
    };

    /// <summary>The list control of the view that is shown, or null in the Tree view.</summary>
    private Control? ShownList(FileListViewModel vm) => vm.ViewMode switch
    {
        ViewMode.Details => Grid,
        ViewMode.Brief => BriefList,
        ViewMode.Thumbnails => ThumbnailList,
        _ => null,
    };

    private void OnBeforeDirectoryLoad(object? sender, EventArgs e)
    {
        // A load that overtakes one still waiting to restore keeps its offset: the rows were replaced
        // since, and the grid may already have scrolled back to the top. That holds while a load is on
        // its way, and also from the moment a listing lands until its restore has run after the layout.
        if (!double.IsNaN(_savedScrollOffset) && (_restorePending || ViewModel?.HasNewerLoadInFlight == true))
        {
            return;
        }

        var sv = FindDescendant<ScrollViewer>(Grid);
        _savedScrollOffset = sv?.VerticalOffset ?? double.NaN;
    }

    private void OnColumnsChanged(object? sender, EventArgs e) => RebuildColumns();

    private void OnColumnWidthsRequested(object? sender, EventArgs e) => CaptureColumnWidths();

    private void RestoreSortIndicator()
    {
        var vm = ViewModel;
        if (vm is null) return;

        var memberPath = vm.SortBy switch
        {
            SortColumn.Name => "Name",
            SortColumn.Extension => "Extension",
            SortColumn.Size => "Size",
            SortColumn.Date => "LastModified",
            SortColumn.Attributes => "Attributes",
            _ => null,
        };

        foreach (var col in Grid.Columns)
        {
            col.SortDirection = col.SortMemberPath == memberPath
                ? (vm.SortDescending
                    ? System.ComponentModel.ListSortDirection.Descending
                    : System.ComponentModel.ListSortDirection.Ascending)
                : null;
        }
    }

    private void OnDirectoryLoaded(object? sender, EventArgs e)
    {
        var listing = ++_listingsShown;
        _restorePending = true;
        Dispatcher.InvokeAsync(() =>
        {
            // A newer load raises this event again and restores the cursor; the saved state and scroll
            // offset wait for it. That is so while it is still on its way, and also when it has landed
            // too while this waited for the layout: otherwise this one spends the state and the newer
            // one drops the cursor on the first row.
            var vm = ViewModel;
            if (listing != _listingsShown || vm?.HasNewerLoadInFlight == true)
            {
                return;
            }

            // The offset saved for this restore is spent by it, whatever it does. Left over, it came
            // back with the next refresh over rows it was not saved for: a folder opened meanwhile.
            _restorePending = false;
            var savedScroll = _savedScrollOffset;
            _savedScrollOffset = double.NaN;
            if (vm is null)
            {
                return;
            }

            if (vm.Files.Count == 0)
            {
                _ = vm.ConsumePendingViewState();
                return;
            }

            // Priority 1: explicit navigation target (navigate into/up folder)
            var selectName = vm.ConsumePendingSelectName();
            if (selectName is not null)
            {
                // A refresh saved alongside it (F6 renaming in place) is spent here too: left pending,
                // the next plain navigation restored it in another folder.
                _ = vm.ConsumePendingViewState();

                // Exact first (a tar may hold "A" and "a"), then ignoring case: a path typed as
                // "c:\projects\foo" names the folder "Foo" on disk.
                var target = vm.Files.FirstOrDefault(f => f.Name == selectName)
                    ?? vm.Files.FirstOrDefault(f => string.Equals(f.Name, selectName, StringComparison.OrdinalIgnoreCase))
                    ?? FilteredCursor(vm)
                    ?? vm.Files[0];
                vm.SelectedItem = target;
                ApplyGridFocus(vm, target, scrollIntoView: true, FocusClaim.Follow);
                return;
            }

            // Priority 2: restore state saved by PrepareForRefresh()
            var viewState = vm.ConsumePendingViewState();
            if (viewState is not null)
            {
                RestoreViewState(vm, viewState, savedScroll);
                return;
            }

            // Priority 3: default — initial load, watcher-triggered reload, etc.
            var first = FilteredCursor(vm) ?? vm.Files[0];
            vm.SelectedItem = first;
            ApplyGridFocus(vm, first, scrollIntoView: true, FocusClaim.Follow);

        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// The cursor a quick filter typed while the folder was being read has already put on its best match
    /// (<see cref="FileListViewModel.CursorAfterFilter"/>), or null. The first row is "..": put there,
    /// Enter left the folder instead of opening what was typed.
    /// </summary>
    private static FileEntryViewModel? FilteredCursor(FileListViewModel vm) =>
        vm.QuickSearchText.Length > 0 && vm.SelectedItem is { IsParentEntry: false } cursor && vm.Files.Contains(cursor)
            ? cursor
            : null;

    private void RestoreViewState(FileListViewModel vm, FileListViewModel.ViewState viewState, double scrollOffset)
    {
        // Restore scroll first — before selection, so DataGrid doesn't override it
        if (vm.ViewMode == ViewMode.Details && !double.IsNaN(scrollOffset))
        {
            FindDescendant<ScrollViewer>(Grid)?.ScrollToVerticalOffset(scrollOffset);
        }

        // Usually already in place: the view model put the cursor back with the rows. A cursor moved by a
        // key since then stays where the key put it.
        var focused = FileListViewModel.CursorAfterRefresh(vm.SelectedItem, vm.Files, viewState)!;
        vm.SelectedItem = focused;

        // Restore multi-selection if any
        if (viewState.SelectedItemNames.Count > 0)
        {
            // A reload replaces the view models of entries that changed (a copy just wrote them);
            // the marks are found again by name.
            var byName = viewState.SelectedItemNames.ToHashSet();
            vm.SetMarks(vm.Files.Where(f => !f.IsParentEntry && byName.Contains(f.Name)));
        }

        ApplyGridFocus(vm, focused, scrollIntoView: false, FocusClaim.Follow);
    }

    private void ApplyGridFocus(FileListViewModel vm, FileEntryViewModel target, bool scrollIntoView, FocusClaim claim)
    {
        if (vm.ViewMode != ViewMode.Details) return;
        if (scrollIntoView) Grid.ScrollIntoView(target);
        Grid.CurrentItem = target;
        Grid.CurrentCell = new DataGridCellInfo(target, Grid.Columns[0]);
        Dispatcher.InvokeAsync(() =>
        {
            // A name opened for renaming keeps the keys: focusing the cell would close it. So does a
            // quick filter being typed (FocusClaim).
            if (Grid.CurrentItem is null || !MayTakeKeys(vm, claim))
            {
                return;
            }

            var cell = Grid.Columns[0].GetCellContent(Grid.CurrentItem)?.Parent as DataGridCell;
            cell?.Focus();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    // ──────────────────────────────── DataGrid column header sorting ────────────────────────────────

    private void OnGridSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true; // prevent default DataGrid sorting

        var vm = ViewModel;
        if (vm is null) return;

        var column = e.Column.SortMemberPath switch
        {
            "Name" => SortColumn.Name,
            "Extension" => SortColumn.Extension,
            "Size" => SortColumn.Size,
            "LastModified" => SortColumn.Date,
            "Attributes" => SortColumn.Attributes,
            _ => (SortColumn?)null,
        };

        // Measured columns (LOC, size on disk) carry no sort path: their values arrive
        // asynchronously and sorting a half-filled list would reshuffle under the user.
        if (!column.HasValue) return;

        vm.SetSort(column.Value);

        // Update visual sort direction indicators
        foreach (var col in Grid.Columns)
            col.SortDirection = null;
        e.Column.SortDirection = vm.SortDescending
            ? System.ComponentModel.ListSortDirection.Descending
            : System.ComponentModel.ListSortDirection.Ascending;
    }

    // ──────────────────────────────── Tree mode ────────────────────────────────

    private bool _treeSelectionChanging;

    /// <summary>The node selected to follow the panel may be below the fold; its header is scrolled into view.</summary>
    private void OnTreeViewItemSelected(object sender, RoutedEventArgs e) => BringTreeItemHeaderIntoView(e);

    /// <summary>
    /// Scrolls a freshly selected tree item's header into view — the header only: the whole item of an
    /// open folder includes its subtree, and scrolling to that would jump the tree on every click.
    /// Shared by the panel's Tree view and the docked folder tree.
    /// </summary>
    internal static void BringTreeItemHeaderIntoView(RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem item)
        {
            var header = item.Template?.FindName("PART_Header", item) as FrameworkElement;
            (header ?? item).BringIntoView();
        }
    }

    private void OnTreeViewSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_treeSelectionChanging) return;
        if (e.NewValue is not TreeNodeViewModel node || string.IsNullOrEmpty(node.FullPath)) return;

        var vm = ViewModel;
        if (vm is null) return;
        if (vm.IsBuildingTree) return;

        // The tree shows the disk: picking a node while an archive or server is open leaves it first,
        // or the disk path would be handed to the mounted file system.
        if (vm.IsMounted)
        {
            vm.UnmountAll();
        }

        if (!node.IsDirectory)
        {
            // File selected: navigate to parent dir and highlight the file, don't set CurrentPath to a file
            var parentDir = System.IO.Path.GetDirectoryName(node.FullPath);
            if (string.IsNullOrEmpty(parentDir)) return;

            if (string.Equals(vm.CurrentPath, parentDir, StringComparison.OrdinalIgnoreCase))
            {
                // Already in the right directory — just select the file in the list
                var entry = vm.Files.FirstOrDefault(f => string.Equals(f.Name, node.Name, StringComparison.OrdinalIgnoreCase));
                if (entry != null) vm.SelectedItem = entry;
            }
            else
            {
                vm.SetPendingSelectName(node.Name);
                _treeSelectionChanging = true;
                vm.CurrentPath = parentDir;
                _treeSelectionChanging = false;
            }
            return;
        }

        if (!string.Equals(vm.CurrentPath, node.FullPath, StringComparison.OrdinalIgnoreCase))
        {
            _treeSelectionChanging = true;
            vm.CurrentPath = node.FullPath;
            _treeSelectionChanging = false;
        }
    }

    // ──────────────────────────────── View mode (the panel's status line) ────────────────────────────────

    /// <summary>A view button of the panel was clicked: this list changes how it shows its files.</summary>
    public void ShowAs(ViewMode mode)
    {
        var vm = ViewModel;
        if (vm is null)
        {
            return;
        }

        vm.ViewMode = mode;

        // The button was clicked, so the keys follow the list (the panel may not be the active one yet).
        Dispatcher.InvokeAsync(() => FocusCursor(scrollIntoView: true, FocusClaim.Hand), System.Windows.Threading.DispatcherPriority.Background);
    }

    // ──────────────────────────────── Keyboard input ────────────────────────────────

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var vm = ViewModel;
        if (vm is null)
        {
            return;
        }

        // A name being renamed in place takes its own keys (NameEditBox).
        if (e.OriginalSource is TextBoxBase)
        {
            return;
        }

        // Esc with the keys in the list (a click left the box open) drops the edit before anything else.
        if (e.Key == Key.Escape && vm.NameEditTarget is not null)
        {
            vm.EndNameEdit(commit: false);
            e.Handled = true;
            return;
        }

        // Selection, Backspace and Enter are dispatched by MainWindow through HotkeyManager so
        // they stay rebindable. Quick search itself is typed text (OnPreviewTextInput below).
        // Esc closes the filter box too: closing drops the filter, and a cleared box left open would
        // stay on screen with nothing in it.
        if (e.Key == Key.Escape && (vm.QuickSearchText.Length > 0 || vm.IsFilterOpen))
        {
            vm.IsFilterOpen = false;
            vm.QuickSearchText = string.Empty;
            e.Handled = true;
            return;
        }

        if (TryMoveCursor(e.Key, vm, e.OriginalSource))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// The cursor keys the list takes from its views (<see cref="CursorKeys.TryTake"/>) move the panel's
    /// cursor — the view model's, shared by all views — through <see cref="FileListViewModel.MoveCursorTo"/>,
    /// and Shift marks the entries passed over on top of the marks already made. Not a command (INV-05):
    /// like the arrows, these keys move the cursor rather than act on files.
    /// </summary>
    private bool TryMoveCursor(Key key, FileListViewModel vm, object source)
    {
        var modifiers = Keyboard.Modifiers;
        if (source is TextBoxBase || !CursorKeys.TryTake(key, vm.ViewMode, modifiers, out var move))
        {
            return false;
        }

        if (move is not null)
        {
            MoveCursor(vm, move.Value, extendMarks: (modifiers & ModifierKeys.Shift) != 0);
        }

        return true;
    }

    /// <summary>
    /// ↑ ↓ and the page keys pressed in the panel's quick filter box: the cursor walks through the matches
    /// while the keys stay in the box, and Shift marks on the way, as in the list. False for any other
    /// key, and in the Tree view, which the filter does not narrow.
    /// </summary>
    public bool MoveCursorFromFilter(Key key, ModifierKeys modifiers)
    {
        var vm = ViewModel;
        if (vm is null || vm.ViewMode == ViewMode.Tree || (modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0)
        {
            return false;
        }

        // Jumping, every name is listed: ↑ ↓ go to the previous or next match, not the next row (6.87).
        if (vm.QuickSearchJumps && key is Key.Up or Key.Down && vm.QuickSearchText.Length > 0)
        {
            vm.MoveToNextMatch(forward: key == Key.Down, extendMarks: (modifiers & ModifierKeys.Shift) != 0);
            return true;
        }

        GridMove? move = key switch
        {
            Key.Up => GridMove.Up,
            Key.Down => GridMove.Down,
            Key.PageUp => GridMove.PageUp,
            Key.PageDown => GridMove.PageDown,
            _ => null,
        };
        if (move is null)
        {
            return false;
        }

        MoveCursor(vm, move.Value, extendMarks: (modifiers & ModifierKeys.Shift) != 0);
        return true;
    }

    private void MoveCursor(FileListViewModel vm, GridMove move, bool extendMarks)
    {
        var count = vm.Files.Count;
        if (count == 0)
        {
            return;
        }

        var (perLine, linesPerPage) = MeasureLines(vm.ViewMode);
        var index = vm.SelectedItem is { } cursor ? Math.Max(0, vm.Files.IndexOf(cursor)) : 0;
        var target = WrapGrid.Move(index, count, perLine, move, linesPerPage, CursorKeys.FlowOf(vm.ViewMode));
        vm.MoveCursorTo(target, extendMarks);
    }

    /// <summary>
    /// How the shown view lays out its entries now: entries per line (tiles per row in Thumbnails, names
    /// per column in Brief, one row in Details) and the whole lines in view, which is how far the page
    /// keys go. (1, 1) before the first layout.
    /// </summary>
    private (int PerLine, int LinesPerPage) MeasureLines(ViewMode view)
    {
        switch (view)
        {
            case ViewMode.Thumbnails when MeasureThumbnailRows() is { } rows:
                return (rows.Columns, WrapGrid.LinesPerPage(rows.ViewportHeight, rows.RowHeight));
            case ViewMode.Brief when MeasureBriefColumns() is { } columns:
                return columns;
            case ViewMode.Details when FindDescendant<ScrollViewer>(Grid) is { ViewportHeight: > 0 } sv:
                // The grid scrolls by row, so its viewport is counted in rows.
                return (1, Math.Max(1, (int)Math.Floor(sv.ViewportHeight)));
            default:
                return (1, 1);
        }
    }

    /// <summary>
    /// A character typed into the list went into the quick filter; the panel hands the next keys to the
    /// filter box (<see cref="QuickFilterBox"/>).
    /// </summary>
    public event EventHandler? QuickFilterTyped;

    /// <summary>
    /// Quick search takes the characters the keyboard layout produced, not physical keys: with a
    /// Russian layout "д" must filter by "д", not by "l", and digits, dots, dashes and capitals
    /// are part of file names too. A key already handled as a gesture never gets here. The first
    /// character opens the filter here; the rest are typed into its box.
    /// </summary>
    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        base.OnPreviewTextInput(e);
        var vm = ViewModel;
        if (vm is null || string.IsNullOrEmpty(e.Text)) return;

        // Typed into a name being renamed in place: text for the box, not a quick search.
        if (e.OriginalSource is TextBoxBase)
        {
            return;
        }

        // Ctrl or Alt chords are commands (Ctrl+Alt together is AltGr, which types, TRAP-07);
        // a space marks the file (Total Commander) rather than typing.
        var chord = Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt);
        if (chord is ModifierKeys.Control or ModifierKeys.Alt) return;
        if (e.Text.Any(c => char.IsControl(c) || c == ' ')) return;

        // The tree is not the list the filter narrows: there the letters find a node (6.87).
        if (vm.ViewMode == ViewMode.Tree)
        {
            TypeInTree(vm, e.Text);
            e.Handled = true;
            return;
        }

        vm.QuickSearchText += e.Text;
        e.Handled = true;
        QuickFilterTyped?.Invoke(this, EventArgs.Empty);
    }

    private readonly Navigation.TypeAhead _treeTypeAhead = new();

    /// <summary>
    /// A letter typed in the Tree view: the cursor goes to the next node in sight whose name starts with
    /// what was typed (<see cref="Navigation.TypeAhead"/>), as in Explorer's folder tree. Selecting a node
    /// is the panel's navigation, which the tree's selection handler does.
    /// </summary>
    private void TypeInTree(FileListViewModel vm, string text)
    {
        var nodes = new List<TreeNodeViewModel>();
        CollectVisible(vm.TreeRoots, nodes);
        var current = nodes.FindIndex(n => n.IsSelected);
        var prefix = _treeTypeAhead.Type(text, DateTime.UtcNow);
        var index = Navigation.TypeAhead.Find(nodes.Select(n => n.Name).ToList(), current, prefix);
        if (index < 0 || index == current)
        {
            return;
        }

        var target = nodes[index];
        target.IsSelected = true;

        // The keys stay with the tree on the new node: its arrows go on from there.
        Dispatcher.InvokeAsync(() =>
        {
            if (Descendants<TreeViewItem>(TreeViewPanel).FirstOrDefault(i => ReferenceEquals(i.DataContext, target)) is { } item)
            {
                item.Focus();
            }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>The nodes a person sees, top to bottom: the roots and the children of every open node.</summary>
    private static void CollectVisible(IEnumerable<TreeNodeViewModel> level, List<TreeNodeViewModel> into)
    {
        foreach (var node in level)
        {
            into.Add(node);
            if (node.IsExpanded)
            {
                CollectVisible(node.Children, into);
            }
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var deeper in Descendants<T>(child))
            {
                yield return deeper;
            }
        }
    }

    // ──────────────────────────────── Right-click context menu ────────────────────────────────

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);

        // The button that dragged files released them: no menu on top of the drop.
        if (_rightDragged)
        {
            _rightDragged = false;
            e.Handled = true;
            return;
        }

        var vm = ViewModel;
        if (vm is null) return;

        var mainVm = Window.GetWindow(this)?.DataContext as MainViewModel;
        if (mainVm is null) return;

        // The box of a name being renamed keeps its own Cut / Copy / Paste menu.
        if (FindAncestor<TextBoxBase>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        // A column header gets the column chooser — the grid's own menu, which never opened because
        // every right-click below is handled.
        if (e.OriginalSource is DependencyObject header
            && FindAncestor<System.Windows.Controls.Primitives.DataGridColumnHeader>(header) is not null)
        {
            ColumnsMenu.PlacementTarget = Grid;
            ColumnsMenu.IsOpen = true;
            e.Handled = true;
            return;
        }

        // File under cursor (null means no file — fall back to current folder). Read from the data
        // context of what was clicked, not from a grid row: Brief and Thumbnails have no rows, and
        // a right-click there used to open the folder's menu instead of the file's.
        var clicked = e.OriginalSource switch
        {
            FrameworkElement fe => fe.DataContext,
            FrameworkContentElement fce => fce.DataContext,
            _ => null,
        };
        var target = clicked is FileEntryViewModel { IsParentEntry: false } fev ? fev : null;

        if (target is not null && !target.IsSelected)
        {
            // Clicked on a file that is not marked — it becomes the only thing the menu acts on
            vm.DeselectAll();
            Grid.SelectedItem = target;
            vm.SelectedItem = target;
        }
        // Else: clicked on an already-marked item — keep the marks as they are

        var selectedItems = vm.SelectedFiles.Count > 0
            ? (IReadOnlyList<FileEntryViewModel>)vm.SelectedFiles
            : (target is not null ? [target] : []);

        // The shell menu needs real Windows paths — archive and remote entries have none. Those
        // panels get the manager's own menu instead of falling through to the column chooser.
        if (ShellMenuFolder(vm, selectedItems) is not { } folder)
        {
            ShowMountedContextMenu(mainVm);
            e.Handled = true;
            return;
        }

        var screen = PointToScreen(e.GetPosition(this));
        var extended = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        mainVm.Files.ShowContextMenuCommand.Execute(new ContextMenuRequest(target, selectedItems, folder, screen, extended));
        e.Handled = true;
    }

    private static string? ShellMenuFolder(FileListViewModel vm, IReadOnlyList<FileEntryViewModel> selectedItems) =>
        vm.ShellMenuFolderFor(selectedItems.Select(f => f.Entry).ToList());

    /// <summary>
    /// Context menu for a panel showing an archive, a server or search results, where the Windows
    /// shell menu cannot work (no real paths). The commands are the ones the F-key bar runs.
    /// </summary>
    private void ShowMountedContextMenu(MainViewModel mainVm, FrameworkElement? anchor = null)
    {
        var files = mainVm.Files;
        // From the mouse the menu opens at the pointer; from the keyboard (anchor) under the cursor row.
        var menu = anchor is null
            ? new ContextMenu { PlacementTarget = this }
            : new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        var window = Window.GetWindow(this) as MainWindow;

        // The shortcut shown is the one bound now, so a rebind in the hotkey dialog shows here too.
        // Main-menu headers carry access keys ("Copy full _path"); here they are shown plain.
        void Add(string headerKey, ICommand command, string commandId)
        {
            menu.Items.Add(new MenuItem
            {
                Header = MenuHeaderText.Plain(Localization.LocalizationManager.Get(headerKey)),
                InputGestureText = window?.GestureFor(commandId) ?? string.Empty,
                Command = command,
            });
        }

        Add("Files_CtxOpen", files.OpenFileCommand, "OpenFile");
        Add("Files_CtxView", files.ViewFileCommand, "View");
        Add("Menu_Edit", files.EditFileCommand, "Edit");
        menu.Items.Add(new Separator());
        Add("Op_Copy", files.CopyCommand, "Copy");
        Add("Op_Move", files.MoveCommand, "Move");

        // An archive format the engine cannot write (RAR, 7z, ISO, CAB…), browsed or searched, takes no
        // rename, delete or new folder: those are left out rather than offered and refused (TRAP-58).
        var shown = ViewModel!.ActiveVfs is FileSystem.SearchResultsFileSystem results ? results.Inner : ViewModel.ActiveVfs;
        if (shown is not ArchiveFileSystem archive || archive.SupportsOperation(TCV.Core.Domain.VfsOperation.Rename))
        {
            Add("Op_Rename", files.RenameCommand, "Rename");
            Add("Op_Delete", files.DeleteCommand, "Delete");
            Add("Op_NewFolder", files.NewFolderCommand, "NewFolder");
        }
        menu.Items.Add(new Separator());
        Add("Menu_CopyFullPath", files.CopyFullPathToClipboardCommand, "CopyFullPath");

        // A link anyone can open, for files in S3 or Azure Blob (6.81): an item per lifetime.
        if (Services.ReadLinks.SourceOf(ViewModel!.ActiveVfs) is not null)
        {
            var link = new MenuItem
            {
                Header = MenuHeaderText.Plain(Localization.LocalizationManager.Get("Menu_CopyReadLink")),
            };
            foreach (var (hours, headerKey) in Services.ReadLinks.Lifetimes)
            {
                link.Items.Add(new MenuItem
                {
                    Header = Localization.LocalizationManager.Get(headerKey),
                    Command = files.CopyReadLinkCommand,
                    CommandParameter = hours.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
            }

            menu.Items.Add(link);
        }

        Add("Refresh", ViewModel.RefreshCommand, "RefreshTab");

        menu.IsOpen = true;
    }

    /// <summary>Stop under a connection being reconnected by itself: the error stays, with Reconnect and Disconnect.</summary>
    private void OnStopReconnect(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this)?.DataContext is MainViewModel shell)
        {
            var panel = ReferenceEquals(shell.LeftPanel.FileList, ViewModel) ? shell.LeftPanel : shell.RightPanel;
            shell.Mounts.StopReconnectCommand.Execute(panel);
        }
    }

    /// <summary>Reconnect or Disconnect under a server's error: the click activated this panel first.</summary>
    private void OnRunConnectionCommand(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id } && Window.GetWindow(this) is MainWindow window)
        {
            window.RunCommand(id);
        }
    }

    // ──────────────────────────────── Keyboard context menu ────────────────────────────────

    /// <summary>
    /// The Menu key and Shift+F10 open the menu of the file under the cursor (6.56). WPF raises this
    /// event on the focused cell and opens the nearest <c>ContextMenu</c> it finds on the way up — the
    /// grid's column chooser, since only mouse right-clicks were routed to the file menu (they are
    /// handled in <see cref="OnMouseRightButtonUp"/> before WPF opens anything). TRAP-54.
    /// </summary>
    protected override void OnContextMenuOpening(ContextMenuEventArgs e)
    {
        base.OnContextMenuOpening(e);
        if (e.Handled)
        {
            return;
        }

        // A column header keeps its chooser; a text box (inline rename) keeps its Cut/Copy/Paste.
        var source = e.OriginalSource as System.Windows.Media.Visual;
        if (FindAncestor<DataGridColumnHeader>(source) is not null || FindAncestor<TextBoxBase>(source) is not null)
        {
            return;
        }

        var vm = ViewModel;
        if (vm is null || Window.GetWindow(this)?.DataContext is not MainViewModel mainVm)
        {
            return;
        }

        e.Handled = true;
        var anchor = CursorAnchor(source, vm.SelectedItem);

        // Marked files are what the menu acts on, as with a right-click on a marked file; with no
        // marks, the file under the cursor; on ".." or an empty folder, the folder itself.
        var target = vm.SelectedItem is { IsParentEntry: false } cursor ? cursor : null;
        var selectedItems = vm.SelectedFiles.Count > 0
            ? (IReadOnlyList<FileEntryViewModel>)vm.SelectedFiles
            : (target is not null ? [target] : []);

        if (ShellMenuFolder(vm, selectedItems) is not { } folder)
        {
            ShowMountedContextMenu(mainVm, anchor);
            return;
        }

        var screen = anchor.PointToScreen(new Point(0, anchor.RenderSize.Height));
        mainVm.Files.ShowContextMenuCommand.Execute(new ContextMenuRequest(target, selectedItems, folder, screen));
    }

    /// <summary>Where a keyboard-opened menu goes: the cursor's row or tile, else the list itself.</summary>
    private FrameworkElement CursorAnchor(System.Windows.Media.Visual? focused, FileEntryViewModel? cursor)
    {
        if (FindAncestor<DataGridRow>(focused) is { } row)
        {
            return row;
        }

        if (focused is FrameworkElement { DataContext: FileEntryViewModel } item)
        {
            return item;   // a Brief entry or a thumbnail tile
        }

        if (cursor is not null && Grid.IsVisible && Grid.ItemContainerGenerator.ContainerFromItem(cursor) is FrameworkElement container)
        {
            return container;
        }

        return this;
    }

    private void OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Navigate into directory or open file — handled by MainViewModel.OpenFile
        if (e.OriginalSource is FrameworkElement { DataContext: FileEntryViewModel })
        {
            var mainVm = Window.GetWindow(this)?.DataContext as MainViewModel;
            mainVm?.Files.OpenFileCommand.Execute(null);
            return;
        }

        // Tree mode: double-click on a file node — SelectedItem was already set by tree selection handler
        if (e.OriginalSource is FrameworkElement { DataContext: TreeNodeViewModel { IsDirectory: false } })
        {
            var mainVm = Window.GetWindow(this)?.DataContext as MainViewModel;
            mainVm?.Files.OpenFileCommand.Execute(null);
        }
    }

    // ──────────────────────────────── Drag source ────────────────────────────────

    // The left button went down in the box of a name being renamed: what follows is placing the caret
    // and selecting text, not a click on a row or the start of a file drag.
    private bool _pressedInNameBox;

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _pendingSelectItem = null;
        _pendingUnmarkItem = null;
        _pressedInNameBox = FindAncestor<TextBoxBase>(e.OriginalSource as DependencyObject) is not null;
        if (_pressedInNameBox)
        {
            base.OnPreviewMouseLeftButtonDown(e);
            return;
        }

        // If the click lands on empty DataGrid space (no row, no column header, no scrollbar),
        // clear selection and focus the grid. Only inside the grid: this preview handler sees the
        // view toolbar and the other view modes too, and swallowing their clicks killed them.
        if (e.OriginalSource is DependencyObject emptySpaceSrc
            && ReferenceEquals(FindAncestor<DataGrid>(emptySpaceSrc), Grid))
        {
            var row = FindAncestor<DataGridRow>(emptySpaceSrc);
            var header = FindAncestor<DataGridColumnHeader>(emptySpaceSrc);
            var scrollBar = FindAncestor<ScrollBar>(emptySpaceSrc);
            if (row is null && header is null && scrollBar is null)
            {
                // Marks only: the grid's own selection is the cursor (SelectionMode="Single", TRAP-60),
                // and SelectedItems.Clear() throws in single mode.
                ViewModel?.DeselectAll();
                Grid.Focus();
                e.Handled = true;
                return;
            }
        }

        if (e.OriginalSource is DependencyObject src && ViewModel is { } vm && EntryUnder(src) is { } fev)
        {
            // Ctrl and Shift clicks change the marks and never reach the list, whose own selection is
            // only the cursor. The list made its selection the marks and dropped every mark it did not
            // hold: Ctrl+click after Ctrl+A left two files marked instead of all but one (TRAP-60).
            var modifiers = Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift);
            if (modifiers != ModifierKeys.None)
            {
                ClickWithModifiers(vm, fev, shift: (modifiers & ModifierKeys.Shift) != 0);
                _dragStartPoint = e.GetPosition(null);
                _isDragging = false;
                e.Handled = true;
                return;
            }

            // A plain click on one of several marked entries keeps them all for a drag; they collapse
            // to the clicked one on release if no drag started.
            if (fev.IsSelected && vm.SelectedFiles.Count > 1)
            {
                _pendingSelectItem = fev;
                _dragStartPoint = e.GetPosition(null);
                _isDragging = false;
                e.Handled = true;
                return;
            }

            // A plain click on an unmarked entry starts over: marks left behind by Insert or a
            // mask would otherwise make the next F8 act on files the click did not choose.
            if (!fev.IsSelected)
            {
                vm.DeselectAll();
            }
        }

        base.OnPreviewMouseLeftButtonDown(e);
        _dragStartPoint = e.GetPosition(null);
        _isDragging = false;
    }

    /// <summary>
    /// Ctrl+click flips the mark of the entry alone and leaves the others (Total Commander, Explorer);
    /// Shift+click marks the range from the cursor (<see cref="FileListViewModel.ExtendMarksTo"/>).
    /// Either moves the cursor to the entry. A marked entry is unmarked on release, so a Ctrl+drag
    /// started on it still carries it.
    /// </summary>
    private void ClickWithModifiers(FileListViewModel vm, FileEntryViewModel entry, bool shift)
    {
        var index = vm.Files.IndexOf(entry);
        if (index < 0)
        {
            return;
        }

        if (shift)
        {
            vm.ExtendMarksTo(entry);
        }
        else
        {
            vm.MoveCursorTo(index, extendMarks: false);
            if (entry.IsSelected)
            {
                _pendingUnmarkItem = entry;
            }
            else
            {
                vm.ToggleSelect(entry);
            }
        }

        // The click never reached the list, so it did not take the focus either.
        FocusCursor(scrollIntoView: false, FocusClaim.Hand);
    }

    /// <summary>The entry whose row (Details) or item (Brief, Thumbnails) <paramref name="source"/> is in.</summary>
    private FileEntryViewModel? EntryUnder(DependencyObject source)
    {
        if (FindAncestor<DataGridRow>(source) is { DataContext: FileEntryViewModel row }
            && ReferenceEquals(FindAncestor<DataGrid>(source), Grid))
        {
            return row;
        }

        return FindAncestor<ListBoxItem>(source) is { DataContext: FileEntryViewModel item } container
            && ItemsControl.ItemsControlFromItemContainer(container) is { } list
            && (ReferenceEquals(list, BriefList) || ReferenceEquals(list, ThumbnailList))
                ? item
                : null;
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);

        // No drag happened: the marks collapse to the clicked entry, or the Ctrl-clicked one is unmarked.
        if (!_isDragging && ViewModel is { } vm)
        {
            if (_pendingSelectItem is { } clicked)
            {
                vm.DeselectAll();
                vm.SelectedItem = clicked;
            }

            if (_pendingUnmarkItem is { IsSelected: true } unmark)
            {
                vm.ToggleSelect(unmark);
            }
        }

        _pendingSelectItem = null;
        _pendingUnmarkItem = null;
    }

    // Right-button drag (Total Commander): moves instead of copying. The entry the button went down
    // on, because a right press does not move the cursor; the drag carries it, or all marks if it is marked.
    private FileEntryViewModel? _rightPressedItem;
    private bool _rightDragged;

    /// <summary>Data format flag: the drag was started with the right button, so the drop moves.</summary>
    private const string RightDragFormat = "TCV.RightDrag";

    protected override void OnPreviewMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseRightButtonDown(e);

        _rightDragged = false;
        _pressedInNameBox = FindAncestor<TextBoxBase>(e.OriginalSource as DependencyObject) is not null;
        _rightPressedItem = e.OriginalSource is DependencyObject src
            && EntryUnder(src) is { IsParentEntry: false } pressed ? pressed : null;
        _dragStartPoint = e.GetPosition(null);
        _isDragging = false;
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        var rightDrag = e.LeftButton != MouseButtonState.Pressed && e.RightButton == MouseButtonState.Pressed;
        if ((e.LeftButton != MouseButtonState.Pressed && !rightDrag) || _isDragging || _pressedInNameBox)
        {
            return;
        }

        if (rightDrag && _rightPressedItem is null)
        {
            return;
        }

        var pos = e.GetPosition(null);
        var delta = pos - _dragStartPoint;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var vm = ViewModel;
        if (vm is null) return;

        // Collect files to drag: selected files or current item
        IReadOnlyList<FileEntryViewModel>? files;
        if (rightDrag)
        {
            files = _rightPressedItem!.IsSelected && vm.SelectedFiles.Count > 0
                ? vm.SelectedFiles
                : [_rightPressedItem];
        }
        else
        {
            files = vm.SelectedFiles.Count > 0
                ? vm.SelectedFiles
                : (vm.SelectedItem is { } item ? [item] : null);
        }

        if (files is null || files.Count == 0) return;

        _isDragging = true;
        _pendingSelectItem = null;  // drag started — keep the marks as they are
        _pendingUnmarkItem = null;

        DataObject dataObject;

        // A remote panel has no FileDrop paths to offer — handing the shell backslashed VFS paths would
        // resolve them against the current drive — so it drags only to the other panel (PanelDrag below).
        // Nor have the hits of a search on a server or in an archive listed in the panel: "/etc/hosts"
        // would be C:\etc\hosts.
        if (!vm.EntriesOnDisk && !vm.IsInsideArchive && !vm.IsRemote) { _isDragging = false; return; }

        if (vm.IsRemote)
        {
            // Nothing to hand the shell, so only another panel of this window can take it (PanelDrag).
            var entries = files.Where(f => f.Entry is not null && !f.IsParentEntry).Select(f => f.Entry).ToList();
            if (entries.Count == 0) { _isDragging = false; return; }

            dataObject = new DataObject();
            dataObject.SetData(PanelDrag.Format, "remote");
            PanelDrag.Current = new PanelDrag(vm, vm.ActiveVfs, entries);
        }
        else if (vm.IsInsideArchive && vm.ActiveVfs is ArchiveFileSystem archiveVfs)
        {
            // Drag from archive: use custom formats, no real FileDrop paths
            var entryPaths = files
                .Where(f => f.Entry is not null && !f.IsParentEntry)
                .Select(f => f.Entry.Path.Value.TrimStart('/'))
                .ToArray();

            if (entryPaths.Length == 0) { _isDragging = false; return; }

            dataObject = new DataObject();
            dataObject.SetData("TCV.ArchivePath", archiveVfs.ArchivePath);
            dataObject.SetData("TCV.ArchiveEntries", entryPaths);
        }
        else
        {
            var paths = files
                .Where(f => f.Entry is not null)
                .Select(f => FileSystem.WindowsFileSystem.ToWindowsPath(f.Entry.Path))
                .Where(p => System.IO.File.Exists(p) || System.IO.Directory.Exists(p))
                .ToArray();

            if (paths.Length == 0) { _isDragging = false; return; }

            dataObject = new DataObject(DataFormats.FileDrop, paths);
            dataObject.SetData("TCV.FilePaths", paths);
            dataObject.SetData("TCV.SourcePath", vm.CurrentPath);
        }

        if (rightDrag)
        {
            dataObject.SetData(RightDragFormat, true);
        }

        _rightHeldDuringDrag = rightDrag;
        try
        {
            DragDrop.DoDragDrop(this, dataObject, DragDropEffects.Copy | DragDropEffects.Move);
        }
        finally
        {
            // A throwing drag loop must not leave the static flag on: every later drop from Explorer would move.
            _isDragging = false;
            PanelDrag.Current = null;
            _rightDragged = rightDrag || _rightHeldDuringDrag;
            _rightHeldDuringDrag = false;
            _rightPressedItem = null;
        }
    }

    // The right button was held at some point of the current drag. At the drop both buttons are
    // already up, so the target cannot see it in KeyStates.
    private static bool _rightHeldDuringDrag;

    /// <summary>
    /// Pressing the other mouse button makes OLE cancel a drag by default. Here it asks for a move
    /// instead: a left drag that also grabbed the right button moves, like a right-button drag.
    /// The drag ends when every button is up, Esc cancels.
    /// </summary>
    protected override void OnQueryContinueDrag(QueryContinueDragEventArgs e)
    {
        base.OnQueryContinueDrag(e);
        if (!_isDragging)
        {
            return;
        }

        var left = e.KeyStates.HasFlag(DragDropKeyStates.LeftMouseButton);
        var right = e.KeyStates.HasFlag(DragDropKeyStates.RightMouseButton);
        if (right)
        {
            _rightHeldDuringDrag = true;
        }

        e.Action = e.EscapePressed ? DragAction.Cancel
            : left || right ? DragAction.Continue
            : DragAction.Drop;
        e.Handled = true;
    }

    protected override void OnGiveFeedback(GiveFeedbackEventArgs e)
    {
        base.OnGiveFeedback(e);
        if (!_isDragging)
        {
            return;
        }

        // The badge names what the drop will do; effects it has no picture for keep the system cursor.
        var scale = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (DragCursor.For(e.Effects, scale) is { } cursor)
        {
            Mouse.SetCursor(cursor);
            e.UseDefaultCursors = false;
            e.Handled = true;
        }
    }

    /// <summary>Shift or the right button (started with it or pressed during the drag) moves; a plain left drag copies.</summary>
    private static bool IsMoveDrag(DragEventArgs e) =>
        e.KeyStates.HasFlag(DragDropKeyStates.ShiftKey)
        || e.KeyStates.HasFlag(DragDropKeyStates.RightMouseButton)
        || _rightHeldDuringDrag
        || e.Data.GetDataPresent(RightDragFormat);

    // ──────────────────────────────── Drop target ────────────────────────────────

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        e.Effects = GetDropEffect(e);
        e.Handled = true;
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        e.Effects = GetDropEffect(e);
        e.Handled = true;
    }

    protected override void OnDragLeave(DragEventArgs e)
    {
        base.OnDragLeave(e);
        e.Handled = true;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        e.Handled = true;

        var vm = ViewModel;
        if (vm is null) return;

        var mainVm = Window.GetWindow(this)?.DataContext as MainViewModel;
        if (mainVm is null) return;

        var isMove = IsMoveDrag(e);

        // Drop from a server panel onto the other panel — the transfer F5 would run
        if (e.Data.GetDataPresent(PanelDrag.Format))
        {
            if (PanelDrag.Current is { } drag && PanelDropAllowed(drag, vm, isMove))
            {
                _ = mainVm.Files.HandleDropFromPanelAsync(drag.Source, drag.Fs, drag.Entries, vm, isMove);
            }

            return;
        }

        // Archive entries dropped on its own ".." — out of the archive, into the folder it lies in
        if (ArchiveExitFolder(e) is { } exitFolder)
        {
            var exitArchive = e.Data.GetData("TCV.ArchivePath") as string;
            var exitEntries = e.Data.GetData("TCV.ArchiveEntries") as string[];
            if (!string.IsNullOrEmpty(exitArchive) && exitEntries?.Length > 0)
            {
                _ = mainVm.Files.HandleDropFromArchiveAsync(exitArchive, exitEntries, exitFolder, isMove);
            }

            return;
        }

        // Drop FROM archive onto regular folder — extract entries
        if (!vm.IsInsideArchive &&
            e.Data.GetDataPresent("TCV.ArchivePath") &&
            e.Data.GetDataPresent("TCV.ArchiveEntries"))
        {
            var archivePath = e.Data.GetData("TCV.ArchivePath") as string;
            var entryPaths = e.Data.GetData("TCV.ArchiveEntries") as string[];
            if (!string.IsNullOrEmpty(archivePath) && entryPaths?.Length > 0)
                _ = mainVm.Files.HandleDropFromArchiveAsync(archivePath, entryPaths, vm.CurrentPath, isMove, vm.IsMounted);
            return;
        }

        // Drop of regular files — into archive or into regular folder
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (paths.Length == 0) return;

        if (vm.IsInsideArchive)
        {
            // Drop regular files INTO archive — archive them
            if (vm.ActiveVfs is ArchiveFileSystem archiveVfs)
                _ = mainVm.Files.HandleDropToArchiveAsync(paths, archiveVfs, isMove);
            return;
        }

        // Same-folder guard
        if (e.Data.GetDataPresent("TCV.SourcePath") &&
            e.Data.GetData("TCV.SourcePath") is string sourcePath &&
            sourcePath == vm.CurrentPath)
            return;

        // A mounted (remote) panel receives the upload through its own file system
        _ = mainVm.Files.HandleDropAsync(paths, vm.CurrentPath, isMove,
            vm.IsMounted ? vm.ActiveVfs : null);
    }

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element != null)
        {
            if (element is T found) return found;
            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private static T? FindDescendant<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent is null) return null;
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            var result = FindDescendant<T>(child);
            if (result is not null) return result;
        }
        return null;
    }

    /// <summary>
    /// Whether <paramref name="target"/> takes what <paramref name="drag"/> carries: the other panel, standing
    /// in a disk folder or on a server that is not read-only. Not an archive (the entries would have to be
    /// repacked) and not a search list, which is no folder.
    /// </summary>
    private static bool PanelDropAllowed(PanelDrag drag, FileListViewModel target, bool isMove) =>
        !ReferenceEquals(target, drag.Source)
        && !(isMove && drag.Source.ActiveConnection is { ReadOnly: true })
        && !target.IsInsideArchive
        && !target.IsSearchResults
        && target.ActiveConnection is not { ReadOnly: true }
        && (target.IsRemote || target.DiskFolder is not null);

    /// <summary>
    /// The folder an archive's own entries are extracted into when dropped on the ".." row of its root, as
    /// Total Commander does: the folder the archive lies in. Null for any other drop. An archive that is a
    /// scratch copy (opened from a server or from inside another archive) lies in no folder of the user's.
    /// </summary>
    private string? ArchiveExitFolder(DragEventArgs e)
    {
        if (ViewModel is not { IsInsideArchive: true, ActiveVfs: ArchiveFileSystem archive } vm
            || ArchiveFileSystem.NormalizePath(vm.CurrentPath).Length != 0
            || !e.Data.GetDataPresent("TCV.ArchiveEntries")
            || e.Data.GetData("TCV.ArchivePath") is not string dragged
            || !string.Equals(dragged, archive.ArchivePath, StringComparison.OrdinalIgnoreCase)
            || e.OriginalSource is not DependencyObject source
            || EntryUnder(source) is not { IsParentEntry: true }
            || TCV.App.Services.TemporaryFiles.Holds(archive.ArchivePath))
        {
            return null;
        }

        var folder = System.IO.Path.GetDirectoryName(archive.ArchivePath);
        return string.IsNullOrEmpty(folder) ? null : folder;
    }

    private DragDropEffects GetDropEffect(DragEventArgs e)
    {
        var vm = ViewModel;

        if (e.Data.GetDataPresent(PanelDrag.Format))
        {
            return PanelDrag.Current is { } drag && vm is not null && PanelDropAllowed(drag, vm, IsMoveDrag(e))
                ? (IsMoveDrag(e) ? DragDropEffects.Move : DragDropEffects.Copy)
                : DragDropEffects.None;
        }

        // Archive entries dragged from an archive panel
        if (e.Data.GetDataPresent("TCV.ArchiveEntries"))
        {
            if (ArchiveExitFolder(e) is not null)
            {
                return IsMoveDrag(e) ? DragDropEffects.Move : DragDropEffects.Copy;
            }

            // Entries are only extracted into a folder on the disk: not into an archive (the drop
            // would do nothing) nor onto a server or search panel, whose path is not a disk path (INV-07).
            if (vm is null || vm.IsMounted)
                return DragDropEffects.None;

            return IsMoveDrag(e) ? DragDropEffects.Move : DragDropEffects.Copy;
        }

        if (!e.Data.GetDataPresent(DataFormats.FileDrop) &&
            !e.Data.GetDataPresent("TCV.FilePaths"))
            return DragDropEffects.None;

        // The search-results list is not a folder: a drop there would land in the drive's root.
        if (vm?.ActiveVfs is TCV.App.FileSystem.SearchResultsFileSystem
            && TCV.App.FileSystem.SearchResultsFileSystem.IsSyntheticPath(vm.CurrentPath))
        {
            return DragDropEffects.None;
        }

        if (!vm?.IsInsideArchive == true &&
            e.Data.GetDataPresent("TCV.SourcePath") &&
            e.Data.GetData("TCV.SourcePath") is string sourcePath &&
            sourcePath == vm?.CurrentPath)
            return DragDropEffects.None;

        return IsMoveDrag(e) ? DragDropEffects.Move : DragDropEffects.Copy;
    }
}
