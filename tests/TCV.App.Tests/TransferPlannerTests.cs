using System.IO;
using FluentAssertions;
using TCV.App.Operations.Conflict;
using TCV.App.Services;
using TCV.Core.Domain;
using TCV.Core.Interfaces;
using TCV.Network;

namespace TCV.App.Tests;

/// <summary>
/// Shift+F5 on a folder (6.96): every file under it with its relative path, each folder before what it
/// holds, nothing following a link round in circles, nothing left out without a word, and a taken name
/// decided by the one rule every copy goes by (TRAP-58).
/// </summary>
public sealed class TransferPlannerTests
{
    private const string Target = @"C:\target";

    private readonly InMemoryFileSystem _server = new("sftp");
    private readonly Dictionary<string, List<IFileEntry>> _disk = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(IFileEntry Source, IFileEntry Existing)> _asked = [];
    private readonly Dictionary<string, int> _targetListings = new(StringComparer.OrdinalIgnoreCase);

    private ConflictResolution _answer = ConflictResolution.Overwrite;
    private bool _applyToAll;

    private TransferPlanner Download() => new(
        isDownload: true, _server, Target,
        (folder, _) =>
        {
            _targetListings[folder] = _targetListings.GetValueOrDefault(folder) + 1;
            return Task.FromResult<IReadOnlyList<IFileEntry>>(_disk.TryGetValue(folder, out var entries) ? entries : []);
        },
        (source, existing) =>
        {
            _asked.Add((source, existing));
            return Task.FromResult((_answer, _applyToAll));
        });

    private async Task<IFileEntry> EntryAsync(string path) => (await _server.GetEntryAsync(new VfsPath(path)))!;

    private async Task<TransferPlan> PlanAsync(params string[] selected)
    {
        var entries = new List<IFileEntry>();
        foreach (var path in selected)
        {
            entries.Add(await EntryAsync(path));
        }

        return await Download().BuildAsync(entries, null, CancellationToken.None);
    }

    private void OnDisk(string folder, string name, bool isDirectory = false, DateTime? modified = null) =>
        (_disk.TryGetValue(folder, out var list) ? list : _disk[folder] = []).Add(new FileEntry(
            new VfsPath(Path.Combine(folder, name)), name, isDirectory, 1, modified ?? new DateTime(2026, 1, 1),
            isDirectory ? FileAttributes.Directory : FileAttributes.Normal));

