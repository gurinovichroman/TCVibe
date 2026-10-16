using System.Text;
using FluentAssertions;
using TCV.App.Navigation;
using TCV.Core.Domain;

namespace TCV.App.Tests;

public class FileIndexWalkerTests
{
    private static InMemoryFileSystem Fs(params string[] files)
    {
        var fs = new InMemoryFileSystem("file");
        fs.AddDirectory("/repo");
        foreach (var f in files)
            fs.AddFile(f, new byte[] { 1 });
        return fs;
    }

    private static void AddText(InMemoryFileSystem fs, string path, string content) =>
        fs.AddFile(path, Encoding.UTF8.GetBytes(content));

    private static async Task<List<FileIndexEntry>> WalkAsync(
        InMemoryFileSystem fs, FileIndexOptions? options = null, string root = "/repo")
    {
        var walker = new FileIndexWalker(fs, options);
        var result = new List<FileIndexEntry>();
        await foreach (var entry in walker.EnumerateAsync(new VfsPath(root)))
            result.Add(entry);
        return result;
    }

    [Fact]
    public async Task Enumerate_ReturnsFilesAndDirectories_WithPathsRelativeToTheRoot()
    {
        var fs = Fs("/repo/readme.md", "/repo/src/app.cs");

        var entries = await WalkAsync(fs);

        entries.Select(e => e.RelativePath).Should().BeEquivalentTo("readme.md", "src", "src/app.cs");
        entries.Single(e => e.RelativePath == "src").IsDirectory.Should().BeTrue();
        entries.Single(e => e.RelativePath == "src/app.cs").FullPath.Should().Be("/repo/src/app.cs");
    }

    [Fact]
    public async Task Enumerate_IsBreadthFirst_SoShallowEntriesArriveFirst()
    {
        var fs = Fs("/repo/a/b/deep.txt", "/repo/top.txt");

        var entries = await WalkAsync(fs);

        // Depth-first would have finished the "a" branch before reaching top.txt
        entries.Select(e => e.RelativePath).Last().Should().Be("a/b/deep.txt");
    }

    [Fact]
    public async Task Enumerate_WithoutDirectories_ReturnsFilesOnly()
    {
        var fs = Fs("/repo/src/app.cs");

        var entries = await WalkAsync(fs, new FileIndexOptions { IncludeDirectories = false });

        entries.Should().OnlyContain(e => !e.IsDirectory);
        entries.Should().ContainSingle(e => e.RelativePath == "src/app.cs", "it still descends");
    }

    [Fact]
    public async Task Enumerate_SkipsVcsMetadata()
    {
        var fs = Fs("/repo/.git/config", "/repo/app.cs");

        var entries = await WalkAsync(fs);

        entries.Should().NotContain(e => e.RelativePath.StartsWith(".git"));
        entries.Should().ContainSingle(e => e.RelativePath == "app.cs");
    }

    [Fact]
    public async Task Enumerate_KeepsDotFiles_TheyAreWhatDevelopersLookFor()
    {
        var fs = Fs("/repo/.gitignore", "/repo/.github/workflows/ci.yml");

        var entries = await WalkAsync(fs);

        entries.Select(e => e.RelativePath).Should().Contain(".gitignore");
        entries.Select(e => e.RelativePath).Should().Contain(".github/workflows/ci.yml");
    }

    [Fact]
    public async Task Enumerate_AppliesGitignoreFromTheRoot()
    {
        var fs = Fs("/repo/app.cs", "/repo/app.log", "/repo/bin/app.exe");
        AddText(fs, "/repo/.gitignore", "*.log\nbin/\n");

        var entries = await WalkAsync(fs);

        entries.Select(e => e.RelativePath).Should().Contain("app.cs");
        entries.Select(e => e.RelativePath).Should().NotContain("app.log");
        entries.Should().NotContain(e => e.RelativePath.StartsWith("bin"),
            "an ignored directory is never entered");
    }

    [Fact]
    public async Task Enumerate_AppliesNestedGitignore_RelativeToItsOwnDirectory()
    {
        var fs = Fs("/repo/src/keep.cs", "/repo/src/skip.gen.cs", "/repo/skip.gen.cs");
        AddText(fs, "/repo/src/.gitignore", "*.gen.cs\n");

        var entries = await WalkAsync(fs);

        entries.Select(e => e.RelativePath).Should().Contain("src/keep.cs");
        entries.Select(e => e.RelativePath).Should().NotContain("src/skip.gen.cs");
        entries.Select(e => e.RelativePath).Should().Contain("skip.gen.cs",
            "the nested rule does not reach above its own directory");
    }

    [Fact]
    public async Task Enumerate_NestedNegation_ReIncludesWhatTheRootIgnored()
    {
        var fs = Fs("/repo/app.log", "/repo/logs/keep.log");
        AddText(fs, "/repo/.gitignore", "*.log\n");
        AddText(fs, "/repo/logs/.gitignore", "!*.log\n");

        var entries = await WalkAsync(fs);

        entries.Select(e => e.RelativePath).Should().NotContain("app.log");
        entries.Select(e => e.RelativePath).Should().Contain("logs/keep.log");
    }

