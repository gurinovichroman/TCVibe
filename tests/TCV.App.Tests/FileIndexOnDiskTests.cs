using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.FileSystem;
using TCV.App.Navigation;
using TCV.Core.Domain;

namespace TCV.App.Tests;

/// <summary>
/// The index over the real <see cref="WindowsFileSystem"/>, on a throwaway tree. Covers what the
/// in-memory fake cannot: Windows paths going in, VFS paths coming out, and a <c>.gitignore</c>
/// read from an actual file.
/// </summary>
public class FileIndexOnDiskTests : IDisposable
{
    private readonly WindowsFileSystem _fs = new(NullLogger<WindowsFileSystem>.Instance);
    private readonly string _root;

    public FileIndexOnDiskTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tcv-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(Path.Combine(_root, "readme.md"), "hi");
        File.WriteAllText(Path.Combine(_root, "src", "app.cs"), "class A {}");
        File.WriteAllText(Path.Combine(_root, "bin", "app.exe"), "MZ");
        File.WriteAllText(Path.Combine(_root, ".git", "config"), "[core]");
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "bin/\n*.tmp\n");
        File.WriteAllText(Path.Combine(_root, "scratch.tmp"), "x");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private async Task<List<FileIndexEntry>> WalkAsync(FileIndexOptions? options = null)
    {
        var walker = new FileIndexWalker(_fs, options);
        var result = new List<FileIndexEntry>();
        await foreach (var entry in walker.EnumerateAsync(new VfsPath(_root)))
        {
            result.Add(entry);
        }

        return result;
    }

    [Fact]
    public async Task Walk_HonoursTheGitignoreOnDisk_AndSkipsTheGitDirectory()
    {
        var entries = await WalkAsync();
        var paths = entries.Select(e => e.RelativePath).ToList();

        paths.Should().Contain("readme.md");
        paths.Should().Contain("src/app.cs");
        paths.Should().Contain(".gitignore", "dot-files are what a developer searches for");
        paths.Should().NotContain("scratch.tmp");
        paths.Should().NotContain("bin");
        paths.Should().NotContain(e => e.StartsWith(".git/"));
    }

    [Fact]
    public async Task Walk_ProducesFullPathsThatResolveBackToRealFiles()
    {
        var entries = await WalkAsync();

        var app = entries.Single(e => e.RelativePath == "src/app.cs");
        var onDisk = WindowsFileSystem.ToWindowsPath(new VfsPath(app.FullPath));

        File.Exists(onDisk).Should().BeTrue("the picker navigates to this path");
        Path.GetFileName(onDisk).Should().Be("app.cs");
    }

    [Fact]
    public async Task Walk_WithGitignoreDisabled_SeesTheIgnoredFiles()
    {
        var entries = await WalkAsync(new FileIndexOptions { RespectGitignore = false });

        entries.Select(e => e.RelativePath).Should().Contain("scratch.tmp").And.Contain("bin");
    }

    [Fact]
    public async Task ProjectRoot_IsFoundFromASubdirectory_ByTheDotGitMarker()
    {
        var root = await ProjectRootLocator.FindAsync(_fs, Path.Combine(_root, "src"));

        WindowsFileSystem.ToWindowsPath(new VfsPath(root)).Should().Be(_root);
    }

    [Fact]
    public async Task Walk_Junction_IsListedButNotEntered()
    {
        var link = Path.Combine(_root, "loop");
        var mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "cmd.exe", $"/c mklink /J \"{link}\" \"{_root}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        mk.WaitForExit();
        if (!Directory.Exists(link))
        {
            return;   // junctions unavailable on this volume
        }

        var paths = (await WalkAsync(new FileIndexOptions { RespectGitignore = false, MaxDepth = 6 }))
            .Select(e => e.RelativePath).ToList();

        paths.Should().Contain("loop");
        paths.Should().NotContain(p => p.StartsWith("loop/", StringComparison.Ordinal),
            "following the junction would index the whole tree again under it");
    }
}
