using FluentAssertions;
using TCV.App.Controls;

namespace TCV.App.Tests;

/// <summary>Cursor keys and the visible range of the wrapped views: Thumbnails (tiles in rows) and Brief (names in columns).</summary>
public class WrapGridTests
{
    // 10 tiles, 4 per row:
    //   0 1 2 3
    //   4 5 6 7
    //   8 9
    private const int Count = 10;
    private const int Columns = 4;

    [Theory]
    [InlineData(5, GridMove.Left, 4)]
    [InlineData(5, GridMove.Right, 6)]
    [InlineData(5, GridMove.Up, 1)]
    [InlineData(5, GridMove.Down, 9)]
    [InlineData(5, GridMove.First, 0)]
    [InlineData(5, GridMove.Last, 9)]
    public void Move_InTheMiddle_GoesToTheNeighbour(int from, GridMove move, int expected)
    {
        WrapGrid.Move(from, Count, Columns, move).Should().Be(expected);
    }

    [Theory]
    [InlineData(3, GridMove.Right, 4)]
    [InlineData(4, GridMove.Left, 3)]
    public void Move_LeftAndRight_WrapToTheNextOrPreviousRow(int from, GridMove move, int expected)
    {
        WrapGrid.Move(from, Count, Columns, move).Should().Be(expected);
    }

    [Theory]
    [InlineData(0, GridMove.Left, 0)]
    [InlineData(9, GridMove.Right, 9)]
    [InlineData(2, GridMove.Up, 2)]
    [InlineData(9, GridMove.Down, 9)]
    public void Move_AtTheEdge_StaysPut(int from, GridMove move, int expected)
    {
        WrapGrid.Move(from, Count, Columns, move).Should().Be(expected);
    }

    [Fact]
    public void Move_DownIntoAShorterLastRow_LandsOnTheLastTile()
    {
        // Tile 7 has nothing under it; Explorer goes to the last tile rather than staying.
        WrapGrid.Move(7, Count, Columns, GridMove.Down).Should().Be(9);
    }

    [Fact]
    public void Move_PageDown_KeepsTheColumnAndStopsOnTheLastRow()
    {
        WrapGrid.Move(1, Count, Columns, GridMove.PageDown, linesPerPage: 1).Should().Be(5);
        WrapGrid.Move(1, Count, Columns, GridMove.PageDown, linesPerPage: 5).Should().Be(9);
        WrapGrid.Move(2, Count, Columns, GridMove.PageDown, linesPerPage: 5).Should().Be(9, "row 3 has no third column");
    }

    [Fact]
    public void Move_PageUp_KeepsTheColumnAndStopsOnTheFirstRow()
    {
        WrapGrid.Move(9, Count, Columns, GridMove.PageUp, linesPerPage: 1).Should().Be(5);
        WrapGrid.Move(9, Count, Columns, GridMove.PageUp, linesPerPage: 5).Should().Be(1);
    }

    [Fact]
    public void Move_WithOneColumn_IsAPlainList()
    {
        WrapGrid.Move(3, 5, 1, GridMove.Down).Should().Be(4);
        WrapGrid.Move(3, 5, 1, GridMove.Up).Should().Be(2);
        WrapGrid.Move(3, 5, 0, GridMove.Down).Should().Be(4, "a list not laid out yet counts as one column");
    }

    [Fact]
    public void Move_InAnEmptyList_HasNowhereToGo()
    {
        WrapGrid.Move(0, 0, Columns, GridMove.Down).Should().Be(-1);
    }

    [Fact]
    public void Move_FromAnIndexOutsideTheList_StartsFromTheNearestEnd()
    {
        WrapGrid.Move(42, Count, Columns, GridMove.Left).Should().Be(8);
    }

    [Fact]
    public void VisibleRange_AtTheTop_CoversTheRowsThatTouchTheViewport()
    {
        // Rows of 100 px, a 250 px viewport: rows 0, 1 and the top of row 2.
        WrapGrid.VisibleRange(0, 250, 100, Columns, 40).Should().Be((0, 11));
    }

    [Fact]
    public void VisibleRange_ARowEndingAtTheViewportEdge_IsNotVisible()
    {
        WrapGrid.VisibleRange(0, 200, 100, Columns, 40).Should().Be((0, 7));
    }

