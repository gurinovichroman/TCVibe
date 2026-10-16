using System.Diagnostics;
using FluentAssertions;
using TCV.Core.Domain;

namespace TCV.Core.Tests;

public sealed class DirectoryTreeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tcv-tree-" + Guid.NewGuid().ToString("N")[..8]);

    public DirectoryTreeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        // A plain recursive delete is the very thing that fails on a junction (TRAP-50).
        try
        {
            DirectoryTree.Delete(_dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leaked temp folder is not worth failing a test over.
        }
    }

    [Fact]
    public void Delete_TreeHoldingAJunction_RemovesItAndLeavesWhatTheJunctionPointsTo()
    {
        // The pnpm layout: node_modules full of junctions into a store outside the project.
        var keep = Write("store", "keep.txt");
        File.SetAttributes(keep, FileAttributes.ReadOnly);
        Write("project", "a.txt");
        Directory.CreateDirectory(P("project", "node_modules"));
        if (!TryCreateJunction(P("project", "node_modules", "pkg"), P("store")))
        {
            return;   // no mklink on this machine
        }

        DirectoryTree.Delete(P("project"));

        Directory.Exists(P("project")).Should().BeFalse();
        File.Exists(keep).Should().BeTrue("a junction goes as a link, its target is not part of the tree");
        File.GetAttributes(keep).Should().HaveFlag(FileAttributes.ReadOnly, "nothing behind a junction is touched");
    }

    [Fact]
    public void Delete_ReadOnlyFilesInAHiddenFolder_AreRemovedToo()
    {
        // Git keeps its object store read-only, and .git is hidden.
        var gitDir = Directory.CreateDirectory(P("repo", ".git"));
        gitDir.Attributes |= FileAttributes.Hidden;
        File.SetAttributes(Write("repo", ".git", "objects", "ab"), FileAttributes.ReadOnly);
        File.SetAttributes(Write("repo", "tracked.txt"), FileAttributes.ReadOnly);

        DirectoryTree.Delete(P("repo"));

        Directory.Exists(P("repo")).Should().BeFalse();
    }

    [Fact]
    public void Delete_ReadOnlyFolders_AreRemovedToo()
    {
        // Windows marks a customised folder read-only, and RemoveDirectory refuses one with "access denied".
        Write("tree", "custom", "a.txt");
        new DirectoryInfo(P("tree", "custom")).Attributes |= FileAttributes.ReadOnly;
        new DirectoryInfo(P("tree")).Attributes |= FileAttributes.ReadOnly;

        DirectoryTree.Delete(P("tree"));

        Directory.Exists(P("tree")).Should().BeFalse();
    }

    [Fact]
    public void Delete_PathIsAJunction_RemovesOnlyTheLink()
    {
        var keep = Write("target", "keep.txt");
        if (!TryCreateJunction(P("link"), P("target")))
        {
            return;
        }

        DirectoryTree.Delete(P("link"));

        Directory.Exists(P("link")).Should().BeFalse();
        File.Exists(keep).Should().BeTrue();
    }

    [Fact]
    public void FindDirectoryLink_NestedJunction_ReturnsIt()
    {
        Write("target", "keep.txt");
        Write("tree", "a.txt");
        Directory.CreateDirectory(P("tree", "deep", "node_modules"));
        if (!TryCreateJunction(P("tree", "deep", "node_modules", "pkg"), P("target")))
        {
            return;
        }

        DirectoryTree.FindDirectoryLink(P("tree")).Should().Be(P("tree", "deep", "node_modules", "pkg"));
    }

    [Fact]
    public void FindDirectoryLink_TreeWithoutLinks_ReturnsNull()
    {
        Write("tree", "sub", "a.txt");

        DirectoryTree.FindDirectoryLink(P("tree")).Should().BeNull();
    }

    private string P(params string[] parts) => Path.Combine([_dir, .. parts]);

    private string Write(params string[] parts)
    {
        var path = P(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    /// <summary>A junction needs no privilege, unlike a symlink. False when even that is unavailable.</summary>
    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10_000);

            return process.ExitCode == 0 && Directory.Exists(link);
        }
        catch
        {
            return false;
        }
    }
}
