using System.IO;
using FluentAssertions;
using TCV.App.Navigation;

namespace TCV.App.Tests;

/// <summary>Covers the instant name index added in task 6.21.</summary>
public class NameIndexTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "tcv-index-" + Guid.NewGuid().ToString("N"));

    public NameIndexTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static NameIndex Build(params (string Path, bool IsDirectory)[] entries)
    {
        var index = new NameIndex();
        index.Replace(
            entries.Select(e => new NameIndexEntry(
                e.Path, e.Path[(e.Path.LastIndexOfAny(['\\', '/']) + 1)..], e.IsDirectory)).ToList(),
            [@"D:\"],
            new DateTime(2026, 7, 27));
        return index;
    }

    // ── Querying ──────────────────────────────────────────────────────────────

    [Fact]
    public void Query_Substring_FindsMatchesAnywhereInTheName()
    {
        var index = Build((@"D:\src\Program.cs", false), (@"D:\src\readme.md", false));

        index.Query("gram").Select(e => e.Name).Should().BeEquivalentTo("Program.cs");
    }

    [Fact]
    public void Query_IsCaseInsensitive()
    {
        var index = Build((@"D:\src\Program.cs", false));

        index.Query("PROGRAM").Should().ContainSingle();
    }

    [Fact]
    public void Query_PrefixMatchesRankAboveMatchesInTheMiddle()
    {
        var index = Build(
            (@"D:\a\unrelated-log.txt", false),
            (@"D:\b\log.txt", false));

        index.Query("log")[0].Name.Should().Be("log.txt");
    }

    [Fact]
    public void Query_ShorterNamesRankFirstAtEqualPosition()
    {
        var index = Build((@"D:\a\app.config.backup", false), (@"D:\b\app.cs", false));

        index.Query("app")[0].Name.Should().Be("app.cs");
    }

    [Fact]
    public void Query_Empty_ReturnsTheHeadOfTheIndex()
    {
        var index = Build((@"D:\a.txt", false), (@"D:\b.txt", false), (@"D:\c.txt", false));

        index.Query("", limit: 2).Should().HaveCount(2);
    }

    [Fact]
    public void Query_RespectsTheLimit()
    {
        var index = Build(Enumerable.Range(0, 50)
            .Select(i => ($@"D:\file{i}.txt", false)).ToArray());

        index.Query("file", limit: 10).Should().HaveCount(10);
        index.Query("file", limit: 0).Should().BeEmpty();
    }

    [Fact]
    public void Query_NoMatch_IsEmpty()
    {
        Build((@"D:\a.txt", false)).Query("zzz").Should().BeEmpty();
    }

    [Fact]
    public void Entry_Directory_IsThePathWithoutTheName()
    {
        var entry = new NameIndexEntry(@"D:\src\app\main.cs", "main.cs", false);

        entry.Directory.Should().Be(@"D:\src\app");
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    [Fact]
    public void SaveLoad_RoundTripsEntriesAndMetadata()
    {
        var path = Path.Combine(_dir, "index.json");
        var original = Build((@"D:\src\Program.cs", false), (@"D:\src", true));

        original.Save(path).Should().BeTrue();

        var restored = new NameIndex();
        restored.Load(path).Should().BeTrue();
        restored.Count.Should().Be(2);
        restored.BuiltUtc.Should().Be(new DateTime(2026, 7, 27));
        restored.Roots.Should().BeEquivalentTo([@"D:\"]);
        restored.Query("src").Should().Contain(e => e.IsDirectory && e.Path == @"D:\src");
    }

    [Fact]
    public void Load_MissingFile_IsFalseAndLeavesTheIndexEmpty()
    {
        var index = new NameIndex();

        index.Load(Path.Combine(_dir, "nope.json")).Should().BeFalse();
        index.Count.Should().Be(0);
    }

    [Fact]
    public void Load_CorruptFile_IsFalseRatherThanThrowing()
    {
        var path = Path.Combine(_dir, "broken.json");
        File.WriteAllText(path, "{not json");

        var index = new NameIndex();
        var act = () => index.Load(path);

        act.Should().NotThrow();
        index.Load(path).Should().BeFalse();
    }

    [Fact]
    public void Replace_SwapsTheWholeContent()
    {
        var index = Build((@"D:\old.txt", false));
        index.Replace([new NameIndexEntry(@"D:\new.txt", "new.txt", false)], [@"D:\"], DateTime.UtcNow);

        index.Query("old").Should().BeEmpty();
        index.Query("new").Should().ContainSingle();
    }

    // ── Top-K query ───────────────────────────────────────────────────────────

    [Fact]
    public void Query_ManyHits_ReturnsOnlyTheBestOnesInRankOrder()
    {
        var entries = Enumerable.Range(0, 500)
            .Select(i => ($@"D:\x\{new string('z', i % 40)}a{i:D4}.txt", false))
            .Append((@"D:\x\a.txt", false))       // prefix match, shortest name: must come first
            .ToArray();
        var index = Build(entries);

        var top = index.Query("a", limit: 5);

        top.Should().HaveCount(5);
        top[0].Name.Should().Be("a.txt");
        top.Select(t => t.Name.IndexOf('a')).Should().BeInAscendingOrder();
    }

    [Fact]
    public void Query_TopK_MatchesTheFullSortForEveryLimit()
    {
        var entries = new[] { "beta.txt", "alpha.txt", "gamma-a.txt", "a.txt", "Aardvark.md", "xa.txt", "xxa.txt" }
            .Select(n => ($@"D:\x\{n}", false)).ToArray();
        var index = Build(entries);

        var expected = index.Query("a", limit: 100).Select(e => e.Name).ToList();

        for (var limit = 1; limit <= expected.Count; limit++)
        {
            index.Query("a", limit).Select(e => e.Name).Should().Equal(expected.Take(limit));
        }
    }

    // ── Subtree refresh and truncation ────────────────────────────────────────

    [Fact]
    public void ReplaceSubtree_SwapsOnlyThatFolder()
    {
        var index = Build((@"D:\a\old.txt", false), (@"D:\a", true), (@"D:\b\keep.txt", false));

        index.ReplaceSubtree(@"D:\a", [new NameIndexEntry(@"D:\a\new.txt", "new.txt", false)]);

        index.Query("old").Should().BeEmpty();
        index.Query("new").Should().ContainSingle();
        index.Query("keep").Should().ContainSingle();
    }

    [Fact]
    public void ReplaceSubtree_LeavesSiblingsWithACommonPrefixAlone()
    {
        var index = Build((@"D:\src\a.txt", false), (@"D:\src-old\b.txt", false));

        index.ReplaceSubtree(@"D:\src", []);

        index.Query("a.txt").Should().BeEmpty();
        index.Query("b.txt").Should().ContainSingle();
    }

    [Fact]
    public void ReplaceChildren_SwapsDirectChildrenAndKeepsDeeperEntries()
    {
        var index = Build((@"D:\a\old.txt", false), (@"D:\a\sub", true), (@"D:\a\sub\deep.txt", false), (@"D:\a", true));

        index.ReplaceChildren(@"D:\a", [
            new NameIndexEntry(@"D:\a\sub", "sub", true),
            new NameIndexEntry(@"D:\a\new.txt", "new.txt", false),
        ]);

        index.Query("old").Should().BeEmpty();
        index.Query("new").Should().ContainSingle();
        index.Query("deep").Should().ContainSingle();
        index.Query("sub").Should().ContainSingle();
        index.Query("a").Select(e => e.Path).Should().Contain(@"D:\a", "the folder's own entry is not one of its children");
    }

    [Fact]
    public void SaveLoad_KeepsTheTruncatedFlag()
    {
        var path = Path.Combine(_dir, "trunc.json");
        var index = new NameIndex();
        index.Replace([new NameIndexEntry(@"D:\a.txt", "a.txt", false)], [@"D:\"], DateTime.UtcNow, truncated: true);
        index.Save(path).Should().BeTrue();

        var restored = new NameIndex();
        restored.Load(path).Should().BeTrue();

        restored.IsTruncated.Should().BeTrue();
    }

    // ── Roots ─────────────────────────────────────────────────────────────────

    [Fact]
    public void DefaultRoots_AreExistingDirectories()
    {
        foreach (var root in NameIndexService.DefaultRoots())
        {
            Directory.Exists(root).Should().BeTrue();
        }
    }
}
