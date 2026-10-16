using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace TCV.App.Controls;

/// <summary>
/// A wrapping items panel that makes containers only for the items in view (6.46): the Brief and
/// Thumbnails views. The stock WrapPanel built and laid out a container for every file, so a folder of
/// 15 000 files took seconds to show in those views (TRAP-52). Every item takes one slot of one size —
/// <see cref="ItemWidth"/> and <see cref="ItemHeight"/> when set, else what the first item in view asks
/// for — which is what lets the panel know where any item is without measuring it.
/// </summary>
/// <remarks>
/// The panel scrolls itself (<see cref="IScrollInfo"/>; the list needs <c>ScrollViewer.CanContentScroll</c>)
/// along one axis: down for <see cref="WrapFlow.Rows"/>, sideways for <see cref="WrapFlow.Columns"/>. The
/// arithmetic is <see cref="WrapLayout"/>. A container that holds the keyboard focus is kept when it
/// scrolls away, as VirtualizingStackPanel keeps it, so the wheel does not drop the focus to the window.
/// <see cref="BringIndexIntoView"/> is what <c>ListBox.ScrollIntoView</c> calls for an item with no
/// container: it scrolls, and the container exists after the next layout pass.
/// </remarks>
public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty FlowProperty = DependencyProperty.Register(
        nameof(Flow), typeof(WrapFlow), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(WrapFlow.Rows, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Lines made beyond each edge of the viewport, so a step of the cursor finds its item ready.</summary>
    private const int CacheLines = 1;

    private Size _itemSize = Size.Empty;
    private Size _viewport;
    private Size _extent;
    private Vector _offset;
    private WrapLayout _layout;

    /// <summary>Rows (Thumbnails, scrolled down) or Columns (Brief, scrolled sideways).</summary>
    public WrapFlow Flow
    {
        get => (WrapFlow)GetValue(FlowProperty);
        set => SetValue(FlowProperty, value);
    }

    /// <summary>The width of every slot; NaN takes the width the first item in view asks for.</summary>
    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    /// <summary>The height of every slot; NaN takes the height the first item in view asks for.</summary>
    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    /// <summary>The layout of the last measure: items per line, lines in view and where each item is.</summary>
    public WrapLayout Layout => _layout;

    /// <summary>The scroll offset along the flow: down in Rows, sideways in Columns.</summary>
    public double OffsetAlong => Flow == WrapFlow.Rows ? _offset.Y : _offset.X;

    // ── Layout ───────────────────────────────────────────────────────────────

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = InternalChildren;   // touched first: it is what attaches the generator
        var count = ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;

        if (count == 0)
        {
            RemoveAll();
            _layout = default;
            UpdateScrollInfo(availableSize, new Size(0, 0));
            return new Size(0, 0);
        }

        // Twice at most: the first pass may only now learn the item size (a new folder, a new font).
        for (var pass = 0; pass < 2; pass++)
        {
            if (_itemSize.IsEmpty)
            {
                SampleItemSize(count);
            }

            _layout = new WrapLayout(count, _itemSize.Width, _itemSize.Height,
                availableSize.Width, availableSize.Height, Flow);

            var along = _layout.ClampOffset(OffsetAlong);
            _offset = Flow == WrapFlow.Rows ? new Vector(0, along) : new Vector(along, 0);

            var (first, last) = _layout.Range(along, CacheLines);
            Realize(first, last);
            Release(first, last);

            if (FirstChildSize() is { } measured && !SameSize(measured, _itemSize))
            {
                _itemSize = measured;
                continue;
            }

            break;
        }

        UpdateScrollInfo(availableSize, new Size(_layout.ExtentWidth, _layout.ExtentHeight));

        // Desired size: the viewport where it is bounded, the extent where it is not.
        return new Size(
            double.IsInfinity(availableSize.Width) ? _extent.Width : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? _extent.Height : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var generator = ItemContainerGenerator;
        var children = InternalChildren;
        for (var i = 0; i < children.Count; i++)
        {
            var index = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index < 0)
            {
                continue;
            }

            var (x, y) = _layout.SlotOf(index);
            children[i].Arrange(new Rect(x - _offset.X, y - _offset.Y, _itemSize.Width, _itemSize.Height));
        }

        return finalSize;
    }

    /// <summary>The slot size, from <see cref="ItemWidth"/>/<see cref="ItemHeight"/> or a realised item.</summary>
    private void SampleItemSize(int count)
    {
        if (!double.IsNaN(ItemWidth) && !double.IsNaN(ItemHeight))
        {
            _itemSize = new Size(ItemWidth, ItemHeight);
            return;
        }

        // Any item will do (one template, one size); the one at the old offset is the likeliest to stay.
        var index = _layout.Count > 0 ? Math.Clamp(_layout.Range(OffsetAlong).First, 0, count - 1) : 0;
        Realize(index, index);
        _itemSize = FirstChildSize() ?? new Size(1, 1);
    }

    /// <summary>The size of the first container, measured for a slot; null when none exists.</summary>
    private Size? FirstChildSize()
    {
        if (InternalChildren.Count == 0)
        {
            return null;
        }

        var child = InternalChildren[0];
        child.Measure(SlotConstraint());
        var desired = child.DesiredSize;
        return new Size(
            double.IsNaN(ItemWidth) ? Math.Max(1, desired.Width) : ItemWidth,
            double.IsNaN(ItemHeight) ? Math.Max(1, desired.Height) : ItemHeight);
    }

    private Size SlotConstraint() => new(
        double.IsNaN(ItemWidth) ? double.PositiveInfinity : ItemWidth,
        double.IsNaN(ItemHeight) ? double.PositiveInfinity : ItemHeight);

    private static bool SameSize(Size a, Size b) => Math.Abs(a.Width - b.Width) < 0.5 && Math.Abs(a.Height - b.Height) < 0.5;

    /// <summary>Makes (or keeps) the containers of the items <paramref name="first"/>..<paramref name="last"/>.</summary>
    private void Realize(int first, int last)
    {
        if (last < first)
        {
            return;
        }

        var generator = ItemContainerGenerator;
        var children = InternalChildren;
        var constraint = SlotConstraint();
        var start = generator.GeneratorPositionFromIndex(first);
        var childIndex = start.Offset == 0 ? start.Index : start.Index + 1;

        using (generator.StartAt(start, GeneratorDirection.Forward, allowStartAtRealizedItem: true))
        {
            for (var index = first; index <= last; index++, childIndex++)
            {
                if (generator.GenerateNext(out var isNew) is not UIElement child)
                {
                    break;
                }

                if (isNew)
                {
                    if (childIndex >= children.Count)
                    {
                        AddInternalChild(child);
                    }
                    else
                    {
                        InsertInternalChild(childIndex, child);
                    }

                    generator.PrepareItemContainer(child);
                }

                child.Measure(constraint);
            }
        }
    }

    /// <summary>Drops the containers outside <paramref name="first"/>..<paramref name="last"/>, except a focused one.</summary>
    private void Release(int first, int last)
    {
        var generator = ItemContainerGenerator;
        var children = InternalChildren;
        for (var i = children.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            var index = generator.IndexFromGeneratorPosition(position);
            if ((index < first || index > last) && !children[i].IsKeyboardFocusWithin)
            {
                generator.Remove(position, 1);
                RemoveInternalChildRange(i, 1);
            }
        }
    }

    private void RemoveAll()
    {
        var children = InternalChildren;
        if (children.Count == 0)
        {
            return;
        }

        ItemContainerGenerator.RemoveAll();
        RemoveInternalChildRange(0, children.Count);
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
                RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
            case NotifyCollectionChangedAction.Move:
                // The container that moved is where the item was, not where it went (as VirtualizingStackPanel).
                RemoveInternalChildRange(args.OldPosition.Index, args.ItemUICount);
                break;
            case NotifyCollectionChangedAction.Reset:
                // A new listing may be another kind of item (or a new font): ask its size again.
                _itemSize = Size.Empty;
                break;
        }

        base.OnItemsChanged(sender, args);
        InvalidateMeasure();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == ItemWidthProperty || e.Property == ItemHeightProperty || e.Property == FlowProperty)
        {
            _itemSize = Size.Empty;
        }
    }

    /// <summary>Scrolls the item's line into view; <c>ListBox.ScrollIntoView</c> asks this for an item with no container.</summary>
    protected override void BringIndexIntoView(int index)
    {
        if (_layout.Count == 0 || index < 0)
        {
            return;
        }

        SetOffsetAlong(_layout.OffsetToShow(index, OffsetAlong));
    }

    // ── IScrollInfo ──────────────────────────────────────────────────────────

    public bool CanHorizontallyScroll { get; set; }

    public bool CanVerticallyScroll { get; set; }

    public double ExtentWidth => _extent.Width;

    public double ExtentHeight => _extent.Height;

    public double ViewportWidth => _viewport.Width;

    public double ViewportHeight => _viewport.Height;

    public double HorizontalOffset => _offset.X;

    public double VerticalOffset => _offset.Y;

    public ScrollViewer? ScrollOwner { get; set; }

    private double Step => Math.Max(16, _layout.LineSize > 0 ? _layout.LineSize : 16);

    public void LineUp() => SetVerticalOffset(_offset.Y - Step);

    public void LineDown() => SetVerticalOffset(_offset.Y + Step);

    public void LineLeft() => SetHorizontalOffset(_offset.X - Step);

    public void LineRight() => SetHorizontalOffset(_offset.X + Step);

    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height);

    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height);

    public void PageLeft() => SetHorizontalOffset(_offset.X - _viewport.Width);

    public void PageRight() => SetHorizontalOffset(_offset.X + _viewport.Width);

    public void MouseWheelUp() => SetVerticalOffset(_offset.Y - (Step * WheelLines));

    public void MouseWheelDown() => SetVerticalOffset(_offset.Y + (Step * WheelLines));

    public void MouseWheelLeft() => SetHorizontalOffset(_offset.X - (Step * WheelLines));

    public void MouseWheelRight() => SetHorizontalOffset(_offset.X + (Step * WheelLines));

    // A notch moves a line of tiles; three would skip most of a short viewport of 118 px rows.
    private static int WheelLines => 1;

    public void SetHorizontalOffset(double offset)
    {
        if (Flow == WrapFlow.Columns)
        {
            SetOffsetAlong(offset);
        }
    }

    public void SetVerticalOffset(double offset)
    {
        if (Flow == WrapFlow.Rows)
        {
            SetOffsetAlong(offset);
        }
    }

    private void SetOffsetAlong(double offset)
    {
        var along = _layout.Count > 0 ? _layout.ClampOffset(offset) : 0;
        if (Math.Abs(along - OffsetAlong) < 0.01)
        {
            return;
        }

        _offset = Flow == WrapFlow.Rows ? new Vector(0, along) : new Vector(along, 0);
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        var children = InternalChildren;
        for (var i = 0; i < children.Count; i++)
        {
            if (!ReferenceEquals(children[i], visual) && !visual.IsDescendantOf(children[i]))
            {
                continue;
            }

            var index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index < 0)
            {
                break;
            }

            SetOffsetAlong(_layout.OffsetToShow(index, OffsetAlong));
            var (x, y) = _layout.SlotOf(index);
            return new Rect(x - _offset.X, y - _offset.Y, _itemSize.Width, _itemSize.Height);
        }

        return Rect.Empty;
    }

    private void UpdateScrollInfo(Size viewport, Size extent)
    {
        viewport = new Size(
            double.IsInfinity(viewport.Width) ? extent.Width : viewport.Width,
            double.IsInfinity(viewport.Height) ? extent.Height : viewport.Height);
        if (viewport == _viewport && extent == _extent)
        {
            return;
        }

        _viewport = viewport;
        _extent = extent;
        ScrollOwner?.InvalidateScrollInfo();
    }
}
