using System.IO;
using FluentAssertions;
using TCV.App.Operations;
using TCV.App.ViewModels.Dialogs;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Tests;

/// <summary>
/// The copy dialog shows only the options the transfer honours: out of an archive there are no threads
/// (it is read in one pass), into one neither threads nor "keep the dates" (6.47, 6.55).
/// </summary>
public sealed class CopyDialogOptionSetTests
{
    private static IFileEntry File(string name) =>
        new FileEntry(new VfsPath("/x/" + name), name, false, 10, new DateTime(2025, 1, 1), FileAttributes.Normal);

    [Fact]
    public void BuildOptions_OutOfAnArchive_HidesAndIgnoresTheThreads()
    {
        var vm = new CopyDialogViewModel([File("a")], @"D:\out", isMove: false,
            CopyOptionSet.Timestamps | CopyOptionSet.OnlyIfNewer)
        {
            ParallelCopy = true,
            Threads = 8,
            PreserveTimestamps = false,
            OnlyIfNewer = true,
        };

        vm.ShowCopyOptions.Should().BeTrue();
        vm.ShowParallel.Should().BeFalse();
        vm.ShowTimestamps.Should().BeTrue();
        vm.ShowOnlyIfNewer.Should().BeTrue();
        var options = vm.BuildOptions();
        options.Threads.Should().Be(1);
        options.PreserveTimestamps.Should().BeFalse();
        options.OnlyIfNewer.Should().BeTrue();
    }

    [Fact]
    public void BuildOptions_IntoAnArchive_KeepsTheDatesWhateverTheHiddenBoxSays()
    {
        var vm = new CopyDialogViewModel([File("a")], "/", isMove: false, CopyOptionSet.OnlyIfNewer)
        {
            PreserveTimestamps = false,
            ParallelCopy = true,
            OnlyIfNewer = true,
        };

        vm.ShowTimestamps.Should().BeFalse();
        vm.ShowParallel.Should().BeFalse();
        var options = vm.BuildOptions();
        options.PreserveTimestamps.Should().BeTrue();
        options.Threads.Should().Be(1);
        options.OnlyIfNewer.Should().BeTrue();
    }

    [Fact]
    public void BuildOptions_ByDefault_OffersEverything()
    {
        var vm = new CopyDialogViewModel([File("a")], @"D:\out", isMove: false) { ParallelCopy = true, Threads = 4 };

        vm.ShowTimestamps.Should().BeTrue();
        vm.ShowOnlyIfNewer.Should().BeTrue();
        vm.ShowParallel.Should().BeTrue();
        vm.BuildOptions().Threads.Should().Be(4);
    }

    [Fact]
    public void ShowCopyOptions_NothingOffered_IsFalse()
    {
        new CopyDialogViewModel([File("a")], "/", isMove: false, CopyOptionSet.None)
            .ShowCopyOptions.Should().BeFalse();
    }

    [Fact]
    public void UnpackDialog_SeveralArchives_CountsThemInsteadOfNamingOne()
    {
        // Without a loaded dictionary the text is the key in brackets: enough to tell which one was used.
        new UnpackDialogViewModel(["a.zip"], @"D:\out", canUseWindowsShell: true)
            .Description.Should().Be("[Unpack_Description]");
        new UnpackDialogViewModel(["a.zip", "b.7z"], @"D:\out", canUseWindowsShell: false)
            .Description.Should().Be("[Unpack_DescriptionMany]");
    }
}
