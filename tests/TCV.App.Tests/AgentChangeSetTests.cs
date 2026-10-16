using FluentAssertions;
using TCV.Ai.Agents;
using TCV.App.Agents;
using TCV.App.Operations;
using TCV.App.Services;
using TCV.Git;

namespace TCV.App.Tests;

/// <summary>
/// The app-side half of edit provenance: reducing our own operation log to facts the pure attributor
/// can join on. The interesting part is <see cref="AgentChangeSet.StripCount"/> — the log records a
/// batch as «&lt;directory&gt; (7)», and matching that literal string against a path would never hit.
/// </summary>
public class AgentChangeSetTests
{
    private static OperationLogEntry Entry(
        string source,
        string? destination = null,
        OperationOutcome outcome = OperationOutcome.Completed,
        string? origin = null,
        OperationKind kind = OperationKind.Delete,
        string? arrivedIn = null) =>
        new()
        {
            Timestamp = new DateTime(2026, 7, 27, 12, 0, 0, DateTimeKind.Local),
            Kind = kind,
            Source = source,
            Destination = destination,
            ItemCount = 1,
            Outcome = outcome,
            Origin = origin,
            ArrivedIn = arrivedIn,
        };

    // ── StripCount ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"D:\repo\src (7)", @"D:\repo\src")]
    [InlineData(@"D:\repo\src (1)", @"D:\repo\src")]
    [InlineData(@"D:\repo\src\a.cs", @"D:\repo\src\a.cs")]
    public void StripCount_RemovesTheBatchCounter(string source, string expected) =>
        AgentChangeSet.StripCount(source).Should().Be(expected);

