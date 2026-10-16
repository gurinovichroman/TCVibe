using FluentAssertions;
using TCV.App.Controls;

namespace TCV.App.Tests;

/// <summary>
/// The title bar on a narrow window: the workspace box has the room first, and the notes after it
/// (drop stack, guest window, version) are shown from the first while they fit whole, never cut in half.
/// </summary>
public class FitPanelTests
{
    [Fact]
    public void CountFitting_AllFit_ShowsAll()
    {
        FitPanel.CountFitting([30, 120, 90], 240).Should().Be(3);
    }

    [Fact]
    public void CountFitting_LastDoesNotFitWhole_HidesItAndWhatFollows()
    {
        FitPanel.CountFitting([30, 120, 90], 239).Should().Be(2);
        FitPanel.CountFitting([30, 120, 90], 100).Should().Be(1);
        FitPanel.CountFitting([30, 120, 90], 0).Should().Be(0);
    }

    [Fact]
    public void CountFitting_CollapsedChild_TakesNoRoom()
    {
        FitPanel.CountFitting([0, 120, 90], 120).Should().Be(2);
    }

    [Fact]
    public void CountFitting_NoLimit_ShowsAll()
    {
        FitPanel.CountFitting([30, 120, 90], double.PositiveInfinity).Should().Be(3);
    }

    [Fact]
    public void Split_RoomForEverything_FirstTakesWhatTheNotesLeave()
    {
        var (shown, firstWidth) = FitPanel.Split(150, 0, [0, 0, 116], 500);

        shown.Should().Be(3);
        firstWidth.Should().Be(384);
    }

    [Fact]
    public void Split_NameAndVersionDoNotBothFit_VersionGivesWay()
    {
        var (shown, firstWidth) = FitPanel.Split(150, 0, [0, 0, 116], 200);

        shown.Should().Be(2, "the collapsed notes take no room, the version does not fit beside the box");
        firstWidth.Should().Be(200);
    }

    [Fact]
    public void Split_FirstWiderThanTheRoom_KeepsAllOfIt()
    {
        var (shown, firstWidth) = FitPanel.Split(300, 0, [40], 200);

        shown.Should().Be(0);
        firstWidth.Should().Be(200);
    }

    [Fact]
    public void Split_LessRoomThanTheFirstChildsMinimum_HidesItAndGivesTheNotesTheRow()
    {
        var (shown, firstWidth) = FitPanel.Split(240, 188, [0, 0, 116], 150);

        shown.Should().Be(3);
        firstWidth.Should().Be(0);
    }

    [Fact]
    public void Split_RoomBetweenMinimumAndWant_KeepsTheFirstNarrowerAndDropsTheNotes()
    {
        var (shown, firstWidth) = FitPanel.Split(240, 188, [116], 200);

        shown.Should().Be(0);
        firstWidth.Should().Be(200);
    }
}