    [Fact]
    public void VisibleRange_ScrolledDown_SkipsTheRowsAbove()
    {
        // Scrolled by 130 px: row 0 is gone, row 1 shows its bottom 70 px, rows up to 3 are in view.
        WrapGrid.VisibleRange(-130, 250, 100, Columns, 40).Should().Be((4, 15));
    }

    [Fact]
    public void VisibleRange_WithAMargin_TakesARowOnEachSide()
    {
        WrapGrid.VisibleRange(-130, 250, 100, Columns, 40, marginRows: 1).Should().Be((0, 19));
    }

    [Fact]
    public void VisibleRange_NearTheEnd_StopsAtTheLastItem()
    {
        // Three rows of 100 px scrolled to the bottom of a 100 px viewport: row 2, plus row 1 as margin.
        WrapGrid.VisibleRange(-200, 100, 100, Columns, Count, marginRows: 1).Should().Be((4, 9));
    }

    [Theory]
    [InlineData(0, 250, 100)]
    [InlineData(250, 0, 100)]
    [InlineData(250, 100, 0)]
    public void VisibleRange_WithNothingToShow_IsEmpty(int count, double viewport, double rowHeight)
    {
        var (first, last) = WrapGrid.VisibleRange(0, viewport, rowHeight, Columns, count);

        last.Should().BeLessThan(first);
    }

    [Theory]
    [InlineData(250, 100, 2)]
    [InlineData(99, 100, 1)]
    [InlineData(400, 0, 1)]
    public void LinesPerPage_CountsTheWholeLinesThatFit(double viewport, double rowHeight, int expected)
    {
        WrapGrid.LinesPerPage(viewport, rowHeight).Should().Be(expected);
    }

    // The Brief view: the same 10 items, 4 per column, top to bottom:
    //   0 4 8
    //   1 5 9
    //   2 6
    //   3 7
    private const int Rows = 4;

    [Theory]
    [InlineData(5, GridMove.Up, 4)]
    [InlineData(5, GridMove.Down, 6)]
    [InlineData(5, GridMove.Left, 1)]
    [InlineData(5, GridMove.Right, 9)]
    [InlineData(5, GridMove.First, 0)]
    [InlineData(5, GridMove.Last, 9)]
    public void Move_InColumns_GoesToTheNeighbour(int from, GridMove move, int expected)
    {
        WrapGrid.Move(from, Count, Rows, move, flow: WrapFlow.Columns).Should().Be(expected);
    }

    [Theory]
    [InlineData(3, GridMove.Down, 4)]
    [InlineData(4, GridMove.Up, 3)]
    public void Move_InColumns_UpAndDownWrapToTheNextOrPreviousColumn(int from, GridMove move, int expected)
    {
        WrapGrid.Move(from, Count, Rows, move, flow: WrapFlow.Columns).Should().Be(expected);
    }

    [Theory]
    [InlineData(2, GridMove.Left, 2)]
    [InlineData(9, GridMove.Right, 9)]
    [InlineData(0, GridMove.Up, 0)]
    [InlineData(9, GridMove.Down, 9)]
    public void Move_InColumns_AtTheEdge_StaysPut(int from, GridMove move, int expected)
    {
        WrapGrid.Move(from, Count, Rows, move, flow: WrapFlow.Columns).Should().Be(expected);
    }

    [Fact]
    public void Move_InColumns_RightIntoAShorterLastColumn_LandsOnTheLastItem()
    {
        // Item 7 has nothing to its right; Total Commander goes to the last item rather than staying.
        WrapGrid.Move(7, Count, Rows, GridMove.Right, flow: WrapFlow.Columns).Should().Be(9);
    }

    [Fact]
    public void Move_InColumns_PageKeysMoveByWholeColumnsAndKeepTheRow()
    {
        WrapGrid.Move(1, Count, Rows, GridMove.PageDown, linesPerPage: 1, flow: WrapFlow.Columns).Should().Be(5);
        WrapGrid.Move(2, Count, Rows, GridMove.PageDown, linesPerPage: 5, flow: WrapFlow.Columns).Should().Be(9, "the last column has no third row");
        WrapGrid.Move(9, Count, Rows, GridMove.PageUp, linesPerPage: 1, flow: WrapFlow.Columns).Should().Be(5);
        WrapGrid.Move(9, Count, Rows, GridMove.PageUp, linesPerPage: 5, flow: WrapFlow.Columns).Should().Be(1);
    }

