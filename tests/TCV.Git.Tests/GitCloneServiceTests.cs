using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace TCV.Git.Tests;

/// <summary>
/// Clone against a local repository — git treats a path as a perfectly good remote, so the whole
/// path (options, progress callbacks, the working-directory the caller is handed back) runs for
/// real without a network.
/// </summary>
public class GitCloneServiceTests : IDisposable
{
    private readonly GitCloneService _service = new(NullLogger<GitCloneService>.Instance);
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), $"tcv_clone_{Guid.NewGuid():N}");

    public GitCloneServiceTests() => Directory.CreateDirectory(_workspace);

    public void Dispose() => TempRepository.DeleteTree(_workspace);

    [Fact]
    public async Task CloneAsync_LocalRepository_ProducesAWorkingCheckout()
    {
        using var source = new TempRepository("clone-src");
        var target = Path.Combine(_workspace, "cloned");

        var result = await _service.CloneAsync(source.Path, target);

        result.Success.Should().BeTrue(result.Message);
        // The message is the working directory, not the .git folder — the panel opens the former.
        result.Message.TrimEnd('\\').Should().Be(target);
        File.Exists(Path.Combine(target, "README.md")).Should().BeTrue();
        GitHeadReader.ReadBranch(target).Should().Be(source.InitialBranch);
    }

    [Fact]
    public async Task CloneAsync_ReportsProgress()
    {
        using var source = new TempRepository("clone-progress");
        var target = Path.Combine(_workspace, "with-progress");
        var stages = new List<string>();

        await _service.CloneAsync(
            source.Path, target, new Progress<GitCloneProgress>(p => stages.Add(p.Stage)));

        // Reported synchronously from the native callbacks, so nothing is lost to the dispatcher.
        stages.Should().Contain("connecting");
        stages.Should().Contain("done");
    }

    [Fact]
    public async Task CloneAsync_NotAUrl_IsRefusedBeforeTouchingTheDisk()
    {
        var target = Path.Combine(_workspace, "never");

        var result = await _service.CloneAsync("not a url", target);

        result.Success.Should().BeFalse();
        result.Kind.Should().Be(GitFailureKind.InvalidUrl);
        result.Detail.Should().Be("not a url");
        Directory.Exists(target).Should().BeFalse();
    }

    [Fact]
    public async Task CloneAsync_NonEmptyTarget_IsRefusedWithoutTouchingIt()
    {
        using var source = new TempRepository("clone-occupied");
        var target = Path.Combine(_workspace, "occupied");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "mine.txt"), "keep me");

        var result = await _service.CloneAsync(source.Path, target);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("not empty");
        File.ReadAllText(Path.Combine(target, "mine.txt")).Should().Be("keep me");
    }

    [Fact]
    public async Task CloneAsync_UnreachableSource_LeavesNoPartialDirectoryBehind()
    {
        // Otherwise the next attempt fails with "directory is not empty" and blames the user.
        var missing = Path.Combine(_workspace, "no-such-repo");
        var target = Path.Combine(_workspace, "failed");

        var result = await _service.CloneAsync(missing, target);

        result.Success.Should().BeFalse();
        Directory.Exists(target).Should().BeFalse();
    }

    [Fact]
    public async Task CloneAsync_FailingIntoADirectoryPickedByHand_LeavesItEmptyForTheRetry()
    {
        // The folder existed before the clone and was verified empty, so whatever is in it now is
        // this attempt's debris — leaving it would block the retry with "directory is not empty".
        var missing = Path.Combine(_workspace, "no-such-repo");
        var target = Path.Combine(_workspace, "picked-by-hand");
        Directory.CreateDirectory(target);

        var result = await _service.CloneAsync(missing, target);

        result.Success.Should().BeFalse();
        Directory.Exists(target).Should().BeTrue();
        Directory.EnumerateFileSystemEntries(target).Should().BeEmpty();
    }

    [Fact]
    public async Task CloneAsync_AlreadyCancelled_NeverStarts()
    {
        // The token is only honoured inside the native transfer callback, and a local clone may
        // never reach it — so an already-cancelled token has to be caught before the call.
        using var source = new TempRepository("clone-cancel");
        var target = Path.Combine(_workspace, "cancelled");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await _service.CloneAsync(source.Path, target, progress: null, ct: cts.Token);

        result.Success.Should().BeFalse();
        result.Kind.Should().Be(GitFailureKind.Cancelled);
        Directory.Exists(target).Should().BeFalse();
    }

    [Fact]
    public async Task CloneAsync_NoTargetDirectory_IsRefused()
    {
        using var source = new TempRepository("clone-notarget");

        var result = await _service.CloneAsync(source.Path, "  ");

        result.Success.Should().BeFalse();
        result.Kind.Should().Be(GitFailureKind.NoTargetDirectory);
    }
}
