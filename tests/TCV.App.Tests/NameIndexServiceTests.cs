using System.Diagnostics;
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.Navigation;
using TCV.App.Operations;
using TCV.App.Services;

namespace TCV.App.Tests;

/// <summary>The whole-disk name index: lazy cache load, fresh-ness, incremental refresh, walk rules.</summary>
public sealed class NameIndexServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "tcv-nis-" + Guid.NewGuid().ToString("N"));

    private string Cache => Path.Combine(_dir, "name-index.json");
    private string Tree => Path.Combine(_dir, "tree");

    public NameIndexServiceTests()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(Tree);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private NameIndexService Service(OperationLog? log = null, Func<IReadOnlyList<string>>? roots = null) =>
        new(NullLogger<NameIndexService>.Instance, Cache, log, roots ?? (() => [Tree]));

    private void Touch(string relative)
    {
        var path = Path.Combine(Tree, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
    }

    private void SeedCache(DateTime builtUtc, params string[] files)
    {
        var index = new NameIndex();
        index.Replace(
            files.Select(f => new NameIndexEntry(f, Path.GetFileName(f), false)).ToList(),
            [Tree], builtUtc, truncated: true);
        index.Save(Cache).Should().BeTrue();
    }

    // ── Lazy cache load (the cache used to be written but never read) ─────────

    [Fact]
    public void Constructor_DoesNotTouchTheCache()
    {
        SeedCache(DateTime.UtcNow, Path.Combine(Tree, "cached.txt"));

        using var service = Service();

        service.Index.Count.Should().Be(0, "nothing may be read before the search is first opened (cold start)");
    }

    [Fact]
    public async Task EnsureReadyAsync_FreshCache_IsLoadedAndNotRebuilt()
    {
        var cached = Path.Combine(Tree, "cached-only.txt");
        SeedCache(DateTime.UtcNow, cached);
        using var service = Service();

        await service.EnsureReadyAsync();

        service.Index.Query("cached-only").Should().ContainSingle();
        service.Index.IsTruncated.Should().BeTrue("the truncation flag is persisted with the cache");
        service.IsBuilding.Should().BeFalse();
    }

    [Fact]
    public async Task EnsureReadyAsync_StaleCache_IsLoadedThenRebuiltFromTheRoots()
    {
        Touch("fresh.txt");
        SeedCache(DateTime.UtcNow - NameIndexService.StaleAfter - TimeSpan.FromHours(1), Path.Combine(Tree, "old.txt"));
        using var service = Service();

        await service.EnsureReadyAsync();

        service.Index.Query("fresh").Should().ContainSingle();
        service.Index.Query("old.txt").Should().BeEmpty("the rebuild replaces the stale content");
    }

    [Fact]
    public async Task EnsureReadyAsync_NoCache_BuildsTheIndex()
    {
        Touch("a/b.txt");
        using var service = Service();

        await service.EnsureReadyAsync();

        service.Index.Query("b.txt").Should().ContainSingle();
        File.Exists(Cache).Should().BeTrue("the rebuilt index is persisted for the next session");
    }

    [Fact]
    public async Task EnsureReadyAsync_CalledTwice_ReadsTheCacheOnce()
    {
        SeedCache(DateTime.UtcNow, Path.Combine(Tree, "cached.txt"));
        using var service = Service();
        var changes = 0;
        service.Changed += (_, _) => Interlocked.Increment(ref changes);

        await service.EnsureReadyAsync();
        var afterFirst = changes;
        await service.EnsureReadyAsync();

        changes.Should().Be(afterFirst);
    }

    // ── Rebuild state ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RebuildAsync_ReportsTruncationAtTheCap()
    {
        for (var i = 0; i < 30; i++)
        {
            Touch($"f{i}.txt");
        }

        using var service = Service();
        var capped = NameIndexWalker.Walk([Tree], new NameIndexWalkOptions(MaxEntries: 10), CancellationToken.None);

        capped.Truncated.Should().BeTrue();
        capped.Entries.Should().HaveCount(10);

        await service.RebuildAsync([Tree], defaultRoots: false);
        service.Index.IsTruncated.Should().BeFalse();
    }

    [Fact]
    public async Task RebuildAsync_SupersededBuild_DoesNotClearTheBuildingFlagOfTheNewerOne()
    {
        Touch("x.txt");
        using var service = Service();

        // First build is cancelled by the second; the flag must stay up until the second one ends.
        var first = service.RebuildAsync([Tree], defaultRoots: false);
        var second = service.RebuildAsync([Tree], defaultRoots: false);
        await Task.WhenAll(first, second);

        service.IsBuilding.Should().BeFalse("the newest build finished");
        service.Index.Query("x.txt").Should().ContainSingle();
    }

    [Fact]
    public async Task CancelRebuild_KeepsThePreviousIndex()
    {
        Touch("keep.txt");
        using var service = Service();
        await service.RebuildAsync([Tree], defaultRoots: false);

        service.CancelRebuild();

        service.IsBuilding.Should().BeFalse();
        service.Index.Query("keep").Should().ContainSingle();
    }

    // ── Incremental refresh ───────────────────────────────────────────────────

    [Fact]
    public async Task RescanDirtyAsync_PicksUpAFileCreatedAfterTheBuild()
    {
        Touch("sub/old.txt");
        using var service = Service();
        await service.RebuildAsync([Tree], defaultRoots: false);

        Touch("sub/new-from-agent.txt");
        service.NoteChanged(Path.Combine(Tree, "sub"));
        await service.RescanDirtyAsync();

        service.Index.Query("new-from-agent").Should().ContainSingle();
        service.Index.Query("old.txt").Should().ContainSingle();
    }

    [Fact]
    public async Task RescanDirtyAsync_DeletedFolder_DropsItsEntries()
    {
        Touch("gone/inner.txt");
        using var service = Service();
        await service.RebuildAsync([Tree], defaultRoots: false);

        Directory.Delete(Path.Combine(Tree, "gone"), recursive: true);
        service.NoteChanged(Path.Combine(Tree, "gone"));
        await service.RescanDirtyAsync();

        service.Index.Query("inner").Should().BeEmpty();
        service.Index.Query("gone").Should().BeEmpty();
    }

    [Fact]
    public async Task RescanDirtyAsync_FolderLargerThanTheCap_KeepsDeepEntriesAndAddsNewChild()
    {
        for (var i = 0; i < 5; i++)
        {
            Touch($"big/deep{i}/nested/file{i}.txt");
        }
        var service = new NameIndexService(NullLogger<NameIndexService>.Instance, Cache, null, () => [Tree])
        {
            RescanMaxEntries = 3,
        };
        using var _ = service;
        await service.RebuildAsync([Tree], defaultRoots: false);

        Touch("big/copied-here.txt");
        service.NoteChanged(Path.Combine(Tree, "big"));
        await service.RescanDirtyAsync();

        service.Index.Query("copied-here").Should().ContainSingle();
        service.Index.Query("file").Should().HaveCount(5, "a capped rescan must not drop what lies below its cap");
    }

    [Fact]
    public void ListSubtree_InsideARepository_HonoursTheGitignoreAboveTheFolder()
    {
        Directory.CreateDirectory(Path.Combine(Tree, ".git"));
        File.WriteAllText(Path.Combine(Tree, ".gitignore"), "bin/\n");
        Touch("src/app.cs");
        Touch("src/bin/app.dll");

        var result = NameIndexWalker.ListSubtree(Path.Combine(Tree, "src"), new NameIndexWalkOptions(), CancellationToken.None);

        result.Entries.Select(e => e.Name).Should().Contain("app.cs").And.NotContain("app.dll");
    }

    [Fact]
    public async Task OperationLog_CompletedCopy_RefreshesTheDestinationFolder()
    {
        Touch("dest/existing.txt");
        var log = new OperationLog(Path.Combine(_dir, "log.jsonl"));
        using var service = Service(log);
        await service.RebuildAsync([Tree], defaultRoots: false);

        Touch("dest/copied.txt");
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += (_, _) => { if (service.Index.Query("copied").Count > 0) { changed.TrySetResult(); } };
        log.Append(new OperationLogEntry
        {
            Timestamp = DateTime.UtcNow,
            Kind = OperationKind.Copy,
            Source = "D:/elsewhere/copied.txt",
            Destination = Path.Combine(Tree, "dest"),
            ItemCount = 1,
            Outcome = OperationOutcome.Completed,
            ArrivedIn = Path.Combine(Tree, "dest"),
        });

        var winner = await Task.WhenAny(changed.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        winner.Should().BeSameAs(changed.Task, "a completed operation must reach the index without a manual rebuild");
    }

    [Fact]
    public async Task OperationLog_CopyUnderANewName_IndexesTheArrivedFile()
    {
        // F5 to «dest\renamed.txt» logs the file itself as the destination; rescanning that as a folder
        // left the copy out of the name search until the next rebuild (6.63).
        Touch("dest/existing.txt");
        var log = new OperationLog(Path.Combine(_dir, "log.jsonl"));
        using var service = Service(log);
        await service.RebuildAsync([Tree], defaultRoots: false);

        Touch("dest/renamed.txt");
        log.Append(new OperationLogEntry
        {
            Timestamp = DateTime.UtcNow,
            Kind = OperationKind.Copy,
            Source = "D:/elsewhere/original.txt",
            Destination = Path.Combine(Tree, "dest", "renamed.txt"),
            ItemCount = 1,
            Outcome = OperationOutcome.Completed,
            ArrivedIn = Path.Combine(Tree, "dest"),
        });
        await service.RescanDirtyAsync();

        service.Index.Query("renamed").Should().ContainSingle();
        service.Index.Query("existing").Should().ContainSingle();
    }

    [Fact]
    public async Task OperationLog_ArchiveEntriesDeleted_KeepsTheArchiveInTheIndex()
    {
        // Removing entries from an archive logs the archive as the destination and «archive (N)» as the
        // source; neither is a folder, and rescanning one as a folder dropped the archive.
        Touch("pack/bundle.zip");
        var log = new OperationLog(Path.Combine(_dir, "log.jsonl"));
        using var service = Service(log);
        await service.RebuildAsync([Tree], defaultRoots: false);

        var archive = Path.Combine(Tree, "pack", "bundle.zip");
        log.Append(new OperationLogEntry
        {
            Timestamp = DateTime.UtcNow,
            Kind = OperationKind.Delete,
            Source = archive + " (2)",
            Destination = archive,
            ItemCount = 2,
            Outcome = OperationOutcome.Completed,
        });
        await service.RescanDirtyAsync();

        service.Index.Query("bundle").Should().ContainSingle();
    }

    [Theory]
    [InlineData("D:/repo/src (12)", "D:/repo/src")]
    [InlineData("D:/repo/src/a.txt", "D:/repo/src")]
    [InlineData("a.txt", null)]
    [InlineData("", null)]
    public void ParentOfLoggedSource_HandlesSingleItemsAndBatches(string source, string? expected)
    {
        NameIndexService.ParentOfLoggedSource(source).Should().Be(expected);
    }

    // ── Walk rules ────────────────────────────────────────────────────────────

    private List<string> Walk(NameIndexWalkOptions options) =>
        NameIndexWalker.Walk([Tree], options, CancellationToken.None).Entries
            .Select(e => Path.GetRelativePath(Tree, e.Path).Replace((char)92, '/'))
            .ToList();

    [Fact]
    public void Walk_DefaultRoots_SkipVendoredFolders()
    {
        Touch("app/main.cs");
        Touch("app/node_modules/dep/index.js");

        Walk(new NameIndexWalkOptions(SkipVendored: true)).Should()
            .Contain("app/main.cs").And.NotContain(p => p.Contains("node_modules/"));
    }

    [Fact]
    public void Walk_ExplicitRoot_IndexesInsideVendoredAndBuildFolders()
    {
        Touch("app/node_modules/dep/index.js");
        Touch("app/bin/tool.dll");
        Touch("app/obj/x.cache");

        Walk(new NameIndexWalkOptions(SkipVendored: false, RespectGitignore: false)).Should()
            .Contain(["app/node_modules/dep/index.js", "app/bin/tool.dll", "app/obj/x.cache"],
                "the user asked for this folder; a directory that happens to be called bin is not noise");
    }

    [Fact]
    public void Walk_GitignoredFolder_IsSkippedButIgnoredFilesElsewhereStay()
    {
        Touch(".gitignore");
        File.WriteAllText(Path.Combine(Tree, ".gitignore"), "build/\n.env\n");
        Touch("build/out.dll");
        Touch(".env");
        Touch("src/a.cs");

        var found = Walk(new NameIndexWalkOptions());

        found.Should().Contain("src/a.cs").And.Contain(".env");
        found.Should().NotContain("build/out.dll");
    }

    [Fact]
    public void Walk_FolderCalledWindowsBelowAProjectRoot_IsIndexed()
    {
        Touch("proj/Windows/app.xaml");

        Walk(new NameIndexWalkOptions()).Should().Contain("proj/Windows/app.xaml");
    }

    [Fact]
    public void Walk_GitFolder_IsAlwaysSkipped()
    {
        Touch(".git/HEAD");
        Touch("a.txt");

        Walk(new NameIndexWalkOptions(SkipVendored: false, RespectGitignore: false)).Should()
            .Contain("a.txt").And.NotContain(p => p.StartsWith(".git/", StringComparison.Ordinal));
    }

    [Fact]
    public void Walk_Junction_IsNotFollowed()
    {
        Touch("real/inner.txt");
        var link = Path.Combine(Tree, "loop");
        var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{Tree}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        mk.WaitForExit();
        if (!Directory.Exists(link))
        {
            return;   // junction creation is not available here
        }

        var found = Walk(new NameIndexWalkOptions(SkipVendored: false, RespectGitignore: false));

        found.Should().Contain("real/inner.txt");
        found.Should().NotContain(p => p.StartsWith("loop/", StringComparison.Ordinal));
    }

    [Fact]
    public void DefaultRoots_OnlyReadyFixedDrives()
    {
        var roots = NameIndexService.DefaultRoots();

        roots.Should().OnlyContain(r => Directory.Exists(r));
        roots.Select(r => new DriveInfo(r).DriveType).Should().OnlyContain(t => t == DriveType.Fixed);
    }
}