    [Fact]
    public async Task Enumerate_WithGitignoreDisabled_ReturnsIgnoredFilesToo()
    {
        var fs = Fs("/repo/app.log");
        AddText(fs, "/repo/.gitignore", "*.log\n");

        var entries = await WalkAsync(fs, new FileIndexOptions { RespectGitignore = false });

        entries.Select(e => e.RelativePath).Should().Contain("app.log");
    }

    [Fact]
    public async Task Enumerate_StopsAtMaxEntries_AndReportsTruncation()
    {
        var fs = Fs(Enumerable.Range(0, 20).Select(i => $"/repo/f{i}.txt").ToArray());
        var walker = new FileIndexWalker(fs, new FileIndexOptions { MaxEntries = 5 });

        var count = 0;
        await foreach (var _ in walker.EnumerateAsync(new VfsPath("/repo")))
            count++;

        count.Should().Be(5);
        walker.WasTruncated.Should().BeTrue();
    }

    [Fact]
    public async Task Enumerate_RespectsMaxDepth()
    {
        var fs = Fs("/repo/a/b/c/deep.txt");

        var entries = await WalkAsync(fs, new FileIndexOptions { MaxDepth = 1 });

        entries.Select(e => e.RelativePath).Should().Contain("a/b");
        entries.Select(e => e.RelativePath).Should().NotContain("a/b/c");
    }

    [Fact]
    public async Task Enumerate_UnreadableDirectory_DoesNotAbortTheWalk()
    {
        // A directory record with no listing behind it stands in for one we cannot read
        var fs = Fs("/repo/ok.txt");
        fs.AddDirectory("/repo/ghost");
        fs.AddFile("/repo/ghost/inner.txt", new byte[] { 1 });
        fs.FailListingOf = "/repo/ghost";

        var entries = await WalkAsync(fs);

        entries.Select(e => e.RelativePath).Should().Contain("ok.txt");
        entries.Select(e => e.RelativePath).Should().NotContain("ghost/inner.txt");
    }

    [Fact]
    public async Task Enumerate_Cancellation_Throws()
    {
        var fs = Fs("/repo/a.txt");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var walker = new FileIndexWalker(fs);
        var act = async () =>
        {
            await foreach (var _ in walker.EnumerateAsync(new VfsPath("/repo"), cts.Token)) { }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void RelativeDirectory_IsTheParentPart_EmptyAtTheRoot()
    {
        new FileIndexEntry("/repo/a/b.txt", "a/b.txt", "b.txt", false).RelativeDirectory.Should().Be("a");
        new FileIndexEntry("/repo/b.txt", "b.txt", "b.txt", false).RelativeDirectory.Should().BeEmpty();
    }

    [Fact]
    public void DisplayName_MarksDirectoriesWithASlash()
    {
        new FileIndexEntry("/repo/src", "src", "src", true).DisplayName.Should().Be("src/");
        new FileIndexEntry("/repo/a.txt", "a.txt", "a.txt", false).DisplayName.Should().Be("a.txt");
    }
}

public class ProjectRootLocatorTests
{
    [Fact]
    public async Task Find_ClimbsToTheDirectoryHoldingDotGit()
    {
        var fs = new InMemoryFileSystem("file");
        fs.AddDirectory("/C:/work/repo/.git");
        fs.AddDirectory("/C:/work/repo/src/deep");

        var root = await ProjectRootLocator.FindAsync(fs, "/C:/work/repo/src/deep");

        root.Should().Be("/C:/work/repo");
    }

    [Fact]
    public async Task Find_AcceptsDotGitAsAFile_AsInAWorktree()
    {
        var fs = new InMemoryFileSystem("file");
        fs.AddFile("/C:/work/wt/.git", new byte[] { 1 });
        fs.AddDirectory("/C:/work/wt/src");

        var root = await ProjectRootLocator.FindAsync(fs, "/C:/work/wt/src");

        root.Should().Be("/C:/work/wt");
    }

    [Fact]
    public async Task Find_StartsAtTheMarkerItself_WhenAlreadyAtTheRoot()
    {
        var fs = new InMemoryFileSystem("file");
        fs.AddDirectory("/C:/work/repo/.git");

        var root = await ProjectRootLocator.FindAsync(fs, "/C:/work/repo");

        root.Should().Be("/C:/work/repo");
    }

    [Fact]
    public async Task Find_OutsideAnyRepository_ReturnsTheStartPath()
    {
        var fs = new InMemoryFileSystem("file");
        fs.AddDirectory("/C:/temp/loose");

        var root = await ProjectRootLocator.FindAsync(fs, "/C:/temp/loose");

        root.Should().Be("/C:/temp/loose");
    }

    [Fact]
    public async Task Find_EmptyPath_IsReturnedUnchanged()
    {
        var fs = new InMemoryFileSystem("file");

        (await ProjectRootLocator.FindAsync(fs, "")).Should().BeEmpty();
    }
}
