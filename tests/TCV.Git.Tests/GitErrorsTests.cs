using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;

namespace TCV.Git.Tests;

/// <summary>
/// ROADMAP 6.53: «run an agent on a task» showed git's failure as a raw English line. Every way
/// creating a worktree can fail now carries a <see cref="GitFailureKind"/> the UI turns into a
/// sentence in the user's language — including the ones libgit2 throws.
/// </summary>
public class GitErrorsTests
{
    private readonly GitWorktreeService _service = new(NullLogger<GitWorktreeService>.Instance);

    [Fact]
    public void FromException_LockFileInTheWay_IsRepositoryLocked()
    {
        var result = GitErrors.FromException(new LockedFileException("failed to lock file 'index.lock'"));

        result.Success.Should().BeFalse();
        result.Kind.Should().Be(GitFailureKind.RepositoryLocked);
    }

    [Fact]
    public void FromException_CheckoutConflict_IsCheckoutConflict()
    {
        GitErrors.FromException(new CheckoutConflictException("1 conflict prevents checkout"))
            .Kind.Should().Be(GitFailureKind.CheckoutConflict);
    }

    [Fact]
    public void FromException_UnbornBranch_IsNoCommits()
    {
        GitErrors.FromException(new UnbornBranchException("reference 'refs/heads/main' not found"))
            .Kind.Should().Be(GitFailureKind.NoCommits);
    }

    [Fact]
    public void FromException_RepositoryNotFound_NamesThePath()
    {
        var result = GitErrors.FromException(new RepositoryNotFoundException("not found"), @"D:\somewhere");

        result.Kind.Should().Be(GitFailureKind.NotARepository);
        result.Detail.Should().Be(@"D:\somewhere");
    }

    [Fact]
    public void FromException_AnythingElse_IsUnexpectedAndKeepsTheRawTextAsTheDetail()
    {
        var result = GitErrors.FromException(new IOException("The process cannot access the file"));

        result.Kind.Should().Be(GitFailureKind.Unexpected);
        result.Detail.Should().Be("The process cannot access the file");
        result.Message.Should().Be("The process cannot access the file");
    }

    [Fact]
    public async Task AddAsync_FailingInsideLibgit2_IsClassifiedNotPassedThrough()
    {
        using var temp = new TempRepository("wt-err-native");

        // A name Windows cannot hold gets past our own checks and fails inside libgit2.
        var result = await _service.AddAsync(new GitWorktreeRequest(
            temp.Path, "native-fail", CreateBranch: true, BaseCommittish: "",
            TargetPath: temp.SiblingPath("bad|name")));

        result.Success.Should().BeFalse();
        result.Kind.Should().NotBe(GitFailureKind.None);
        result.Detail.Should().NotBeEmpty();
    }

    [Fact]
    public async Task AddAsync_BranchHeldByAnotherCheckout_NamesThatCheckout()
    {
        using var repo = new TempRepository("wt-err-held");

        var result = await _service.AddAsync(new GitWorktreeRequest(
            repo.Path, repo.InitialBranch, CreateBranch: false, BaseCommittish: "",
            TargetPath: repo.SiblingPath("held")));

        result.Kind.Should().Be(GitFailureKind.CheckedOutElsewhere);
        result.Detail.TrimEnd('\\').Should().BeEquivalentTo(Path.GetFullPath(repo.Path).TrimEnd('\\'));
    }

    [Fact]
    public async Task AddAsync_UnresolvableBase_IsNoSuchRevision()
    {
        using var repo = new TempRepository("wt-err-base");

        var result = await _service.AddAsync(new GitWorktreeRequest(
            repo.Path, "from-nowhere", CreateBranch: true, BaseCommittish: "no-such-ref",
            TargetPath: repo.SiblingPath("from-nowhere")));

        result.Kind.Should().Be(GitFailureKind.NoSuchRevision);
        result.Detail.Should().Be("no-such-ref");
    }

    [Fact]
    public async Task AddAsync_RepositoryWithoutCommits_SaysSoInsteadOfBlamingAnEmptyStartPoint()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tcv_wt_unborn_{Guid.NewGuid():N}");
        Repository.Init(dir);
        try
        {
            var result = await _service.AddAsync(new GitWorktreeRequest(
                dir, "first", CreateBranch: true, BaseCommittish: "", TargetPath: dir + "-first"));

            result.Success.Should().BeFalse();
            result.Kind.Should().Be(GitFailureKind.NoCommits);
            Directory.Exists(dir + "-first").Should().BeFalse();
        }
        finally
        {
            TempRepository.DeleteTree(dir);
        }
    }

    [Fact]
    public async Task AddAsync_BranchNameCollidingWithAnExistingBranch_IsBranchNameConflict()
    {
        using var temp = new TempRepository("wt-err-dfconflict");
        using (var repo = new Repository(temp.Path))
        {
            repo.Branches.Add("feature", repo.Head.Tip);
        }

        // refs/heads/feature is a file, so refs/heads/feature/login cannot be created under it.
        var result = await _service.AddAsync(new GitWorktreeRequest(
            temp.Path, "feature/login", CreateBranch: true, BaseCommittish: "",
            TargetPath: temp.SiblingPath("feature-login")));

        result.Success.Should().BeFalse();
        result.Kind.Should().Be(GitFailureKind.BranchNameConflict, result.Message);
        result.Detail.Should().Be("feature", "the message names the branch in the way");
        Directory.Exists(temp.SiblingPath("feature-login")).Should().BeFalse();
    }

    [Theory]
    [InlineData("feature/login", "feature")]
    [InlineData("feature", "feature/login")]
    [InlineData("Feature/login", "feature")]
    public void FindPathConflict_BranchAndFolderOfBranches_Collide(string branch, string existing)
    {
        GitRefName.FindPathConflict(branch, ["main", existing]).Should().Be(existing);
    }

    [Theory]
    [InlineData("feature-login")]
    [InlineData("featurex/login")]
    [InlineData("feat")]
    [InlineData("")]
    public void FindPathConflict_UnrelatedNames_DoNotCollide(string branch)
    {
        GitRefName.FindPathConflict(branch, ["main", "feature"]).Should().BeNull();
    }

    [Fact]
    public async Task AddAsync_DriveRootAsTarget_IsInvalidPath()
    {
        using var repo = new TempRepository("wt-err-root");

        // A drive that does not exist gets past the "not empty" check; a mounted one never would.
        var root = Enumerable.Range('G', 20).Select(c => $"{(char)c}:\\").FirstOrDefault(r => !Directory.Exists(r));
        if (root is null)
        {
            return;   // every letter is taken on this machine; nothing to probe with
        }

        var result = await _service.AddAsync(new GitWorktreeRequest(
            repo.Path, "rooted", CreateBranch: true, BaseCommittish: "", TargetPath: root));

        result.Success.Should().BeFalse();
        result.Kind.Should().Be(GitFailureKind.InvalidPath);
        result.Detail.Should().Be(root);
    }

    [Fact]
    public async Task AddAsync_NotARepository_IsClassified()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tcv_wt_nogit_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var result = await _service.AddAsync(new GitWorktreeRequest(
                dir, "x", CreateBranch: true, BaseCommittish: "", TargetPath: Path.Combine(dir, "wt")));

            result.Kind.Should().Be(GitFailureKind.NotARepository);
            result.Detail.Should().Be(dir);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
