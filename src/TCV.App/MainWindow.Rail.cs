using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using TCV.App.Controls;
using TCV.App.Localization;
using TCV.App.Workspaces;

namespace TCV.App;

/// <summary>
/// The workspace rail: its drag and drop (6.79) — a pinned workspace dragged to another place in its
/// group, a folder dropped on the rail to become a workspace in it — and how it reads with many
/// workspaces: fades at an edge with more past it, «name · Alt+N» as the mouse rests on one, and the
/// rail opened wide with the names, which closes as soon as it has been used. Where things land is
/// decided by <see cref="WorkspaceOrder"/> and <see cref="WorkspaceFolderDrop"/>; this part of the
/// window only measures the buttons and draws the marks (<see cref="RailDropAdorner"/>).
/// </summary>
public partial class MainWindow
{
    /// <summary>How far the rail's list fades out at an edge with more past it.</summary>
    private const double RailFadeLength = 24;

    private const double RailNarrowWidth = 64;

    /// <summary>Panels keep at least this much beside the rail opened wide.</summary>
    private const double RailPanelRoom = 240;

    private void InitializeRail()
    {
        _viewModel.Workspaces.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModels.WorkspaceCommands.IsRailExpanded))
            {
                ApplyRailWidth();
            }
        };

        // The pictures of the projects are looked for once the window stands (not in a guest window).
        Loaded += (_, _) => Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            () => _viewModel.Workspaces.StartPictureSearch());
    }

    /// <summary>Where the side bar ends, from the rail's left: the automatic width of the rail opened wide.</summary>
    private double RailSideBarEdge => RailSpace.ActualWidth + FolderTreeColumn.ActualWidth;

    /// <summary>The most the rail opened wide may take, leaving the panels their room.</summary>
    private double RailRoom => (WorkspaceRail.Parent as FrameworkElement)?.ActualWidth - RailPanelRoom ?? RailWidth.Max;

    /// <summary>
    /// Opened wide, the rail is as wide as its edge was dragged, or else reaches the side bar it lies
    /// over: an edge of its own a few pixels short of the bar's left a sliver of tree beside it.
    /// </summary>
    private void ApplyRailWidth() =>
        WorkspaceRail.Width = _viewModel.Workspaces.IsRailExpanded
            ? RailWidth.Expanded(_settings.WorkspaceRailWidth, RailSideBarEdge, RailRoom)
            : RailNarrowWidth;

    private double _railDragStartX;
    private double _railDragStartWidth;

    // From the mouse, not the thumb's own deltas: the thumb moves with the edge, and a width that sticks
    // to the side bar would otherwise leave the edge and the mouse apart.
    private void OnRailWidthDragStarted(object sender, DragStartedEventArgs e)
    {
        _railDragStartX = Mouse.GetPosition(this).X;
        _railDragStartWidth = WorkspaceRail.ActualWidth;
    }

    private void OnRailWidthDragDelta(object sender, DragDeltaEventArgs e) =>
        WorkspaceRail.Width = RailWidth.Dragged(
            _railDragStartWidth + Mouse.GetPosition(this).X - _railDragStartX, RailSideBarEdge, RailRoom).Width;

    private void OnRailWidthDragCompleted(object sender, DragCompletedEventArgs e) =>
        _settings.WorkspaceRailWidth = RailWidth.Dragged(WorkspaceRail.Width, RailSideBarEdge, RailRoom).Stored;

    /// <summary>A double-click on the edge gives the automatic width back.</summary>
    private void OnRailWidthReset(object sender, MouseButtonEventArgs e)
    {
        _settings.WorkspaceRailWidth = 0;
        ApplyRailWidth();
        e.Handled = true;
    }

    private void OnRailScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var scroll = (ScrollViewer)sender;
        var (top, bottom) = ScrollFade.Edges(scroll.VerticalOffset, scroll.ViewportHeight, scroll.ExtentHeight, RailFadeLength);
        scroll.OpacityMask = top > 0 || bottom > 0
            ? RailFadeMask(top, bottom, Math.Min(RailFadeLength, scroll.ViewportHeight / 2) / scroll.ViewportHeight)
            : null;
    }

    /// <summary>Opaque in the middle, thinning to <c>1 - strength</c> over <paramref name="ramp"/> (a fraction
    /// of the height) at each edge.</summary>
    private static Brush RailFadeMask(double top, double bottom, double ramp)
    {
        static Color Alpha(double strength) => Color.FromArgb((byte)Math.Round(255 * (1 - strength)), 0, 0, 0);

        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        mask.GradientStops.Add(new GradientStop(Alpha(top), 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, ramp));
        mask.GradientStops.Add(new GradientStop(Colors.Black, 1 - ramp));
        mask.GradientStops.Add(new GradientStop(Alpha(bottom), 1));
        mask.Freeze();
        return mask;
    }

    /// <summary>
    /// «Name · Alt+3» beside the narrow rail, and «not pinned» under it for the unpinned one; the live
    /// gesture, so a rebound shortcut shows as rebound. Opened wide, the row says the name itself.
    /// </summary>
    private void OnRailWorkspaceToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Workspace workspace } button)
        {
            return;
        }

        if (_viewModel.Workspaces.IsRailExpanded)
        {
            e.Handled = true;
            return;
        }

        var text = new TextBlock();
        text.Inlines.Add(new Run(workspace.Name) { FontWeight = FontWeights.Medium });
        if (WorkspaceOrder.ShortcutNumber(_viewModel.Workspaces.Items, workspace.Name) is { } number
            && GestureFor($"Workspace{number}") is { Length: > 0 } gesture)
        {
            text.Inlines.Add(new Run(" · " + gesture) { Foreground = RailBrush("ForegroundDimBrush") });
        }

        if (!workspace.Pinned)
        {
            text.Inlines.Add(new LineBreak());
            text.Inlines.Add(new Run(LocalizationManager.Get("Rail_UnpinnedTip")) { Foreground = RailBrush("ForegroundDimBrush") });
        }

        button.ToolTip = text;
    }

    /// <summary>
    /// A button on the rail opened wide has been used — a workspace, the switcher, «new», help — so the
    /// rail closes, as an overlay does (its own toggle aside). After the click: the command runs first.
    /// </summary>
    private void OnRailButtonClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Workspaces.IsRailExpanded && e.OriginalSource is not ToggleButton)
        {
            Dispatcher.BeginInvoke(() => _viewModel.Workspaces.IsRailExpanded = false);
        }
    }

    /// <summary>
    /// A press anywhere in the window outside the rail opened wide closes it, and still does what it
    /// does. Not a press in a popup (a menu, the rail's context menu): those are not this window's tree,
    /// and the Workspaces menu's own «expand» item would otherwise reopen what the press closed.
    /// </summary>
    private void OnRailLightDismiss(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.Workspaces.IsRailExpanded || VisualOf(e.OriginalSource as DependencyObject) is not { } pressed)
        {
            return;
        }

        if (IsAncestorOf(pressed) && !WorkspaceRail.IsAncestorOf(pressed))
        {
            _viewModel.Workspaces.IsRailExpanded = false;
        }
    }

    /// <summary>The element itself, or the nearest visual holding a text run.</summary>
    private static Visual? VisualOf(DependencyObject? source)
    {
        while (source is not null and not Visual)
        {
            source = LogicalTreeHelper.GetParent(source);
        }

        return source as Visual;
    }

    /// <summary>Drag data of a rail workspace — its name. A format of its own, so no other target takes it.</summary>
    private const string RailWorkspaceFormat = "TCV.RailWorkspace";

    /// <summary>The gap between two rail buttons (their top margin). A drag that far outside a group is
    /// still in it; the mark is drawn in the middle of it.</summary>
    private const double RailGap = 8;

    private Point _railPressPoint;
    private Workspace? _railPressed;
    private RailDropAdorner? _railMark;
    private AdornerLayer? _railMarkLayer;

    private void OnRailPinnedMouseDown(object sender, MouseButtonEventArgs e)
    {
        _railPressed = RailButtonAt(e.OriginalSource as DependencyObject)?.DataContext as Workspace;
        _railPressPoint = e.GetPosition(this);
    }

    /// <summary>
    /// A press that ended without a drag (a plain click) is forgotten: kept, a later press anywhere else
    /// dragged over the rail would have started dragging this workspace.
    /// </summary>
    private void OnRailPinnedMouseUp(object sender, MouseButtonEventArgs e) => _railPressed = null;

    /// <summary>A press on a pinned workspace that moves past the drag distance starts dragging it.</summary>
    private void OnRailPinnedMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _railPressed = null;
            return;
        }

        if (_railPressed is not { Pinned: true } workspace)
        {
            return;
        }

        var moved = e.GetPosition(this) - _railPressPoint;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _railPressed = null;
        var source = RailButtons().GetValueOrDefault(workspace.Name) ?? (DependencyObject)RailPinnedList;
        DragDrop.DoDragDrop(source, new DataObject(RailWorkspaceFormat, workspace.Name), DragDropEffects.Move);
        ShowRailMark(null);
    }

    private void OnRailDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (RailDraggedName(e) is { } name)
        {
            var target = RailSlotFor(name, e);
            ShowRailMark(target?.MarkOn is { } button ? RailMarkLine(button, target.Value.Below) : null);
            e.Effects = target is null ? DragDropEffects.None : DragDropEffects.Move;
            return;
        }

        if (RailDroppedFolder(e) is not null)
        {
            ShowRailMark(_railMark?.IsFrameOf(WorkspaceRail) == true
                ? _railMark
                : RailDropAdorner.Frame(WorkspaceRail, RailBrush("AccentBrush"), TryFindResource("SelectionBrush") as Brush));
            e.Effects = RailFolderEffect(e.AllowedEffects);
            return;
        }

        ShowRailMark(null);
        e.Effects = DragDropEffects.None;
    }

    /// <summary>
    /// Raised also when a drag passes from a button to the gap next to it; the next DragOver draws the
    /// mark again before anything is rendered, and a drag that really left the rail must not leave it.
    /// </summary>
    private void OnRailDragLeave(object sender, DragEventArgs e) => ShowRailMark(null);

    private void OnRailDrop(object sender, DragEventArgs e)
    {
        ShowRailMark(null);
        e.Handled = true;
        e.Effects = DragDropEffects.None;

        // Both act after the drop has returned (TRAP-84): the drag source waits for it (Explorer, a
        // panel, the rail's own button that the move rebuilds), and the properties window is modal.
        if (RailDraggedName(e) is { } name)
        {
            if (RailSlotFor(name, e) is { } target)
            {
                e.Effects = DragDropEffects.Move;
                Dispatcher.BeginInvoke(() => _viewModel.Workspaces.MoveWorkspace(name, target.Slot));
            }

            return;
        }

        if (RailDroppedFolder(e) is { } folder)
        {
            e.Effects = RailFolderEffect(e.AllowedEffects);
            Dispatcher.BeginInvoke(() => _ = _viewModel.Workspaces.DropFolderAsync(folder));
        }
    }

    /// <summary>The name of the rail workspace being dragged, or null for any other drag.</summary>
    private static string? RailDraggedName(DragEventArgs e) =>
        e.Data.GetDataPresent(RailWorkspaceFormat) ? e.Data.GetData(RailWorkspaceFormat) as string : null;

    /// <summary>
    /// Where a dragged workspace would land: the slot in its group (<see cref="WorkspaceOrder.SlotAt"/>)
    /// and the button the mark goes on — none when the slot is where it already stands. Null outside its
    /// group, which a drag does not leave.
    /// </summary>
    private (int Slot, FrameworkElement? MarkOn, bool Below)? RailSlotFor(string name, DragEventArgs e)
    {
        var items = _viewModel.Workspaces.Items.ToList();
        var dragged = items.FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));
        if (dragged is not { Pinned: true })
        {
            return null;
        }

        var buttons = RailButtons();
        var block = WorkspaceOrder.BlockOf(items, dragged)
            .Select(w => buttons.GetValueOrDefault(w.Name))
            .ToList();
        if (block.Any(b => b is null))
        {
            return null;   // the rail is being rebuilt
        }

        var bounds = block.Select(b =>
        {
            var top = b!.TranslatePoint(default, RailPinnedList).Y;
            return (top, top + b.ActualHeight);
        }).ToList();
        if (WorkspaceOrder.SlotAt(e.GetPosition(RailPinnedList).Y, bounds, RailGap) is not { } slot)
        {
            return null;
        }

        var from = block.IndexOf(buttons[dragged.Name]);
        if (slot == from || slot == from + 1)
        {
            return (slot, null, false);
        }

        return slot < block.Count ? (slot, block[slot], false) : (slot, block[^1], true);
    }

    /// <summary>The rail's pinned workspace buttons by workspace name.</summary>
    private Dictionary<string, FrameworkElement> RailButtons()
    {
        var buttons = new Dictionary<string, FrameworkElement>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<DependencyObject>();
        pending.Push(RailPinnedList);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node is Button { DataContext: Workspace workspace } button)
            {
                buttons.TryAdd(workspace.Name, button);
                continue;
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                pending.Push(VisualTreeHelper.GetChild(node, i));
            }
        }

        return buttons;
    }

    /// <summary>The rail workspace button that <paramref name="source"/> is part of, if any.</summary>
    private Button? RailButtonAt(DependencyObject? source)
    {
        while (source is not null && !ReferenceEquals(source, RailPinnedList))
        {
            if (source is Button { DataContext: Workspace } button)
            {
                return button;
            }

            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }

        return null;
    }

    /// <summary>
    /// The folder a file drop would make a workspace of: one full path that, where looking costs nothing
    /// (a fixed local disk), is a folder. A path on a share is looked at after the drop, off this
    /// thread (INV-06). Null refuses the drop — a file, several items, an archive's or a server's
    /// entries (those carry no paths at all).
    /// </summary>
    private static string? RailDroppedFolder(DragEventArgs e)
    {
        string? path;
        try
        {
            path = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? WorkspaceFolderDrop.SinglePath(e.Data.GetData(DataFormats.FileDrop) as string[])
                : null;
        }
        catch (Exception)
        {
            return null;   // a source that offers the format and then fails to render it
        }

        return path is not null && (!IsOnFixedDisk(path) || Directory.Exists(path)) ? path : null;
    }

    private static bool IsOnFixedDisk(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root)
                   && !root.StartsWith(@"\\", StringComparison.Ordinal)
                   && new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// A link, where the source allows one — the workspace points at the folder, nothing is copied or
    /// moved; else a copy. Never a move: a source that honours it would delete what it gave away.
    /// </summary>
    private static DragDropEffects RailFolderEffect(DragDropEffects allowed) =>
        allowed.HasFlag(DragDropEffects.Link) ? DragDropEffects.Link : allowed & DragDropEffects.Copy;

    /// <summary>The line in the gap above or below a rail button, as wide as the button's tile (the
    /// button itself stretches across the rail, and its name runs under the tile).</summary>
    private RailDropAdorner RailMarkLine(FrameworkElement button, bool below)
    {
        if (_railMark?.Shows(button, below) == true)
        {
            return _railMark;
        }

        var tile = (button as Control)?.Template?.FindName("Tile", button) as FrameworkElement;
        return RailDropAdorner.Line(button, below, RailGap, tile?.ActualWidth ?? button.ActualWidth, RailBrush("AccentBrush"));
    }

    private Brush RailBrush(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    /// <summary>Puts <paramref name="mark"/> up in place of the current one; null takes it down.</summary>
    private void ShowRailMark(RailDropAdorner? mark)
    {
        if (ReferenceEquals(mark, _railMark))
        {
            return;
        }

        // Taken down from the layer it was put on: the button it marks may have left the tree since.
        if (_railMark is not null)
        {
            _railMarkLayer?.Remove(_railMark);
        }

        _railMark = mark;
        _railMarkLayer = mark is null ? null : AdornerLayer.GetAdornerLayer(mark.AdornedElement);
        _railMarkLayer?.Add(mark!);
    }
}
