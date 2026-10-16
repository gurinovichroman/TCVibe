using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.FileSystem;
using TCV.Core.Domain;

namespace TCV.App.Tests;

public class WindowsFileSystemTests
{
    private readonly WindowsFileSystem _sut = new(NullLogger<WindowsFileSystem>.Instance);

    [Fact]
    public async Task ListAsync_SystemDrive_ReturnsEntries()
    {
        var sysFolder = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var path = new VfsPath(sysFolder);

        var entries = await _sut.ListAsync(path);

        entries.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ListAsync_PathWithSubdirs_HasDirectories()
    {
        var path = new VfsPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var entries = await _sut.ListAsync(path);

        entries.Should().Contain(e => e.IsDirectory);
    }

    [Fact]
    public async Task ListAsync_NonExistentPath_ReturnsEmpty()
    {
        var path = new VfsPath(@"C:\ThisPathDoesNotExist_TCV_Test_XYZ");
        var entries = await _sut.ListAsync(path);
        entries.Should().BeEmpty();
    }

    [Fact]
    public async Task GetEntryAsync_ExistingDirectory_ReturnsDirectoryEntry()
    {
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var entry = await _sut.GetEntryAsync(new VfsPath(winDir));

        entry.IsDirectory.Should().BeTrue();
        entry.Name.Should().Be(Path.GetFileName(winDir));
    }

    [Fact]
    public async Task CopyAsync_CopiesFileCorrectly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tcv_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var srcPath = Path.Combine(tempDir, "source.txt");
            var dstPath = Path.Combine(tempDir, "dest.txt");
            await File.WriteAllTextAsync(srcPath, "Hello TCV!");

            await _sut.CopyAsync(new VfsPath(srcPath), new VfsPath(dstPath), null, CancellationToken.None);

            File.Exists(dstPath).Should().BeTrue();
            (await File.ReadAllTextAsync(dstPath)).Should().Be("Hello TCV!");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task CreateDirectoryAsync_CreatesDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tcv_test_{Guid.NewGuid()}");
        try
        {
            await _sut.CreateDirectoryAsync(new VfsPath(tempDir), CancellationToken.None);
            Directory.Exists(tempDir).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir);
        }
    }

    [Fact]
    public async Task DeleteAsync_DeletesFile()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"tcv_test_{Guid.NewGuid()}.txt");
        await File.WriteAllTextAsync(tempFile, "test");
        try
        {
            await _sut.DeleteAsync(new VfsPath(tempFile), recursive: false, CancellationToken.None);
            File.Exists(tempFile).Should().BeFalse();
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task DeleteAsync_RecursiveFolderHoldingAJunction_RemovesItAndLeavesWhatTheJunctionPointsTo()
    {
        // F8 unlinks junctions itself (DeleteOperation); every other recursive delete reaches the file
        // system directly, and without elevation it failed with "access denied" (TRAP-50).
        var root = Path.Combine(Path.GetTempPath(), $"tcv_test_{Guid.NewGuid()}");
        var keep = Path.Combine(root, "store", "keep.txt");
        var doomed = Path.Combine(root, "project");
        Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
        await File.WriteAllTextAsync(keep, "k");
        Directory.CreateDirectory(Path.Combine(doomed, "node_modules"));
        await File.WriteAllTextAsync(Path.Combine(doomed, "a.txt"), "a");
        try
        {
            if (!TryCreateJunction(Path.Combine(doomed, "node_modules", "pkg"), Path.GetDirectoryName(keep)!))
            {
                return;   // no mklink on this machine
            }

            await _sut.DeleteAsync(new VfsPath(doomed), recursive: true, CancellationToken.None);

            Directory.Exists(doomed).Should().BeFalse();
            File.Exists(keep).Should().BeTrue();
        }
        finally
        {
            try
            {
                DirectoryTree.Delete(root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leaked temp folder must not hide the assertion that failed above.
            }
        }
    }

    [Fact]
    public async Task RenameAsync_RenamesFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tcv_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var srcFile = Path.Combine(tempDir, "old.txt");
            await File.WriteAllTextAsync(srcFile, "content");

            await _sut.RenameAsync(new VfsPath(srcFile), "new.txt", CancellationToken.None);

            File.Exists(srcFile).Should().BeFalse();
            File.Exists(Path.Combine(tempDir, "new.txt")).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ListAsync_DriveRoot_ListsTheDriveRoot()
    {
        // A drive root must not degrade into the drive-relative "C:", which would
        // list the process current directory instead.
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var driveRoot = Path.GetPathRoot(windows)!;

        var entries = await _sut.ListAsync(new VfsPath(driveRoot));

        entries.Should().Contain(e =>
            e.Name.Equals(Path.GetFileName(windows), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MoveAsync_ExistingDestination_Overwrites()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tcv_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var srcPath = Path.Combine(tempDir, "source.txt");
            var dstPath = Path.Combine(tempDir, "dest.txt");
            await File.WriteAllTextAsync(srcPath, "new");
            await File.WriteAllTextAsync(dstPath, "old");

            await _sut.MoveAsync(new VfsPath(srcPath), new VfsPath(dstPath), CancellationToken.None);

            File.Exists(srcPath).Should().BeFalse();
            (await File.ReadAllTextAsync(dstPath)).Should().Be("new");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task MoveAsync_Directory_MovesContents()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tcv_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(Path.Combine(tempDir, "src", "nested"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir, "src", "nested", "file.txt"), "content");

            await _sut.MoveAsync(
                new VfsPath(Path.Combine(tempDir, "src")),
                new VfsPath(Path.Combine(tempDir, "dst")),
                CancellationToken.None);

            Directory.Exists(Path.Combine(tempDir, "src")).Should().BeFalse();
            (await File.ReadAllTextAsync(Path.Combine(tempDir, "dst", "nested", "file.txt")))
                .Should().Be("content");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void SupportsOperation_ReturnsTrue_ForAllOperations()
    {
        foreach (var op in Enum.GetValues<TCV.Core.Domain.VfsOperation>())
            _sut.SupportsOperation(op).Should().BeTrue();
    }

    [Fact]
    public void ToWindowsPath_ConvertsForwardSlashes()
    {
        WindowsFileSystem.ToWindowsPath(new VfsPath("C:/Users/test")).Should().Be(@"C:\Users\test");
    }

    /// <summary>A junction needs no privilege, unlike a symlink. False when even that is unavailable.</summary>
    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
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
