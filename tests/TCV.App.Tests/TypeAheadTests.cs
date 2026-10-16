using FluentAssertions;
using TCV.App.Navigation;

namespace TCV.App.Tests;

/// <summary>Letters typed in the panel's Tree view: the next folder in sight whose name starts with them (6.87).</summary>
public class TypeAheadTests
{
    private static readonly string[] Folders = ["bin", "docs", "Downloads", "obj", "src", "Desktop"];

    [Fact]
    public void Find_OneLetter_GoesToTheNextNameWithIt_RoundToTheFirst()
    {
        TypeAhead.Find(Folders, current: 0, "d").Should().Be(1);
        TypeAhead.Find(Folders, current: 1, "d").Should().Be(2, "the same letter again moves on");
        TypeAhead.Find(Folders, current: 5, "d").Should().Be(1, "past the last one it starts over");
    }

    [Fact]
    public void Find_ALongerPrefix_KeepsTheCurrentNameWhileItFits()
    {
        TypeAhead.Find(Folders, current: 0, "do").Should().Be(1, "docs is the first with \"do\" from the cursor");
        TypeAhead.Find(Folders, current: 2, "do").Should().Be(2, "Downloads still fits: it stays");
        TypeAhead.Find(Folders, current: 1, "do").Should().Be(1);
        TypeAhead.Find(Folders, current: 1, "dow").Should().Be(2);
    }

    [Fact]
    public void Find_TheSameLetterRepeated_StepsThroughTheNamesWithIt()
    {
        TypeAhead.Find(Folders, current: 1, "dd").Should().Be(2);
        TypeAhead.Find(Folders, current: 2, "ddd").Should().Be(5);
    }

    [Fact]
    public void Find_NothingSelectedOrNothingFits()
    {
        TypeAhead.Find(Folders, current: -1, "s").Should().Be(4);
        TypeAhead.Find(Folders, current: 0, "zz").Should().Be(-1);
        TypeAhead.Find([], current: -1, "a").Should().Be(-1);
    }

    [Fact]
    public void Type_JoinsLettersTypedInQuickSuccession_AndAPauseStartsAgain()
    {
        var typing = new TypeAhead();
        var start = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

        typing.Type("d", start).Should().Be("d");
        typing.Type("o", start.AddMilliseconds(300)).Should().Be("do");
        typing.Type("s", start.AddMilliseconds(300) + TypeAhead.Pause + TimeSpan.FromMilliseconds(1)).Should().Be("s");
    }
}
