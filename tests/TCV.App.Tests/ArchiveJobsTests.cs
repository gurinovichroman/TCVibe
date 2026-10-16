using System.IO;
using System.IO.Compression;
using FluentAssertions;
using TCV.App.Services;
using TCV.Archives;
using TCV.Core.PluginSdk;

namespace TCV.App.Tests;

public sealed class ArchiveJobsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tcv-jobs-" + Guid.NewGuid().ToString("N"));
    private readonly ArchivesPlugin _plugin = new();

    public ArchiveJobsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string MakeTree()
    {
        var root = Path.Combine(_dir, "tree");
        Directory.CreateDirectory(Path.Combine(root, "empty"));
        Directory.CreateDirectory(Path.Combine(root, "full"));
        File.WriteAllText(Path.Combine(root, "full", "a.txt"), "AAA");
        File.WriteAllText(Path.Combine(root, "top.txt"), "TOP");
        return root;
    }

    private static PackDialogResult Request(string archive, ArchiveFormat format, string? password = null,
        bool addToExisting = false, ArchiveCompressionLevel level = ArchiveCompressionLevel.Normal) =>
        new(archive, format, level, password, addToExisting);

    // ── PackEntryCollector ───────────────────────────────────────────────────

    [Fact]
    public void Collect_FolderWithEmptySubfolder_YieldsFilesAndADirectoryEntry()
    {
        var root = MakeTree();

        var entries = PackEntryCollector.Collect([root]).Select(e => e.entryName).ToList();

        entries.Should().BeEquivalentTo("tree/top.txt", "tree/full/a.txt", "tree/empty/");
    }

    [Fact]
    public void CollectFolder_EmptySubfolderAndTheArchiveItself_KeepsTheFolderAndLeavesOutTheArchive()
    {
        var root = MakeTree();
        var archive = Path.Combine(root, "self.zip");
        File.WriteAllText(archive, "not really a zip");

        var entries = ArchiveAddCollector.CollectFolder(root, "tree", archive, null).Select(e => e.entryName).ToList();

        entries.Should().BeEquivalentTo("tree/top.txt", "tree/full/a.txt", "tree/empty/");
    }

    [Fact]
    public void CollectFolder_Junction_IsReportedAndNotEntered()
    {
        var root = MakeTree();
        var link = Path.Combine(root, "loop");
        var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "cmd.exe", $"/c mklink /J \"{link}\" \"{root}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        mklink.WaitForExit();
        if (!Directory.Exists(link))
        {
            return;   // junctions unavailable on this file system
        }

        var skipped = new List<string>();
        var entries = ArchiveAddCollector.CollectFolder(root, "tree", null, skipped.Add).Select(e => e.entryName).ToList();
        Directory.Delete(link);   // the junction only, never its target

        skipped.Should().Equal("tree/loop");
        entries.Should().NotContain(e => e.StartsWith("tree/loop"));
    }

    [Fact]
    public void CollectSources_FolderHoldingTheArchive_IsNotWholeAndTheArchiveIsLeftOut()
    {
        var root = MakeTree();
        var archive = Path.Combine(root, "self.zip");
        File.WriteAllText(archive, "not really a zip");
        var other = Path.Combine(_dir, "other.txt");
        File.WriteAllText(other, "O");
        var skipped = new List<ArchiveAddSkip>();

        var (files, notWhole) = ArchiveAddCollector.CollectSources(
            [root, other], null, archive, "sub", (reason, _) => skipped.Add(reason), CancellationToken.None);

        notWhole.Should().Equal(0);
        skipped.Should().Equal(ArchiveAddSkip.ArchiveItself);
        files.Select(f => f.EntryName).Should().BeEquivalentTo(
            "sub/tree/top.txt", "sub/tree/full/a.txt", "sub/tree/empty/", "sub/other.txt");
        files.Single(f => f.EntryName == "sub/other.txt").Owner.Should().Be(1);
    }

    [Fact]
    public void CollectSources_FolderWithJunction_IsNotWhole()
    {
        var root = MakeTree();
        var link = Path.Combine(root, "loop");
        var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "cmd.exe", $"/c mklink /J \"{link}\" \"{root}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        mklink.WaitForExit();
        if (!Directory.Exists(link))
        {
            return;   // junctions unavailable on this file system
        }

        var (_, notWhole) = ArchiveAddCollector.CollectSources(
            [root], null, Path.Combine(_dir, "x.zip"), "", (_, _) => { }, CancellationToken.None);
        Directory.Delete(link);

        notWhole.Should().Equal(0);
    }

    [Fact]
    public void CollectSources_SingleSourceWithNewName_TakesTheNewName()
    {
        var file = Path.Combine(_dir, "a.txt");
        File.WriteAllText(file, "A");

        var (files, notWhole) = ArchiveAddCollector.CollectSources(
            [file], "b.txt", Path.Combine(_dir, "x.zip"), "docs", (_, _) => { }, CancellationToken.None);

        notWhole.Should().BeEmpty();
        files.Should().ContainSingle().Which.EntryName.Should().Be("docs/b.txt");
    }

    [Fact]
    public void CollectFolder_HiddenFile_IsIncluded()
    {
        var root = MakeTree();
        var hidden = Path.Combine(root, ".secret");
        File.WriteAllText(hidden, "H");
        File.SetAttributes(hidden, FileAttributes.Hidden);

        ArchiveAddCollector.CollectFolder(root, "tree", null, null)
            .Select(e => e.entryName).Should().Contain("tree/.secret");
    }

    [Fact]
    public void Collect_EmptyFolder_YieldsItsOwnDirectoryEntry()
    {
        var empty = Path.Combine(_dir, "nothing");
        Directory.CreateDirectory(empty);

        PackEntryCollector.Collect([empty]).Select(e => e.entryName).Should().Equal("nothing/");
    }

    [Fact]
    public void Collect_ExcludedFile_IsLeftOut()
    {
        var root = MakeTree();
        var skip = Path.Combine(root, "top.txt");

        var entries = PackEntryCollector.Collect([root], [skip]).Select(e => e.entryName).ToList();

        entries.Should().NotContain("tree/top.txt");
    }

    // ── PackJob ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_Zip_KeepsEmptyFoldersAndLeavesNoScratchFile()
    {
        var root = MakeTree();
        var archive = Path.Combine(_dir, "out.zip");

        await PackJob.RunAsync(Request(archive, ArchiveFormat.Zip), [root], _plugin, false, null, CancellationToken.None);

        new TCV.Archives.Engines.ZipEngine().ListAll(archive).Should().Contain(e => e.FullPath == "tree/empty" && e.IsDirectory);
        Directory.GetFiles(_dir, ".tcv-*").Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_Zip_HonoursTheCompressionLevel()
    {
        var big = Path.Combine(_dir, "big.txt");
        File.WriteAllText(big, new string('x', 300_000));
        var stored = Path.Combine(_dir, "s.zip");
        var packed = Path.Combine(_dir, "p.zip");

        await PackJob.RunAsync(Request(stored, ArchiveFormat.Zip, level: ArchiveCompressionLevel.Store), [big], _plugin, false, null, CancellationToken.None);
        await PackJob.RunAsync(Request(packed, ArchiveFormat.Zip, level: ArchiveCompressionLevel.Ultra), [big], _plugin, false, null, CancellationToken.None);

        new FileInfo(stored).Length.Should().BeGreaterThan(new FileInfo(packed).Length * 5);
    }

    [Fact]
    public async Task RunAsync_ReplacingAnArchive_IsAllOrNothing()
    {
        var root = MakeTree();
        var archive = Path.Combine(_dir, "keep.tar");
        File.WriteAllText(archive, "precious");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => PackJob.RunAsync(Request(archive, ArchiveFormat.Tar), [root], _plugin, false, null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        File.ReadAllText(archive).Should().Be("precious");
        Directory.GetFiles(_dir, ".tcv-*").Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_TarOverAnExistingArchive_ReplacesItWholeNotAppends()
    {
        var root = MakeTree();
        var archive = Path.Combine(_dir, "again.tar");

        await PackJob.RunAsync(Request(archive, ArchiveFormat.Tar), [root], _plugin, false, null, CancellationToken.None);
        var first = new FileInfo(archive).Length;
        await PackJob.RunAsync(Request(archive, ArchiveFormat.Tar), [root], _plugin, false, null, CancellationToken.None);

        new FileInfo(archive).Length.Should().Be(first);
    }

    [Fact]
    public async Task RunAsync_AddToExistingZip_EditsInPlace()
    {
        var root = MakeTree();
        var archive = Path.Combine(_dir, "grow.zip");
        await PackJob.RunAsync(Request(archive, ArchiveFormat.Zip), [Path.Combine(root, "top.txt")], _plugin, false, null, CancellationToken.None);

        await PackJob.RunAsync(Request(archive, ArchiveFormat.Zip, addToExisting: true), [Path.Combine(root, "full")], _plugin, false, null, CancellationToken.None);

        new TCV.Archives.Engines.ZipEngine().ListAll(archive).Select(e => e.FullPath)
            .Should().Contain(["top.txt", "full/a.txt"]);
    }

    [Fact]
    public async Task RunAsync_SevenZipRequestedButMissing_ExplainsWhy()
    {
        var root = MakeTree();

        var act = () => PackJob.RunAsync(
            Request(Path.Combine(_dir, "x.7z"), ArchiveFormat.SevenZip), [root], _plugin, sevenZipAvailable: false,
            null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*7-Zip*");
    }

    [Fact]
    public async Task RunAsync_PasswordZip_IsEncryptedAndReadableWithThePassword()
    {
        if (WindowsArchiveService.GetSevenZipExePath() is null)
        {
            return; // needs 7-Zip on the machine
        }

        var root = MakeTree();
        var archive = Path.Combine(_dir, "secret.zip");

        await PackJob.RunAsync(Request(archive, ArchiveFormat.Zip, password: "pw"), [Path.Combine(root, "top.txt")],
            _plugin, true, null, CancellationToken.None);

        var withoutPassword = () => new TCV.Archives.Engines.ZipEngine().ListAll(archive);
        withoutPassword.Should().Throw<ArchivePasswordRequiredException>();
        using var stream = new TCV.Archives.Engines.ZipEngine("pw").ExtractEntry(archive, "top.txt");
        new StreamReader(stream).ReadToEnd().Should().Be("TOP");
    }

    [Fact]
    public async Task RunAsync_SevenZip_WithBadSourceReportsSevenZipsOwnMessage()
    {
        if (WindowsArchiveService.GetSevenZipExePath() is null)
        {
            return;
        }

        var act = () => PackJob.RunAsync(
            Request(Path.Combine(_dir, "bad.7z"), ArchiveFormat.SevenZip),
            [Path.Combine(_dir, "does-not-exist.txt")], _plugin, true, null, CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<SevenZipException>();
        thrown.Which.Output.Should().NotBeNullOrWhiteSpace();
        Directory.GetFiles(_dir, ".tcv-*").Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_SevenZip_JunctionIsReportedAndNotEntered()
    {
        if (WindowsArchiveService.GetSevenZipExePath() is null)
        {
            return;
        }

        var root = MakeTree();
        var link = Path.Combine(root, "full", "loop");
        var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "cmd.exe", $"/c mklink /J \"{link}\" \"{root}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        mklink.WaitForExit();
        if (!Directory.Exists(link))
        {
            return;   // junctions unavailable on this file system
        }

        var archive = Path.Combine(_dir, "links.7z");
        var skipped = new List<string>();
        try
        {
            await PackJob.RunAsync(Request(archive, ArchiveFormat.SevenZip), [root], _plugin, true, null,
                CancellationToken.None, skipped.Add);
        }
        finally
        {
            Directory.Delete(link);   // the junction only, never its target
        }

        skipped.Should().Equal("tree/full/loop");
        var names = _plugin.CreateEngine(archive).ListAll(archive).Select(e => e.FullPath.Replace('\\', '/')).ToList();
        names.Should().Contain(n => n.EndsWith("full/a.txt")).And.NotContain(n => n.Contains("loop"));
    }

    [Fact]
    public void FindLinks_SourceThatIsALink_IsDroppedAndReported()
    {
        var root = MakeTree();
        var link = Path.Combine(_dir, "alias");
        var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "cmd.exe", $"/c mklink /J \"{link}\" \"{root}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        mklink.WaitForExit();
        if (!Directory.Exists(link))
        {
            return;
        }

        var skipped = new List<string>();
        var (sources, links) = PackEntryCollector.FindLinks([link, root], skipped.Add);
        Directory.Delete(link);

        sources.Should().Equal(root);
        links.Should().BeEmpty();
        skipped.Should().Equal("alias");
    }

    [Fact]
    public async Task RunAsync_SevenZip_OnlySourceIsALink_PacksNothingAndReportsIt()
    {
        // 7z.exe given no names packs "*" of its working directory: it must not be run at all.
        var root = MakeTree();
        var link = Path.Combine(_dir, "only-link");
        var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "cmd.exe", $"/c mklink /J \"{link}\" \"{root}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        mklink.WaitForExit();
        if (!Directory.Exists(link))
        {
            return;
        }

        var archive = Path.Combine(_dir, "nothing.7z");
        var skipped = new List<string>();
        try
        {
            await PackJob.RunAsync(Request(archive, ArchiveFormat.SevenZip), [link], _plugin, true, null,
                CancellationToken.None, skipped.Add);
        }
        finally
        {
            Directory.Delete(link);
        }

        skipped.Should().Equal("only-link");
        File.Exists(archive).Should().BeFalse();
        Directory.GetFiles(_dir, ".tcv-*").Should().BeEmpty();
    }

    [Fact]
    public async Task PackWith7ZipAsync_NoSources_IsRefusedBefore7ZipRuns()
    {
        var act = () => WindowsArchiveService.PackWith7ZipAsync(
            Path.Combine(_dir, "x.7z"), [], ArchiveCompressionLevel.Normal, null, false, null, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void CollectFolder_UnreadableSubfolder_Throws()
    {
        // Passed over, it went in as an empty folder and a move deleted the source as if it were whole.
        var root = MakeTree();
        var locked = new DirectoryInfo(Path.Combine(root, "full"));
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var deny = new System.Security.AccessControl.FileSystemAccessRule(
            me, System.Security.AccessControl.FileSystemRights.ListDirectory, System.Security.AccessControl.AccessControlType.Deny);
        var security = locked.GetAccessControl();
        security.AddAccessRule(deny);
        locked.SetAccessControl(security);
        try
        {
            var act = () => ArchiveAddCollector.CollectFolder(root, "tree", null, null).ToList();

            act.Should().Throw<UnauthorizedAccessException>();
        }
        finally
        {
            security.RemoveAccessRule(deny);
            locked.SetAccessControl(security);
        }
    }

    // ── 7z command line ──────────────────────────────────────────────────────

    [Fact]
    public void BuildPackArguments_ExcludeList_IsPassedBeforeTheSources()
    {
        var args = WindowsArchiveService.BuildPackArguments(
            "out.7z", ["C:\\tree"], ArchiveCompressionLevel.Normal, null, forceZipFormat: false, "C:\\ex.txt").ToList();

        args.Should().Contain("-x@C:\\ex.txt").And.Contain("-scsUTF-8");
        args.IndexOf("-x@C:\\ex.txt").Should().BeLessThan(args.IndexOf("--"));
    }

    [Fact]
    public void BuildPackArguments_WithPassword_NeverPutsThePasswordOnTheCommandLine()
    {
        var args = WindowsArchiveService.BuildPackArguments(
            "out.zip", ["C:\\a.txt"], ArchiveCompressionLevel.Normal, "hunter2", forceZipFormat: true);

        args.Should().NotContain(a => a.Contains("hunter2"));
        args.Should().Contain("-p");
    }

    [Fact]
    public void BuildPackArguments_PasswordZip_UsesAes256()
    {
        var args = WindowsArchiveService.BuildPackArguments(
            "out.zip", ["C:\\a.txt"], ArchiveCompressionLevel.Normal, "pw", forceZipFormat: true);

        args.Should().Contain("-mem=AES256");
        args.Should().NotContain("-mhe=on");
    }

    [Fact]
    public void BuildPackArguments_PasswordSevenZip_EncryptsFileNamesToo()
    {
        var args = WindowsArchiveService.BuildPackArguments(
            "out.7z", ["C:\\a.txt"], ArchiveCompressionLevel.Ultra, "pw", forceZipFormat: false);

        args.Should().Contain("-mhe=on").And.Contain("-t7z").And.Contain("-mx=9");
    }

    [Fact]
    public void BuildPackArguments_SourceNamedLikeASwitch_IsAfterTheEndOfOptionsMarker()
    {
        var args = WindowsArchiveService.BuildPackArguments(
            "out.7z", ["-r"], ArchiveCompressionLevel.Normal, null, forceZipFormat: false).ToList();

        args.IndexOf("--").Should().BeLessThan(args.IndexOf("-r"));
    }

    // ── SevenZipOutput ───────────────────────────────────────────────────────

    [Fact]
    public void Accept_PercentLines_BecomeProgressAndOtherLinesTheSummary()
    {
        var reported = new List<double>();
        var output = new SevenZipOutput(new SyncProgress(reported));

        output.Accept(" 45% 3 + a.txt");
        output.Accept("ERROR: Wrong password : a.txt");

        reported.Should().Equal(0.45);
        output.Summary().Should().Contain("Wrong password");
    }

    private sealed class SyncProgress(List<double> sink) : IProgress<double>
    {
        public void Report(double value) => sink.Add(value);
    }

    // ── UnpackPlanner ────────────────────────────────────────────────────────

    [Fact]
    public void FindOverwrites_ExistingFile_IsReported()
    {
        var dest = Path.Combine(_dir, "dest");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "a.txt"), "old");
        var entries = new[]
        {
            new ArchiveEntry("a.txt", "a.txt", false, 3, DateTime.Now),
            new ArchiveEntry("b.txt", "b.txt", false, 3, DateTime.Now),
        };

        UnpackPlanner.FindOverwrites(entries, dest).Should().Equal(Path.Combine(dest, "a.txt"));
    }

    [Fact]
    public void FindOverwrites_EntryEscapingTheDestination_Throws()
    {
        var entries = new[] { new ArchiveEntry("../evil.txt", "evil.txt", false, 1, DateTime.Now) };

        var act = () => UnpackPlanner.FindOverwrites(entries, Path.Combine(_dir, "dest"));

        act.Should().Throw<InvalidDataException>();
    }

    // ── SevenZipLocator ──────────────────────────────────────────────────────

    [Fact]
    public void Find_ExeBesideTheApp_WinsOverAnInstalledCopy()
    {
        var found = SevenZipLocator.Find(
            "C:\\app", ["C:\\Program Files\\7-Zip"], "C:\\bin",
            path => path is "C:\\app\\7z.exe" or "C:\\Program Files\\7-Zip\\7z.exe");

        found.Should().Be("C:\\app\\7z.exe");
    }

    [Fact]
    public void Find_NothingInstalled_FallsBackToPathThenNull()
    {
        SevenZipLocator.Find("C:\\app", [], "C:\\x;C:\\bin", path => path == "C:\\bin\\7z.exe")
            .Should().Be("C:\\bin\\7z.exe");
        SevenZipLocator.Find("C:\\app", [], "C:\\x", _ => false).Should().BeNull();
    }

    [Fact]
    public void Find_SubfolderNextToTheApp_IsFound()
    {
        SevenZipLocator.Find("C:\\app", [], null, path => path == "C:\\app\\7-Zip\\7z.exe")
            .Should().Be("C:\\app\\7-Zip\\7z.exe");
    }

    [Fact]
    public void ScratchPathFor_KeepsTheExtensionAndStaysBesideTheTarget()
    {
        var scratch = PackJob.ScratchPathFor(Path.Combine(_dir, "a.tar"));

        Path.GetExtension(scratch).Should().Be(".tar");
        Path.GetDirectoryName(scratch).Should().Be(_dir);
        Path.GetFileName(scratch).Should().StartWith(".tcv-");
    }

    [Fact]
    public void MapCompressionLevel_CoversEveryDialogChoice()
    {
        PackJob.MapCompressionLevel(ArchiveCompressionLevel.Store).Should().Be(CompressionLevel.NoCompression);
        PackJob.MapCompressionLevel(ArchiveCompressionLevel.Fast).Should().Be(CompressionLevel.Fastest);
        PackJob.MapCompressionLevel(ArchiveCompressionLevel.Ultra).Should().Be(CompressionLevel.SmallestSize);
    }
}
