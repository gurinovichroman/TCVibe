using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using FluentAssertions;
using TCV.App.Services;
using TCV.Archives;
using TCV.Core.Domain;
using TCV.Core.PluginSdk;

namespace TCV.App.Tests;

/// <summary>
/// Packing (Alt+F5, ZIP and TAR) does not enter junctions and folder links (6.56): a junction to an
/// ancestor used to pack the tree into itself until the path got too long, and one to another tree
/// packed that tree. Each link is left out and reported, as a copy and a delete treat it.
/// </summary>
public sealed class PackLinksTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tcv-packlinks-" + Guid.NewGuid().ToString("N"));

    public PackLinksTests() => Directory.CreateDirectory(_dir);

    // Not Directory.Delete: it fails on a junction without elevation (TRAP-50).
    public void Dispose() => DirectoryTree.Delete(_dir);

    private string MakeTree()
    {
        var root = Path.Combine(_dir, "tree");
        Directory.CreateDirectory(Path.Combine(root, "full"));
        File.WriteAllText(Path.Combine(root, "full", "a.txt"), "AAA");
        File.WriteAllText(Path.Combine(root, "top.txt"), "TOP");
        return root;
    }

    /// <summary>A junction needs no privilege, unlike a symlink. False where the file system has none.</summary>
    private static bool TryCreateJunction(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        mklink.WaitForExit();
        return Directory.Exists(link);
    }

    [Fact]
    public void Collect_JunctionToAnAncestor_IsNotEnteredAndIsReported()
    {
        var root = MakeTree();
        if (!TryCreateJunction(Path.Combine(root, "full", "loop"), root))
        {
            return;
        }

        var skipped = new List<string>();
        var entries = PackEntryCollector.Collect([root], onSkipped: skipped.Add).Select(e => e.entryName).ToList();

        skipped.Should().Equal("tree/full/loop");
        entries.Should().BeEquivalentTo("tree/full/a.txt", "tree/top.txt");
    }

    [Fact]
    public void Collect_JunctionToAnotherTree_LeavesThatTreeOut()
    {
        var root = MakeTree();
        var elsewhere = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "secret.txt"), "not part of the tree");
        if (!TryCreateJunction(Path.Combine(root, "linked"), elsewhere))
        {
            return;
        }

        var skipped = new List<string>();
        var entries = PackEntryCollector.Collect([root], onSkipped: skipped.Add).Select(e => e.entryName).ToList();

        skipped.Should().Equal("tree/linked");
        entries.Should().NotContain(e => e.StartsWith("tree/linked"));
    }

    [Fact]
    public void Collect_SelectedJunction_IsLeftOutWhole()
    {
        var root = MakeTree();
        var link = Path.Combine(_dir, "link");
        if (!TryCreateJunction(link, root))
        {
            return;
        }

        var skipped = new List<string>();
        var entries = PackEntryCollector.Collect([link], onSkipped: skipped.Add).ToList();

        skipped.Should().Equal("link");
        entries.Should().BeEmpty();
    }

    [Fact]
    public void Collect_FolderHoldingOnlyAJunction_KeepsItsOwnEntry()
    {
        var root = MakeTree();
        var holder = Path.Combine(root, "holder");
        Directory.CreateDirectory(holder);
        if (!TryCreateJunction(Path.Combine(holder, "link"), Path.Combine(root, "full")))
        {
            return;
        }

        var entries = PackEntryCollector.Collect([root]).Select(e => e.entryName).ToList();

        entries.Should().Contain("tree/holder/");
        entries.Should().NotContain(e => e.StartsWith("tree/holder/link"));
    }

    [Theory]
    [InlineData(ArchiveFormat.Zip)]
    [InlineData(ArchiveFormat.Tar)]
    public async Task RunAsync_TreeWithAJunctionLoop_PacksTheTreeOnceAndReportsTheLink(ArchiveFormat format)
    {
        var root = MakeTree();
        if (!TryCreateJunction(Path.Combine(root, "loop"), root))
        {
            return;
        }

        var archive = Path.Combine(_dir, format == ArchiveFormat.Zip ? "out.zip" : "out.tar");
        var skipped = new List<string>();

        await PackJob.RunAsync(
            new PackDialogResult(archive, format, ArchiveCompressionLevel.Normal, null, false),
            [root], new ArchivesPlugin(), sevenZipAvailable: false, progress: null, CancellationToken.None,
            skipped.Add);

        skipped.Should().Equal("tree/loop");
        var names = new ArchivesPlugin().CreateEngine(archive).ListAll(archive)
            .Where(e => !e.IsDirectory)
            .Select(e => e.FullPath.TrimStart('/'))
            .ToList();
        names.Should().BeEquivalentTo("tree/full/a.txt", "tree/top.txt");
    }

    [Fact]
    public void Pick_Selection_KeepsOnlyTheArchivesInOrder()
    {
        (string, string, bool)[] selected =
        [
            ("b.zip", @"C:\x\b.zip", false),
            ("folder", @"C:\x\folder", true),
            ("notes.txt", @"C:\x\notes.txt", false),
            ("a.7z", @"C:\x\a.7z", false),
        ];

        var picked = UnpackSelection.Pick(selected,
            path => path.EndsWith(".zip", StringComparison.Ordinal) || path.EndsWith(".7z", StringComparison.Ordinal),
            path => path.EndsWith(".zip", StringComparison.Ordinal));

        picked.Should().Equal(
            new UnpackArchive("b.zip", @"C:\x\b.zip", true),
            new UnpackArchive("a.7z", @"C:\x\a.7z", false));
    }

    [Fact]
    public void Pick_NothingIsAnArchive_IsEmpty()
    {
        UnpackSelection.Pick([("notes.txt", @"C:\x\notes.txt", false)], _ => false, _ => false)
            .Should().BeEmpty();
    }
}
