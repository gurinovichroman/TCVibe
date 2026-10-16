using System.IO;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using TCV.App.ViewModels.Dialogs;
using TCV.Core.Domain;
using TCV.Core.Interfaces;
using TCV.Tools;

namespace TCV.App.Tests;

/// <summary>"Checksums" on a checksum file verifies it instead of hashing the list itself (6.55).</summary>
public sealed class ChecksumDialogStartTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tcv-csum-" + Guid.NewGuid().ToString("N"));

    public ChecksumDialogStartTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static IFileEntry Entry(string windowsPath, bool isDirectory = false) =>
        new FileEntry(new VfsPath(windowsPath), Path.GetFileName(windowsPath), isDirectory, 10, DateTime.Now,
            isDirectory ? FileAttributes.Directory : FileAttributes.Normal);

    [Fact]
    public void VerifyTargetFor_OneChecksumFile_IsThatFileAsAWindowsPath()
    {
        ChecksumDialogViewModel.VerifyTargetFor([Entry(@"D:\iso\ubuntu.sha256")])
            .Should().Be(@"D:\iso\ubuntu.sha256");
    }

    [Fact]
    public void VerifyTargetFor_OrdinaryFile_IsNull()
    {
        ChecksumDialogViewModel.VerifyTargetFor([Entry(@"D:\iso\ubuntu.iso")]).Should().BeNull();
    }

    [Fact]
    public void VerifyTargetFor_SeveralFilesIncludingAChecksumFile_IsNull()
    {
        ChecksumDialogViewModel.VerifyTargetFor([Entry(@"D:\iso\ubuntu.iso"), Entry(@"D:\iso\ubuntu.sha256")])
            .Should().BeNull("several files are picked to get their digests, the list among them included");
    }

    [Fact]
    public void VerifyTargetFor_FolderNamedLikeAChecksumFile_IsNull()
    {
        ChecksumDialogViewModel.VerifyTargetFor([Entry(@"D:\iso\old.md5", isDirectory: true)]).Should().BeNull();
    }

    [Fact]
    public void Constructor_OnAChecksumFile_OpensOnTheVerifyTabAndKeepsTheFileHashable()
    {
        var vm = new ChecksumDialogViewModel([Entry(@"D:\iso\ubuntu.sha256")], @"D:\iso", new ChecksumEngine());

        vm.VerifyMode.Should().BeTrue();
        vm.ChecksumFileToVerify.Should().Be(@"D:\iso\ubuntu.sha256");
        vm.Entries.Should().ContainSingle(e => e.FileName == "ubuntu.sha256",
            "the Calculate tab still offers the file's own digest");
    }

    [Fact]
    public void Constructor_OnAnOrdinaryFile_StaysOnTheCalculateTab()
    {
        var vm = new ChecksumDialogViewModel([Entry(@"D:\iso\ubuntu.iso")], @"D:\iso", new ChecksumEngine());

        vm.VerifyMode.Should().BeFalse();
        vm.ChecksumFileToVerify.Should().BeNull();
    }

    [Fact]
    public async Task StartAsync_OnAChecksumFile_VerifiesTheFilesItLists()
    {
        var data = Path.Combine(_dir, "data.bin");
        await File.WriteAllTextAsync(data, "payload", Encoding.UTF8);
        var digest = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(data))).ToLowerInvariant();
        var sums = Path.Combine(_dir, "data.sha256");
        await File.WriteAllTextAsync(sums, $"{digest}  data.bin\nffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff  gone.bin\n");

        var vm = new ChecksumDialogViewModel([Entry(sums)], _dir, new ChecksumEngine());
        await vm.StartAsync();

        vm.VerifyFilePath.Should().Be(sums);
        vm.VerifyResults.Should().HaveCount(2);
        vm.VerifyResults.Single(r => r.FileName == "data.bin").Status.Should().Be("OK");
        vm.VerifyResults.Single(r => r.FileName == "gone.bin").Status.Should().Be("Missing");
        vm.IsCalculating.Should().BeFalse();
    }

    [Fact]
    public async Task StartAsync_OnAnOrdinaryFile_DoesNothing()
    {
        var vm = new ChecksumDialogViewModel([Entry(@"D:\iso\ubuntu.iso")], @"D:\iso", new ChecksumEngine());

        await vm.StartAsync();

        vm.VerifyResults.Should().BeEmpty();
        vm.VerifyFilePath.Should().BeEmpty();
    }
}