    [Fact]
    public async Task BuildAsync_AFolder_BecomesItsFilesWithRelativePathsEachFolderFirst()
    {
        _server.AddFile("/www/index.html", [1, 2, 3]);
        _server.AddFile("/www/css/site.css", [1]);
        _server.AddFile("/www/css/fonts/a.woff", [1, 2]);

        var plan = await PlanAsync("/www");

        plan.Skips.Should().BeEmpty();
        plan.Stopped.Should().BeFalse();
        plan.FileCount.Should().Be(3);
        plan.Steps.Select(s => s.DestinationPath).Should().Contain(
            [@"C:\target\www", @"C:\target\www\css", @"C:\target\www\css\fonts",
             @"C:\target\www\index.html", @"C:\target\www\css\site.css", @"C:\target\www\css\fonts\a.woff"]);

        var site = plan.Steps.Single(s => s.DestinationPath.EndsWith("site.css", StringComparison.Ordinal));
        site.SourcePath.Should().Be("/www/css/site.css");
        site.Display.Should().Be("www/css/site.css");
        site.Size.Should().Be(1);
        site.IsFolder.Should().BeFalse();
        site.Overwrite.Should().BeFalse();

        foreach (var step in plan.Steps.Where(s => s.IsFolder))
        {
            var parent = Path.GetDirectoryName(step.DestinationPath)!;
            var index = plan.Steps.ToList().IndexOf(step);
            plan.Steps.Take(index).Where(s => s.DestinationPath.StartsWith(step.DestinationPath + @"\", StringComparison.Ordinal))
                .Should().BeEmpty("a file must find its folder there");
            parent.Should().NotBeEmpty();
        }
    }

    [Fact]
    public async Task BuildAsync_AnEmptyFolder_StillArrives()
    {
        _server.AddDirectory("/www");
        _server.AddDirectory("/www/empty");

        var plan = await PlanAsync("/www");

        plan.Steps.Should().Contain(s => s.IsFolder && s.DestinationPath == @"C:\target\www\empty");
    }

    [Fact]
    public async Task BuildAsync_PlainFiles_StayFiles()
    {
        _server.AddFile("/a.txt", [1, 2]);

        var plan = await PlanAsync("/a.txt");

        plan.Steps.Should().ContainSingle().Which.Should().Be(
            new TransferPlanStep("/a.txt", @"C:\target\a.txt", false, 2, false, "a.txt"));
    }

    // ── Links and cycles ──────────────────────────────────────────────────────

    /// <summary>A file system of entries written out by hand: a junction, a server's looping link.</summary>
    private sealed class ScriptedSource(Func<VfsPath, IReadOnlyList<IFileEntry>> list) : IVirtualFileSystem
    {
        public int Listings { get; private set; }

        public string Scheme => "sftp";

        public string DisplayName => "test";

        public event EventHandler<VfsChangedEventArgs>? Changed { add { } remove { } }

        public Task<IReadOnlyList<IFileEntry>> ListAsync(VfsPath path, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Listings++;
            return Task.FromResult(list(path));
        }

        public Task<IFileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite = false, CancellationToken ct = default) => throw new NotSupportedException();

        public Task CopyAsync(VfsPath source, VfsPath destination, IProgress<FileProgress>? progress, CancellationToken ct) => throw new NotSupportedException();

        public Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct) => throw new NotSupportedException();

        public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct) => throw new NotSupportedException();

        public Task CreateDirectoryAsync(VfsPath path, CancellationToken ct) => throw new NotSupportedException();

        public Task RenameAsync(VfsPath path, string newName, CancellationToken ct) => throw new NotSupportedException();

        public bool SupportsOperation(VfsOperation operation) => true;