    [Fact]
    public void Move_InOneColumnOfRows_IsThePlainListOfTheDetailsView()
    {
        // The details grid is a list of one item per row: Page Down goes a screenful, then stops on the last row.
        WrapGrid.Move(3, 100, 1, GridMove.PageDown, linesPerPage: 20).Should().Be(23);
        WrapGrid.Move(90, 100, 1, GridMove.PageDown, linesPerPage: 20).Should().Be(99);
        WrapGrid.Move(10, 100, 1, GridMove.PageUp, linesPerPage: 20).Should().Be(0);
    }

    // ── WrapLayout: what the virtualising panel of Brief and Thumbnails makes (6.46) ──

    // Thumbnails: tiles of 100×120 in a 350×250 viewport — three to a row, two whole rows in view.
    private static WrapLayout Tiles(int count) => new(count, 100, 120, 350, 250, WrapFlow.Rows);

    // Brief: names of 200×24 in a 650×240 viewport — ten to a column, three whole columns in view.
    private static WrapLayout Names(int count) => new(count, 200, 24, 650, 240, WrapFlow.Columns);

    [Fact]
    public void WrapLayout_Rows_WrapsAcrossAndScrollsDown()
    {
        var layout = Tiles(10);

        layout.PerLine.Should().Be(3);
        layout.Lines.Should().Be(4);
        layout.ExtentWidth.Should().Be(300);
        layout.ExtentHeight.Should().Be(480);
        layout.WholeLinesInView.Should().Be(2);
        layout.SlotOf(4).Should().Be((100.0, 120.0));
    }

    [Fact]
    public void WrapLayout_Columns_WrapsDownAndScrollsSideways()
    {
        var layout = Names(100);

        layout.PerLine.Should().Be(10);
        layout.Lines.Should().Be(10);
        layout.ExtentWidth.Should().Be(2000);
        layout.ExtentHeight.Should().Be(240);
        layout.WholeLinesInView.Should().Be(3);
        layout.SlotOf(23).Should().Be((400.0, 72.0));
    }

    [Fact]
    public void WrapLayout_Range_IsTheLinesInViewPlusTheMargin()
    {
        var layout = Tiles(10);

        layout.Range(0).Should().Be((0, 8), "rows 0–2 touch the 250 px viewport");
        layout.Range(0, marginLines: 1).Should().Be((0, 9));
        layout.Range(240).Should().Be((6, 9), "scrolled two rows down, the last two rows are in view");
    }

    [Fact]
    public void WrapLayout_Range_OfAHugeFolder_IsOnlyAScreenful()
    {
        // The point of the panel: 15 000 names make containers for the columns in view, not for every name.
        var (first, last) = Names(15_000).Range(120_000, marginLines: 1);

        (last - first + 1).Should().BeLessThanOrEqualTo(60);
        first.Should().Be(5_990);
    }

    [Fact]
    public void WrapLayout_OffsetToShow_ScrollsOnlyAsFarAsTheLineNeeds()
    {
        var layout = Names(100);

        layout.OffsetToShow(25, 0).Should().Be(0, "column 2 is in view already");
        layout.OffsetToShow(75, 0).Should().Be(950, "column 7 ends at 1600: it lands on the right edge");
        layout.OffsetToShow(5, 950).Should().Be(0, "column 0 lands on the left edge");
    }

    [Fact]
    public void WrapLayout_ClampOffset_KeepsTheScrollInsideTheExtent()
    {
        var layout = Names(100);

        layout.ClampOffset(5000).Should().Be(1350);
        layout.ClampOffset(-5).Should().Be(0);
        layout.ClampOffset(double.NaN).Should().Be(0);
        Names(5).ClampOffset(100).Should().Be(0, "everything fits: nothing to scroll");
    }

    [Fact]
    public void WrapLayout_UnboundedViewport_ShowsEverything()
    {
        new WrapLayout(7, 100, 120, double.PositiveInfinity, 250, WrapFlow.Rows).PerLine.Should().Be(7);
        new WrapLayout(7, 100, 120, 350, double.PositiveInfinity, WrapFlow.Rows).Range(0).Should().Be((0, 6));
        new WrapLayout(0, 100, 120, 350, 250, WrapFlow.Rows).Range(0).Should().Be((0, -1));
    }
}
