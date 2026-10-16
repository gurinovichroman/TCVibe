using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TCV.Ai.Llm;
using TCV.App.Services;
using TCV.App.ViewModels.Dialogs;
using TCV.Git;
using Xunit;

namespace TCV.App.Tests;

/// <summary>
/// The git dialogs' guards against losing or misplacing work: one question before a draft replaces
/// typed text, a worktree removal that asks again when more changed since the listing, a clone that
/// lands where the panel is, and buttons that are only lit when they can act.
/// </summary>
public sealed class GitDialogSafetyTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), $"tcv_gitsafe_{Guid.NewGuid():N}");
    private readonly List<string> _cleanup = new();

    public GitDialogSafetyTests()
    {
        Directory.CreateDirectory(_repo);
        Repository.Init(_repo);
        using var repo = new Repository(_repo);
        repo.Config.Set("user.name", "TCV Test");
        repo.Config.Set("user.email", "test@example.invalid");
        repo.Config.Set("core.autocrlf", false);

        File.WriteAllText(Path.Combine(_repo, "README.md"), "hello\n");
        LibGit2Sharp.Commands.Stage(repo, "README.md");
        var who = new Signature("TCV Test", "test@example.invalid", DateTimeOffset.Now);
        repo.Commit("init", who, who);
        _cleanup.Add(_repo);
    }

    public void Dispose()
    {
        foreach (var path in _cleanup)
        {
            try
            {
                if (!Directory.Exists(path)) continue;
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(path, recursive: true);
            }
            catch
            {
                // A leaked temp directory is not worth failing an otherwise green test over.
            }
        }
    }

    private sealed class FixedReply(string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$$"""{"choices":[{"message":{"content":"{{{content}}}"}}]}""", Encoding.UTF8, "application/json"),
            });
    }

    private static LlmClient Llm(string endpoint = "http://localhost:11434/v1") =>
        new(() => new LlmOptions(endpoint, "key", "model", "embed"), handler: new FixedReply("Fix the readme"));

    // ── Commit dialog ──────────────────────────────────────────────────────────

    [Fact]
    public async Task DraftMessage_OverTypedText_AsksOnceAndReplacesOnYes()
    {
        File.WriteAllText(Path.Combine(_repo, "README.md"), "changed\n");
        var dialogs = new Mock<IDialogService>();
        dialogs.Setup(d => d.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        var vm = new GitCommitDialogViewModel(
            new GitWriteService(NullLogger<GitWriteService>.Instance), _repo, Llm(), dialogs.Object);
        await vm.RefreshAsync();
        vm.Message = "my own words";

        await vm.DraftMessageCommand.ExecuteAsync(null);

        vm.Message.Should().Be("Fix the readme");
        dialogs.Verify(d => d.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void DraftHint_RemoteEndpoint_NamesTheHost()
    {
        var vm = new GitCommitDialogViewModel(
            new GitWriteService(NullLogger<GitWriteService>.Instance), _repo, Llm("https://api.example.com/v1"));

        // Resources are not loaded under test: the key stands in for the sentence that names the host.
        vm.DraftHint.Should().Contain("Git_DraftSentTo");
    }

    [Fact]
    public void DraftHint_LocalEndpoint_NamesNoHost()
    {
        var vm = new GitCommitDialogViewModel(
            new GitWriteService(NullLogger<GitWriteService>.Instance), _repo, Llm());

        vm.DraftHint.Should().NotContain("Git_DraftSentTo");
    }

    [Fact]
    public void BranchDisplay_DetachedHead_SaysSoInsteadOfABareHash()
    {
        var vm = new GitCommitDialogViewModel(new GitWriteService(NullLogger<GitWriteService>.Instance), _repo)
        {
            Branch = "abc1234",
        };

        vm.BranchDisplay.Should().Contain("Git_TabDetachedTip");
        vm.Branch = "main";
        vm.BranchDisplay.Should().Be("main");
    }

    [Fact]
    public void SelectionButtons_NothingSelected_AreOff()
    {
        var vm = new GitCommitDialogViewModel(new GitWriteService(NullLogger<GitWriteService>.Instance), _repo);

        vm.CanActOnUnstagedSelection.Should().BeFalse();
        vm.CanActOnStagedSelection.Should().BeFalse();

        vm.HasUnstagedSelection = true;
        vm.CanActOnUnstagedSelection.Should().BeTrue();
    }

    // ── Worktree dashboard ────────────────────────────────────────────────────

    [Fact]
    public async Task RemoveWorktree_ChangedSinceTheListing_AsksAgainAndKeepsItOnNo()
    {
        var worktrees = new GitWorktreeService(NullLogger<GitWorktreeService>.Instance);
        var target = _repo + "-wt";
        _cleanup.Add(target);
        (await worktrees.AddAsync(new GitWorktreeRequest(_repo, "task", true, "", target))).Success.Should().BeTrue();

        var asked = new List<int>();
        var vm = new GitWorktreeDialogViewModel(worktrees, _repo, w =>
        {
            asked.Add(w.DirtyCount);
            return asked.Count == 1;   // yes to the stale "clean" prompt, no to the fresh one
        });
        await vm.RefreshAsync();
        vm.Selected = vm.Worktrees.Single(w => !w.IsMain);
        vm.Selected.DirtyCount.Should().Be(0);

        File.WriteAllText(Path.Combine(target, "agent-output.txt"), "work\n");
        await vm.RemoveSelectedCommand.ExecuteAsync(null);

        asked.Should().Equal(0, 1);
        Directory.Exists(target).Should().BeTrue();
        File.Exists(Path.Combine(target, "agent-output.txt")).Should().BeTrue();
        vm.RemovedAny.Should().BeFalse();
    }

    [Fact]
    public void CanRemoveSelected_WhileARemovalRuns_IsOff()
    {
        var vm = new GitWorktreeDialogViewModel(
            new GitWorktreeService(NullLogger<GitWorktreeService>.Instance), _repo, _ => true)
        {
            Selected = new GitWorktreeInfo("n", @"D:\x", "b", IsMain: false, IsCurrent: false, IsLocked: false,
                Missing: false, DirtyCount: 0, AheadBy: 0, BehindBy: 0, HasUpstream: false),
        };

        vm.IsBusy = true;

        vm.CanRemoveSelected.Should().BeFalse();
    }

    // ── Clone dialog ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("repo", @"D:\work\repo")]
    [InlineData(@"sub\repo", @"D:\work\sub\repo")]
    [InlineData(@"E:\elsewhere\repo", @"E:\elsewhere\repo")]
    [InlineData(@"\repo", "")]
    [InlineData("C:repo", "")]
    [InlineData("   ", "")]
    public void ResolveTarget_RelativeGoesUnderThePanelFolder(string typed, string expected)
    {
        GitCloneDialogViewModel.ResolveTarget(@"D:\work", typed).Should().Be(expected);
    }
}