    [Theory]
    [InlineData(@"D:\My Folder (copy)")]
    [InlineData(@"D:\Backup (old)")]
    [InlineData(@"D:\thing ()")]
    public void StripCount_LeavesARealDirectoryNameAlone(string source)
    {
        // Only a pure number in the brackets is a count.
        AgentChangeSet.StripCount(source).Should().Be(source);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void StripCount_NothingThere_IsEmpty(string? source) =>
        AgentChangeSet.StripCount(source).Should().BeEmpty();

    // ── ToWriteFacts ──────────────────────────────────────────────────────────

    [Fact]
    public void ToWriteFacts_SingleCopy_NamesTheCopiedFileNotItsFolder()
    {
        // The destination of a copy is the target folder; a one-file copy wrote exactly one file
        // there, and the folder must not claim the user's other edits in it. The source is only read.
        var facts = AgentChangeSet.ToWriteFacts([Entry(@"D:\a.cs", @"D:\sub", kind: OperationKind.Copy)]);

        facts.Should().ContainSingle();
        facts[0].Paths.Should().Equal(@"D:\sub\a.cs");
        facts[0].Directories.Should().BeEmpty();
    }

    [Fact]
    public void ToWriteFacts_SingleCopyUnderANewName_ClaimsTheNewFile()
    {
        // F5 to «D:\x\new.txt» logs the item itself as the destination; gluing the source's name
        // onto it claimed «D:\x\new.txt\old.txt», a path that does not exist.
        var facts = AgentChangeSet.ToWriteFacts(
            [Entry(@"D:\a\old.txt", @"D:\x\new.txt", kind: OperationKind.Copy, arrivedIn: @"D:\x")]);

        facts[0].Paths.Should().Equal(@"D:\x\new.txt");
        facts[0].Directories.Should().BeEmpty();
    }

    [Fact]
    public void ToWriteFacts_SingleCopyKeepingItsName_ClaimsTheFileInTheFolder()
    {
        var facts = AgentChangeSet.ToWriteFacts(
            [Entry(@"D:\a\old.txt", @"D:\x", kind: OperationKind.Copy, arrivedIn: @"D:\x")]);

        facts[0].Paths.Should().Equal(@"D:\x\old.txt");
    }

    [Fact]
    public void ToWriteFacts_SingleMoveUnderANewName_ClaimsTheNewFileAndTheRemovedSource()
    {
        var facts = AgentChangeSet.ToWriteFacts(
            [Entry(@"D:\a\old.txt", @"D:\new.txt", kind: OperationKind.Move, arrivedIn: @"D:\")]);

        facts[0].Paths.Should().Equal(@"D:\new.txt", @"D:\a\old.txt");
    }

    [Fact]
    public void ToWriteFacts_BatchCopy_ClaimsTheDestinationFolderOnly()
    {
        var facts = AgentChangeSet.ToWriteFacts([Entry(@"D:\src (3)", @"D:\sub", kind: OperationKind.Copy)]);

        facts[0].Paths.Should().BeEmpty();
        facts[0].Directories.Should().Equal(@"D:\sub");
    }

    [Fact]
    public void ToWriteFacts_BatchMove_ClaimsBothFolders()
    {
        var facts = AgentChangeSet.ToWriteFacts([Entry(@"D:\src (3)", @"D:\sub", kind: OperationKind.Move)]);

        facts[0].Directories.Should().Equal(@"D:\sub", @"D:\src");
    }

    [Fact]
    public void ToWriteFacts_SingleRename_NamesTheOldAndTheNewFile()
    {
        var facts = AgentChangeSet.ToWriteFacts([Entry(@"D:\dir\old.txt", "new.txt", kind: OperationKind.Rename)]);

        facts[0].Paths.Should().Equal(@"D:\dir\old.txt", @"D:\dir\new.txt");
    }

    [Fact]
    public void ToWriteFacts_DeleteToTheRecycleBin_DoesNotTreatTheTokenAsAPath()
    {
        var facts = AgentChangeSet.ToWriteFacts([Entry(@"D:\a.cs", OperationLogDescriptor.RecycleBinToken)]);

        facts[0].Paths.Should().Equal(@"D:\a.cs");
    }

    [Fact]
    public void ToWriteFacts_DropsFailedOperations()
    {
        // A failed operation wrote nothing, so letting it claim a change would attribute an edit to
        // something that never happened.
        var facts = AgentChangeSet.ToWriteFacts(
            [Entry(@"D:\a.cs", outcome: OperationOutcome.Failed)]);

        facts.Should().BeEmpty();
    }

    [Fact]
    public void ToWriteFacts_KeepsCancelledOnesBecauseTheyMayHaveWrittenSomething()
    {
        var facts = AgentChangeSet.ToWriteFacts(
            [Entry(@"D:\a.cs", outcome: OperationOutcome.Cancelled)]);

        facts.Should().ContainSingle();
    }

    [Fact]
    public void ToWriteFacts_CarriesTheAgentOriginTag()
    {
        var facts = AgentChangeSet.ToWriteFacts([Entry(@"D:\a.cs", origin: "claude-code")]);

        facts[0].Origin.Should().Be("claude-code");
    }

    [Fact]
    public void ToWriteFacts_NoOriginBecomesAnEmptyStringNotNull()
    {
        // The attributor distinguishes Manager from Supervised on Origin.Length, so null must not leak.
        AgentChangeSet.ToWriteFacts([Entry(@"D:\a.cs")])[0].Origin.Should().BeEmpty();
    }

    [Fact]
    public void ToWriteFacts_ConvertsLocalTimestampsToUtc()
    {
        var facts = AgentChangeSet.ToWriteFacts([Entry(@"D:\a.cs")]);

        facts[0].AtUtc.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void ToWriteFacts_EntryWithNoPaths_IsSkipped()
    {
        var facts = AgentChangeSet.ToWriteFacts([Entry(string.Empty)]);

        facts.Should().BeEmpty();
    }

    // ── Build ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_OutsideARepository_IsEmpty()
    {
        var rows = AgentChangeSet.Build(GitWorkingTreeStatus.Empty, [], [], DateTime.UtcNow);

        rows.Should().BeEmpty();
    }

    [Fact]
    public void Build_ProducesOneRowPerGitEntryIncludingTheStagedTwin()
    {
        // A file modified after being staged legitimately produces two rows — that is what git will
        // and will not commit, and collapsing them hides it.
        var status = new GitWorkingTreeStatus(@"D:\repo", "main",
        [
            new GitChangedFile("src/a.cs", GitFileStatus.Modified, IsStaged: true),
            new GitChangedFile("src/a.cs", GitFileStatus.Modified, IsStaged: false),
        ]);

        var rows = AgentChangeSet.Build(status, [], [], DateTime.UtcNow);

        rows.Should().HaveCount(2);
        rows.Should().Contain(r => r.IsStaged).And.Contain(r => !r.IsStaged);
        rows.Should().OnlyContain(r => r.RelativePath == "src/a.cs");
    }

    [Fact]
    public void Build_FillsTheFullPathFromTheRepositoryRoot()
    {
        var status = new GitWorkingTreeStatus(@"D:\repo", "main",
            [new GitChangedFile("src/a.cs", GitFileStatus.Modified, false)]);

        var rows = AgentChangeSet.Build(status, [], [], DateTime.UtcNow);

        rows[0].FullPath.Should().Be(@"D:\repo\src\a.cs");
    }

    [Fact]
    public void Build_MissingFilesAreAttributedExternalRatherThanFailing()
    {
        // Nothing under D:\repo exists in a unit test, so every mtime lookup comes back null.
        var status = new GitWorkingTreeStatus(@"D:\repo", "main",
            [new GitChangedFile("src/a.cs", GitFileStatus.Modified, false)]);

        var rows = AgentChangeSet.Build(status, [], [], DateTime.UtcNow);

        rows[0].Origin.Should().Be(ChangeOrigin.External);
        rows[0].AtUtc.Should().BeNull();
    }

    [Fact]
    public void Build_LoggedDeleteOfAMissingFile_IsAttributedToTheManager()
    {
        var status = new GitWorkingTreeStatus(@"D:\repo", "main",
            [new GitChangedFile("src/gone.cs", GitFileStatus.Deleted, false)]);

        var entry = Entry(@"D:\repo\src\gone.cs");

        // «Now» is taken from the entry: a fixed date against DateTime.UtcNow ages out of the
        // deletion lookback window and turns the test red without any code change.
        var rows = AgentChangeSet.Build(
            status, [entry], [], entry.Timestamp.ToUniversalTime().AddMinutes(1));

        rows[0].Origin.Should().Be(ChangeOrigin.Manager);
    }

    [Theory]
    [InlineData(GitFileStatus.Untracked, true)]
    [InlineData(GitFileStatus.Added, true)]
    [InlineData(GitFileStatus.Modified, false)]
    [InlineData(GitFileStatus.Deleted, false)]
    public void RevertMeansDelete_OnlyForFilesWithNothingInHead(GitFileStatus status, bool expected)
    {
        var row = new AgentChangeRow(
            "a.cs", @"D:\repo\a.cs", status, "?", false, ChangeOrigin.External, string.Empty, null);

        row.RevertMeansDelete.Should().Be(expected);
    }

    [Theory]
    [InlineData(GitFileStatus.Added, true)]
    [InlineData(GitFileStatus.Untracked, false)]
    [InlineData(GitFileStatus.Modified, false)]
    public void RevertUnstagesFirst_OnlyForAStagedNewFile(GitFileStatus status, bool expected)
    {
        // Deleting a staged-new file from disk alone left it in the index, and the next commit
        // still took the agent's version.
        var row = new AgentChangeRow(
            "a.cs", @"D:\repo\a.cs", status, "A", true, ChangeOrigin.External, string.Empty, null);

        row.RevertUnstagesFirst.Should().Be(expected);
    }

    [Fact]
    public void RestorePaths_RenamedRow_RestoresTheOldNameToo()
    {
        var row = new AgentChangeRow(
            "new.cs", @"D:\repo\new.cs", GitFileStatus.Renamed, "R", true,
            ChangeOrigin.External, string.Empty, null, OldRelativePath: "old.cs");

        row.RestorePaths.Should().Equal("new.cs", "old.cs");
    }

    [Fact]
    public void RestorePaths_ModifiedRow_IsJustTheFile()
    {
        var row = new AgentChangeRow(
            "a.cs", @"D:\repo\a.cs", GitFileStatus.Modified, "M", false, ChangeOrigin.External, string.Empty, null);

        row.RestorePaths.Should().Equal("a.cs");
    }
}
