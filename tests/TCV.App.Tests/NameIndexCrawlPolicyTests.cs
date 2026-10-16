using System.IO;
using FluentAssertions;
using TCV.App.Navigation;

namespace TCV.App.Tests;

/// <summary>
/// What the whole-disk name index skips and what it walks first, so the 400 000-entry cap cuts the
/// system and toolchain noise rather than the user's projects (6.56).
/// </summary>
public sealed class NameIndexCrawlPolicyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tcv-nicp-" + Guid.NewGuid().ToString("N"));

    public NameIndexCrawlPolicyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ── Skipped folders ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("Windows")]
    [InlineData("Program Files")]
    [InlineData("Program Files (x86)")]
    [InlineData("ProgramData")]
    [InlineData("PerfLogs")]
    [InlineData("Recovery")]
    [InlineData("Windows.old")]
    [InlineData("$WinREAgent")]
    public void IsSkipped_SystemFolderAtADriveRoot_IsSkippedEvenForAChosenRoot(string name)
    {
        NameIndexCrawlPolicy.IsSkipped(name, @"C:\", wholeDisk: true).Should().BeTrue();
        NameIndexCrawlPolicy.IsSkipped(name, "D:", wholeDisk: false).Should().BeTrue();
    }

    [Theory]
    [InlineData("Program Files")]
    [InlineData("ProgramData")]
    [InlineData("Recovery")]
    public void IsSkipped_SystemFolderNameInsideAProject_IsKept(string name)
    {
        NameIndexCrawlPolicy.IsSkipped(name, @"D:\Projects\installer", wholeDisk: true).Should().BeFalse();
    }

    [Theory]
    [InlineData("$Recycle.Bin")]
    [InlineData("System Volume Information")]
    [InlineData(".git")]
    public void IsSkipped_AlwaysSkippedFolder_IsSkippedAnywhere(string name)
    {
        NameIndexCrawlPolicy.IsSkipped(name, @"D:\Projects\app", wholeDisk: false).Should().BeTrue();
    }

    [Theory]
    [InlineData("AppData")]
    [InlineData(".nuget")]
    [InlineData(".gradle")]
    [InlineData(".cargo")]
    [InlineData(".vscode")]
    [InlineData("scoop")]
    public void IsSkipped_ProfileNoiseInAUserProfile_IsSkippedOnTheWholeDisk(string name)
    {
        NameIndexCrawlPolicy.IsSkipped(name, @"C:\Users\dev", wholeDisk: true).Should().BeTrue();
        NameIndexCrawlPolicy.IsSkipped(name, @"C:\Users\Default\", wholeDisk: true).Should().BeTrue();
    }

    [Fact]
    public void IsSkipped_AppDataOfAFolderTheUserChose_IsKept()
    {
        NameIndexCrawlPolicy.IsSkipped("AppData", @"C:\Users\dev", wholeDisk: false).Should().BeFalse();
    }

    [Theory]
    [InlineData(@"D:\Projects\app")]          // a project's own .vscode settings are worth finding
    [InlineData(@"C:\Users")]                 // a profile called AppData is still a profile
    [InlineData(@"D:\Backup\Users\dev")]      // not under a drive root's Users
    public void IsSkipped_ProfileNoiseNameOutsideAProfile_IsKept(string parent)
    {
        NameIndexCrawlPolicy.IsSkipped("AppData", parent, wholeDisk: true).Should().BeFalse();
        NameIndexCrawlPolicy.IsSkipped(".vscode", parent, wholeDisk: true).Should().BeFalse();
    }

    [Fact]
    public void IsSkipped_DependencyFolder_FollowsTheWholeDiskSwitch()
    {
        NameIndexCrawlPolicy.IsSkipped("node_modules", @"D:\Projects\app", wholeDisk: true).Should().BeTrue();
        NameIndexCrawlPolicy.IsSkipped("node_modules", @"D:\Projects\app", wholeDisk: false).Should().BeFalse();
    }

    [Theory]
    [InlineData(@"C:\", true)]
    [InlineData("C:", true)]
    [InlineData("d:/", true)]
    [InlineData(@"C:\Users", false)]
    [InlineData(@"\\server\share", false)]
    public void IsDriveRoot_Cases(string path, bool expected)
    {
        NameIndexCrawlPolicy.IsDriveRoot(path).Should().Be(expected);
    }

    // ── Priority ──────────────────────────────────────────────────────────────

    [Fact]
    public void Classify_FolderInsideABookmark_InheritsTheExplicitTier()
    {
        var priority = NameIndexPriority.Create([@"D:\Work\shop"], userProfile: null);

        var (walk, inherited) = priority.Classify(@"D:\Work\shop", NameIndexPriority.Rest);
        priority.Classify(@"D:\Work\shop\src", inherited)
            .Should().Be((NameIndexPriority.Explicit, NameIndexPriority.Explicit));
        walk.Should().Be(NameIndexPriority.Explicit);
    }

    [Fact]
    public void Classify_FolderOnTheWayToABookmark_IsWalkedEarlyButPassesNothingOn()
    {
        var priority = NameIndexPriority.Create([@"D:\Clients\shop"], userProfile: null);

        priority.Classify(@"D:\Clients", NameIndexPriority.Rest)
            .Should().Be((NameIndexPriority.Explicit, NameIndexPriority.Rest));
        priority.Classify(@"D:\Clients\games", NameIndexPriority.Rest)
            .Should().Be((NameIndexPriority.Rest, NameIndexPriority.Rest), "a sibling of the bookmark is ordinary disk");
        priority.ClassifyRoot(@"D:\").Walk.Should().Be(NameIndexPriority.Explicit);
    }

    [Fact]
    public void Classify_UserProfile_IsLikely()
    {
        var priority = NameIndexPriority.Create([], userProfile: @"C:\Users\dev");

        priority.Classify(@"C:\Users\dev", NameIndexPriority.Rest).Inherited.Should().Be(NameIndexPriority.Likely);
        priority.Classify(@"C:\Users", NameIndexPriority.Rest).Walk.Should().Be(NameIndexPriority.Likely);
    }

    [Theory]
    [InlineData(@"D:\Projects", NameIndexPriority.Likely)]
    [InlineData(@"E:\repos", NameIndexPriority.Likely)]
    [InlineData(@"C:\src", NameIndexPriority.Likely)]
    [InlineData(@"D:\Games\Projects", NameIndexPriority.Rest)]
    [InlineData(@"D:\Games", NameIndexPriority.Rest)]
    public void Classify_ProjectFolderNameAtADriveRoot_IsLikely(string path, int expected)
    {
        var priority = NameIndexPriority.Create([], userProfile: null);

        priority.Classify(path, NameIndexPriority.Rest).Inherited.Should().Be(expected);
    }

    [Fact]
    public void Classify_WithoutAPriority_ProjectFolderNameStaysOrdinary()
    {
        // A walk without a priority (a root the user chose) is plain breadth-first.
        NameIndexPriority.None.Classify(@"D:\Projects", NameIndexPriority.Rest)
            .Should().Be((NameIndexPriority.Rest, NameIndexPriority.Rest));
    }

    [Theory]
    [InlineData(@"D:\")]
    [InlineData("D:")]
    [InlineData("/pub/site")]
    [InlineData(@"\\nas\share\proj")]
    [InlineData("relative\\dir")]
    [InlineData("")]
    [InlineData(null)]
    public void Create_DriveRootsAndNonDiskPaths_AreIgnored(string? folder)
    {
        var priority = NameIndexPriority.Create([folder], userProfile: null);

        priority.Classify(@"D:\Games", NameIndexPriority.Rest)
            .Should().Be((NameIndexPriority.Rest, NameIndexPriority.Rest), "a drive root would make the whole drive a priority");
    }

    [Fact]
    public void ClassifyRoot_RootInsideAPriorityFolder_InheritsItsTier()
    {
        var priority = NameIndexPriority.Create([@"D:\Work"], userProfile: null);

        priority.ClassifyRoot(@"D:\Work\shop\src").Should().Be((NameIndexPriority.Explicit, NameIndexPriority.Explicit));
    }

    [Fact]
    public void Classify_TrailingSeparatorsAndSlashes_MatchTheSameFolder()
    {
        var priority = NameIndexPriority.Create([@"D:/Work/shop/"], userProfile: null);

        priority.Classify(@"D:\Work\shop", NameIndexPriority.Rest).Inherited.Should().Be(NameIndexPriority.Explicit);
    }

    // ── The walk with a priority ──────────────────────────────────────────────

    private string Touch(string relative)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    /// <summary>A deep project beside a shallow folder holding more files than the cap.</summary>
    private string DeepProjectBesideNoise()
    {
        for (var i = 0; i < 40; i++)
        {
            Touch($"noise/n{i:00}.dat");
        }

        Touch(@"work\client\app\src\Program.cs");
        return Path.Combine(_dir, @"work\client\app");
    }

    [Fact]
    public void Walk_PlainBreadthFirst_LosesTheDeepProjectAtTheCap()
    {
        DeepProjectBesideNoise();

        var result = NameIndexWalker.Walk([_dir], new NameIndexWalkOptions(MaxEntries: 20), CancellationToken.None);

        result.Truncated.Should().BeTrue();
        result.Entries.Should().NotContain(e => e.Name == "Program.cs", "this is the bug the priority fixes");
    }

    [Fact]
    public void Walk_WithTheProjectAsAPriority_IndexesItBeforeTheCapCutsIn()
    {
        var project = DeepProjectBesideNoise();
        var priority = NameIndexPriority.Create([project], userProfile: null);

        var result = NameIndexWalker.Walk(
            [_dir], new NameIndexWalkOptions(MaxEntries: 20, Priority: priority), CancellationToken.None);

        result.Truncated.Should().BeTrue("the noise still does not fit");
        result.Entries.Should().Contain(e => e.Name == "Program.cs");
        result.Entries.Should().Contain(e => e.Name == "noise", "the folders on the way stay listed");
    }

    [Fact]
    public void Walk_WithoutACap_PriorityChangesTheOrderNotTheContent()
    {
        var project = DeepProjectBesideNoise();
        var priority = NameIndexPriority.Create([project], userProfile: null);

        var plain = NameIndexWalker.Walk([_dir], new NameIndexWalkOptions(), CancellationToken.None);
        var ordered = NameIndexWalker.Walk([_dir], new NameIndexWalkOptions(Priority: priority), CancellationToken.None);

        ordered.Entries.Select(e => e.Path).Should().BeEquivalentTo(plain.Entries.Select(e => e.Path));
        ordered.Truncated.Should().BeFalse();
    }
}
