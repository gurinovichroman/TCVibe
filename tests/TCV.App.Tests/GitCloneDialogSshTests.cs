using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.ViewModels.Dialogs;
using TCV.Git;
using Xunit;

namespace TCV.App.Tests;

/// <summary>
/// ROADMAP 6.54: an SSH link (<c>git@host:o/r</c>) passed the clone dialog's check and was pasted
/// in from the clipboard, then always failed. The dialog now says what will happen before Clone is
/// pressed: a well-known host is cloned over https, any other SSH link is refused with what to do.
/// </summary>
public class GitCloneDialogSshTests
{
    private static GitCloneDialogViewModel CloneVm(string url = "") =>
        new(new GitCloneService(NullLogger<GitCloneService>.Instance), @"D:\work", url);

    [Fact]
    public void Url_SshLinkToAWellKnownHost_ClonesOverHttpsAndSaysSo()
    {
        var vm = CloneVm();

        vm.Url = "git@github.com:owner/some-repo.git";

        vm.ResolvedUrl.Url.Should().Be("https://github.com/owner/some-repo.git");
        vm.InfoMessage.Should().Contain("Git_CloneViaHttps");
        vm.ValidationMessage.Should().BeEmpty();
        vm.TargetPath.Should().Be(@"D:\work\some-repo");
        vm.Url.Should().Be("git@github.com:owner/some-repo.git", "the box keeps what the user typed");
    }

    [Fact]
    public void Url_SshLinkToAnUnknownHost_IsRefusedBeforeCloning()
    {
        var vm = CloneVm();

        vm.Url = "git@git.example.com:owner/repo.git";

        vm.CanClone.Should().BeFalse();
        vm.ValidationMessage.Should().Contain("Git_ErrSsh");
        vm.InfoMessage.Should().BeEmpty();
    }

    [Fact]
    public void Clipboard_SshLinkToAnUnknownHost_IsFilledInWithTheReasonShown()
    {
        var vm = CloneVm("git@git.example.com:owner/repo.git");

        vm.Url.Should().Be("git@git.example.com:owner/repo.git");
        vm.CanClone.Should().BeFalse();
        vm.ValidationMessage.Should().NotBeEmpty();
    }

    [Fact]
    public void Url_Https_NeedsNoNote()
    {
        var vm = CloneVm();

        vm.Url = "https://github.com/owner/some-repo.git";

        vm.InfoMessage.Should().BeEmpty();
        vm.ValidationMessage.Should().BeEmpty();
    }
}