        public void Dispose()
        {
        }
    }

    private static FileEntry Folder(string path, FileAttributes extra = 0) =>
        new(new VfsPath(path), new VfsPath(path).FileName, true, 0, new DateTime(2026, 1, 1), FileAttributes.Directory | extra);

    private static FileEntry File1(string path, long size = 1) =>
        new(new VfsPath(path), new VfsPath(path).FileName, false, size, new DateTime(2026, 1, 1), FileAttributes.Normal);

    private static TransferPlanner DownloadFrom(IVirtualFileSystem source) => new(
        isDownload: true, source, Target,
        (_, _) => Task.FromResult<IReadOnlyList<IFileEntry>>([]),
        (_, _) => throw new InvalidOperationException("no conflicts here"));

    [Fact]
    public async Task BuildAsync_AJunction_IsNotEnteredAndIsNamed()
    {
        var source = new ScriptedSource(path => path.Value switch
        {
            "/www" => [Folder("/www/link", FileAttributes.ReparsePoint), File1("/www/a.txt")],
            _ => throw new InvalidOperationException("must not be listed: " + path.Value),
        });

        var plan = await DownloadFrom(source).BuildAsync([Folder("/www")], null, default);

        plan.Skips.Should().ContainSingle().Which.Should().Be(new TransferSkip("/www/link", TransferSkipReason.Link));
        plan.FileCount.Should().Be(1);
    }

    [Fact]
    public async Task BuildAsync_AServersLinkPresentedAsAFolder_StopsAtTheCycleAndNamesIt()
    {
        // /www/loop on such a server lists exactly what /www lists, however deep it is followed.
        var source = new ScriptedSource(path => path.FileName == "www" || path.FileName == "loop"
            ? [File1(path.Value + "/a.txt"), Folder(path.Value + "/loop")]
            : throw new InvalidOperationException(path.Value));

        var plan = await DownloadFrom(source).BuildAsync([Folder("/www")], null, default);

        plan.FileCount.Should().Be(1);
        plan.Skips.Should().ContainSingle().Which.Reason.Should().Be(TransferSkipReason.Cycle);
        source.Listings.Should().Be(2);
    }

    [Fact]
    public async Task BuildAsync_ACycleThatChangesAsItGoes_StopsAtTheDepthLimitAndNamesIt()
    {
        // Every level looks different (a counter in the name), so only the depth can stop it.
        var level = 0;
        var source = new ScriptedSource(path => [File1(path.Value + "/f" + level++), Folder(path.Value + "/d")]);

        var plan = await DownloadFrom(source).BuildAsync([Folder("/www")], null, default);

        plan.Skips.Should().ContainSingle().Which.Reason.Should().Be(TransferSkipReason.TooDeep);
        source.Listings.Should().Be(TransferPlanner.MaxDepth);
    }

    [Fact]
    public async Task BuildAsync_TheSameFolderTwice_IsPlannedOnce()
    {
        _server.AddFile("/www/a.txt", [1]);

        var plan = await PlanAsync("/www", "/www");

        plan.FileCount.Should().Be(1);
    }

    [Fact]
    public async Task BuildAsync_ASubfolderCannotBeRead_IsNamedAndTheRestGoesOn()
    {
        _server.AddFile("/www/a.txt", [1]);
        _server.AddFile("/www/locked/b.txt", [1]);
        _server.AddFile("/www/z/c.txt", [1]);
        _server.FailListingOf = "/www/locked";

        var plan = await PlanAsync("/www");

        plan.Skips.Should().ContainSingle();
        plan.Skips[0].Path.Should().Be("/www/locked");
        plan.Skips[0].Reason.Should().Be(TransferSkipReason.Unreadable);
        plan.Skips[0].Detail.Should().NotBeEmpty();
        plan.Steps.Select(s => s.SourcePath).Should().Contain(["/www/a.txt", "/www/z/c.txt"]);
    }

    [Fact]
    public async Task BuildAsync_AChangedHostKey_StopsTheWalk()
    {
        _server.AddFile("/www/a/b.txt", [1]);
        _server.FailListingOf = "/www/a";
        _server.FailListingWith = () => new HostKeyChangedException("example.org", "SHA256:old", "SHA256:new");

        var plan = () => PlanAsync("/www");

        await plan.Should().ThrowAsync<HostKeyChangedException>();
    }

    [Fact]
    public async Task BuildAsync_Cancelled_Throws()
    {
        _server.AddFile("/www/a.txt", [1]);
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();

        var plan = async () => await Download().BuildAsync([await EntryAsync("/www")], null, stop.Token);

        await plan.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a:b.txt")]
    [InlineData("CON")]
    public async Task BuildAsync_ANameThatCannotBeOnTheDisk_IsNamedAndNotPlanned(string name)
    {
        var source = new ScriptedSource(_ => [new FileEntry(new VfsPath("/www/x"), name, false, 1, DateTime.Now, FileAttributes.Normal), File1("/www/ok.txt")]);

        var plan = await DownloadFrom(source).BuildAsync([Folder("/www")], null, default);

        plan.Skips.Should().ContainSingle().Which.Reason.Should().Be(TransferSkipReason.BadName);
        plan.FileCount.Should().Be(1);
        plan.Steps.Should().NotContain(s => s.DestinationPath.Contains(name, StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildAsync_AServersNameWithASeparator_CannotLandOutsideTheTarget()
    {
        var source = new ScriptedSource(_ => [new FileEntry(new VfsPath("/www/x"), @"..\..\evil.exe", false, 1, DateTime.Now, FileAttributes.Normal)]);

        var plan = await DownloadFrom(source).BuildAsync([Folder("/www")], null, default);

        plan.FileCount.Should().Be(0);
        plan.Skips.Should().ContainSingle().Which.Reason.Should().Be(TransferSkipReason.BadName);
    }

    // ── Names that are taken ──────────────────────────────────────────────────

    [Fact]
    public async Task BuildAsync_ANameIsTaken_AsksAndOverwritesOnlyWhenTold()
    {
        _server.AddFile("/a.txt", [1]);
        OnDisk(Target, "a.txt");

        var plan = await PlanAsync("/a.txt");

        _asked.Should().ContainSingle();
        plan.Steps.Should().ContainSingle().Which.Overwrite.Should().BeTrue();
    }

    [Fact]
    public async Task BuildAsync_AnswerSkip_LeavesItOut()
    {
        _server.AddFile("/a.txt", [1]);
        _server.AddFile("/b.txt", [1]);
        OnDisk(Target, "a.txt");
        _answer = ConflictResolution.Skip;

        var plan = await PlanAsync("/a.txt", "/b.txt");

        plan.Steps.Select(s => s.SourcePath).Should().Equal("/b.txt");
        plan.Skips.Should().BeEmpty("the user chose that: nothing to report back");
    }

    [Fact]
    public async Task BuildAsync_AnswerCancel_QueuesNothing()
    {
        _server.AddFile("/www/a.txt", [1]);
        _server.AddFile("/www/b.txt", [1]);
        OnDisk(Target + @"\www", "b.txt");
        OnDisk(Target, "www", isDirectory: true);
        _answer = ConflictResolution.Cancel;

        var plan = await PlanAsync("/www");

        plan.Stopped.Should().BeTrue();
        plan.Steps.Should().BeEmpty();
    }

    [Fact]
    public async Task BuildAsync_ApplyToAll_AsksOnce()
    {
        _server.AddFile("/www/a.txt", [1]);
        _server.AddFile("/www/b.txt", [1]);
        OnDisk(Target, "www", isDirectory: true);
        OnDisk(Target + @"\www", "a.txt");
        OnDisk(Target + @"\www", "b.txt");
        _applyToAll = true;

        var plan = await PlanAsync("/www");

        _asked.Should().ContainSingle();
        plan.Steps.Where(s => !s.IsFolder).Should().OnlyContain(s => s.Overwrite);
    }

    [Theory]
    [InlineData(2027, true)]
    [InlineData(2020, false)]
    public async Task BuildAsync_OverwriteIfNewer_IsSettledByTheOneRule(int existingYear, bool sourceIsOlder)
    {
        _server.AddFile("/a.txt", [1], new DateTime(2026, 1, 1));
        OnDisk(Target, "a.txt", modified: new DateTime(existingYear, 1, 1));
        _answer = ConflictResolution.OverwriteIfNewer;

        var plan = await PlanAsync("/a.txt");

        if (sourceIsOlder)
        {
            plan.Steps.Should().BeEmpty("the file there is newer");
        }
        else
        {
            plan.Steps.Should().ContainSingle().Which.Overwrite.Should().BeTrue();
        }
    }

    [Fact]
    public async Task BuildAsync_AnswerRename_TakesAFreeName()
    {
        _server.AddFile("/a.txt", [1]);
        OnDisk(Target, "a.txt");
        OnDisk(Target, "a (1).txt");
        _answer = ConflictResolution.RenameAuto;

        var plan = await PlanAsync("/a.txt");

        var step = plan.Steps.Should().ContainSingle().Subject;
        step.DestinationPath.Should().Be(@"C:\target\a (2).txt");
        step.Overwrite.Should().BeFalse();
    }

    [Fact]
    public async Task BuildAsync_TwoSelectedFilesOfOneName_MeetInThePlanAndAreAskedAbout()
    {
        // Search hits from different folders, both going to one target.
        _server.AddFile("/one/a.txt", [1]);
        _server.AddFile("/two/a.txt", [2]);
        _answer = ConflictResolution.RenameAuto;

        var plan = await PlanAsync("/one/a.txt", "/two/a.txt");

        _asked.Should().ContainSingle();
        plan.Steps.Select(s => s.DestinationPath).Should().Equal(@"C:\target\a.txt", @"C:\target\a (1).txt");
    }

    [Fact]
    public async Task BuildAsync_AFolderThatExists_IsMergedIntoWithoutAskingOrMakingIt()
    {
        _server.AddFile("/www/a.txt", [1]);
        OnDisk(Target, "www", isDirectory: true);

        var plan = await PlanAsync("/www");

        _asked.Should().BeEmpty();
        plan.Steps.Should().ContainSingle().Which.SourcePath.Should().Be("/www/a.txt");
    }

    [Fact]
    public async Task BuildAsync_AFileWhereTheFolderWouldGo_IsNamedAndNotOverwritten()
    {
        _server.AddFile("/www/a.txt", [1]);
        OnDisk(Target, "www");

        var plan = await PlanAsync("/www");

        plan.Steps.Should().BeEmpty();
        plan.Skips.Should().ContainSingle().Which.Reason.Should().Be(TransferSkipReason.KindClash);
    }

    [Fact]
    public async Task BuildAsync_AFolderWhereTheFileWouldGo_IsNamedAndNotOverwritten()
    {
        _server.AddFile("/a.txt", [1]);
        OnDisk(Target, "a.txt", isDirectory: true);

        var plan = await PlanAsync("/a.txt");

        plan.Steps.Should().BeEmpty();
        plan.Skips.Should().ContainSingle().Which.Reason.Should().Be(TransferSkipReason.KindClash);
    }

    [Fact]
    public async Task BuildAsync_ATargetFolder_IsListedOnceAndOneJustPlannedNotAtAll()
    {
        _server.AddFile("/www/a.txt", [1]);
        _server.AddFile("/www/b.txt", [1]);
        _server.AddFile("/www/sub/c.txt", [1]);
        _server.AddFile("/d.txt", [1]);

        await PlanAsync("/www", "/d.txt");

        _targetListings.Should().ContainKey(Target).WhoseValue.Should().Be(1);
        _targetListings.Should().NotContainKey(Target + @"\www");
        _targetListings.Should().NotContainKey(Target + @"\www\sub");
    }

    // ── An upload ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildAsync_AnUpload_SpeaksWindowsOnTheSourceAndTheServersPathsOnTheTarget()
    {
        var source = new ScriptedSource(path => path.Value == "C:/src/www"
            ? [File1("C:/src/www/a.txt"), Folder("C:/src/www/sub")]
            : path.Value == "C:/src/www/sub" ? [File1("C:/src/www/sub/b.txt")] : []);
        var planner = new TransferPlanner(
            isDownload: false, source, "/var",
            (_, _) => Task.FromResult<IReadOnlyList<IFileEntry>>([]),
            (_, _) => throw new InvalidOperationException());

        var plan = await planner.BuildAsync([Folder("C:/src/www")], null, default);

        plan.Steps.Select(s => (s.SourcePath, s.DestinationPath)).Should().BeEquivalentTo(new[]
        {
            (@"C:\src\www", "/var/www"),
            (@"C:\src\www\a.txt", "/var/www/a.txt"),
            (@"C:\src\www\sub", "/var/www/sub"),
            (@"C:\src\www\sub\b.txt", "/var/www/sub/b.txt"),
        });
    }

    [Fact]
    public async Task BuildAsync_AFolderOf100000Files_DoesNotGoQuadratic()
    {
        var files = Enumerable.Range(0, 100_000).Select(i => (IFileEntry)File1($"/www/f{i:D6}.bin", 10)).ToList();
        var source = new ScriptedSource(path => path.Value == "/www" ? files : []);
        var reports = 0;
        var progress = new CountingProgress(() => reports++);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var plan = await DownloadFrom(source).BuildAsync([Folder("/www")], progress, default);
        clock.Stop();

        plan.FileCount.Should().Be(100_000);
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
        reports.Should().BeLessThan(10_000, "the window does not want a message per file");
        reports.Should().BeGreaterThan(0);
    }

    private sealed class CountingProgress(Action report) : IProgress<TransferPlanProgress>
    {
        public void Report(TransferPlanProgress value) => report();
    }
}
