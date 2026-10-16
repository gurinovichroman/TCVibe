using FluentAssertions;
using TCV.App.Workspaces;

namespace TCV.App.Tests;

public class WorkspaceColorTests
{
    [Theory]
    [InlineData("#4F9CF9", "#4F9CF9")]
    [InlineData("4F9CF9", "#4F9CF9")]
    [InlineData("#4f9cf9", "#4F9CF9")]
    [InlineData("  #4F9CF9  ", "#4F9CF9")]
    public void ValidHex_NormalisesToCanonicalForm(string input, string expected)
    {
        WorkspaceColor.TryNormalize(input, out var normalized).Should().BeTrue();
        normalized.Should().Be(expected);
    }

    [Theory]
    [InlineData("#F00", "#FF0000")]
    [InlineData("abc", "#AABBCC")]
    public void ThreeDigitShorthand_IsExpanded(string input, string expected)
    {
        WorkspaceColor.TryNormalize(input, out var normalized).Should().BeTrue();
        normalized.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_MeansNoColour_AndIsNotAnError(string? input)
    {
        // Clearing the accent is a normal action, not a typo to reject.
        WorkspaceColor.TryNormalize(input, out var normalized).Should().BeTrue();
        normalized.Should().BeEmpty();
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("#GGGGGG")]
    [InlineData("#4F9CF9 extra")]
    public void Garbage_IsRejected(string input)
    {
        WorkspaceColor.TryNormalize(input, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParseRgb_SplitsChannels()
    {
        WorkspaceColor.TryParseRgb("#4F9CF9", out var r, out var g, out var b).Should().BeTrue();
        r.Should().Be(0x4F);
        g.Should().Be(0x9C);
        b.Should().Be(0xF9);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("nonsense")]
    public void TryParseRgb_RefusesBlankOrBroken(string? input)
    {
        // Callers use false as "paint nothing" — it must never throw mid-render.
        WorkspaceColor.TryParseRgb(input, out _, out _, out _).Should().BeFalse();
    }
}

public class WorkspaceGroupingTests
{
    private static Workspace Ws(string name, bool pinned = true, string group = "") =>
        new() { Name = name, Pinned = pinned, Group = group };

    [Fact]
    public void OnlyPinnedWorkspaces_ReachTheSidebar()
    {
        var groups = WorkspaceGrouping.BuildPinnedGroups(new[]
        {
            Ws("kept"),
            Ws("dropped", pinned: false),
        });

        groups.SelectMany(g => g.Items).Select(w => w.Name).Should().Equal("kept");
    }

    [Fact]
    public void UngroupedBlock_ComesFirst_ThenGroupsAlphabetically()
    {
        var groups = WorkspaceGrouping.BuildPinnedGroups(new[]
        {
            Ws("z-work", group: "Work"),
            Ws("loose"),
            Ws("a-oss", group: "OSS"),
        });

        groups.Select(g => g.Header).Should().Equal("", "OSS", "Work");
        groups[0].HasHeader.Should().BeFalse();
        groups[1].HasHeader.Should().BeTrue();
    }

    [Fact]
    public void OrderWithinAGroup_FollowsTheCallersOrder()
    {
        // The store hands over pinned → MRU; the sidebar must not re-sort and disagree with Alt+1..9.
        var groups = WorkspaceGrouping.BuildPinnedGroups(new[]
        {
            Ws("second", group: "Work"),
            Ws("first", group: "Work"),
        });

        groups.Single().Items.Select(w => w.Name).Should().Equal("second", "first");
    }

    [Fact]
    public void GroupNames_AreMatchedCaseInsensitively_AndTrimmed()
    {
        var groups = WorkspaceGrouping.BuildPinnedGroups(new[]
        {
            Ws("a", group: "Work"),
            Ws("b", group: " work "),
        });

        groups.Should().ContainSingle();
        groups[0].Items.Should().HaveCount(2);
    }

    [Fact]
    public void NothingPinned_YieldsNoGroups()
    {
        WorkspaceGrouping.BuildPinnedGroups(new[] { Ws("x", pinned: false) }).Should().BeEmpty();
    }

    [Fact]
    public void NullInput_IsHandled()
    {
        WorkspaceGrouping.BuildPinnedGroups(null).Should().BeEmpty();
    }
}
