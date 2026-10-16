using System.IO;
using FluentAssertions;
using TCV.App.Workspaces;

namespace TCV.App.Tests;

public sealed class WorkspaceNotesStoreTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"tcv-notes-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private WorkspaceNotesStore Store() => new(_dir);

    [Fact]
    public void MissingNotes_ReadEmpty()
    {
        Store().Read("work").Should().BeEmpty();
        Store().HasNotes("work").Should().BeFalse();
    }

    [Fact]
    public void Write_Then_Read_RoundTrips()
    {
        var store = Store();
        store.Write("work", "# Task\n- fix the parser\n");

        store.Read("work").Should().Be("# Task\n- fix the parser\n");
        store.HasNotes("work").Should().BeTrue();
    }

    [Fact]
    public void Write_CreatesDirectoryOnDemand()
    {
        Directory.Exists(_dir).Should().BeFalse();

        Store().Write("work", "note");

        File.Exists(Store().PathFor("work")).Should().BeTrue();
    }

    [Fact]
    public void Write_Unicode_SurvivesRoundTrip()
    {
        var store = Store();
        store.Write("проект", "Заметка — с тире и «кавычками»");

        store.Read("проект").Should().Be("Заметка — с тире и «кавычками»");
    }

    [Fact]
    public void BlankNotes_DeleteTheFile_RatherThanLeavingItEmpty()
    {
        var store = Store();
        store.Write("work", "something");
        store.Write("work", "   \r\n  ");

        File.Exists(store.PathFor("work")).Should().BeFalse();
        store.HasNotes("work").Should().BeFalse();
    }

    [Fact]
    public void Delete_RemovesNotes_AndIsSafeWhenThereAreNone()
    {
        var store = Store();
        store.Write("work", "something");

        store.Delete("work");
        store.Delete("work");   // second call must not throw

        store.Read("work").Should().BeEmpty();
    }

    [Fact]
    public void NameKey_IsCaseInsensitive_LikeTheWorkspaceStore()
    {
        // WorkspaceStore treats "Work" and "work" as one workspace — the notes must follow, or a
        // rename that only changes case would strand them.
        var store = Store();
        store.Write("Work", "note");

        store.Read("work").Should().Be("note");
        store.Read("WORK").Should().Be("note");
    }

    [Fact]
    public void NamesThatSlugAlike_GetSeparateFiles()
    {
        // Both slug to "api-v1"; without the hash suffix they would silently share one file.
        WorkspaceNotesStore.FileNameFor("api/v1")
            .Should().NotBe(WorkspaceNotesStore.FileNameFor("api:v1"));

        var store = Store();
        store.Write("api/v1", "slash");
        store.Write("api:v1", "colon");

        store.Read("api/v1").Should().Be("slash");
        store.Read("api:v1").Should().Be("colon");
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("PRN")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("LPT1")]
    public void ReservedDeviceNames_StillProduceAUsableFile(string name)
    {
        // "CON.md" is unopenable on Windows; the hash suffix is what makes the base name ordinary.
        var store = Store();
        store.Write(name, "note");

        store.Read(name).Should().Be("note");
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData("a*b?")]
    [InlineData("  spaced  name  ")]
    [InlineData("...")]
    [InlineData("")]
    public void AwkwardNames_ProduceValidFileNames(string name)
    {
        var fileName = WorkspaceNotesStore.FileNameFor(name);

        fileName.Should().EndWith(".md");
        fileName.IndexOfAny(Path.GetInvalidFileNameChars()).Should().Be(-1);
        // A name of only punctuation still needs somewhere to live.
        fileName.Should().NotStartWith("-").And.NotBe(".md");
    }

    [Fact]
    public void UnicodeNames_KeepAReadableSlug()
    {
        // A Russian-first product must not turn every workspace into "workspace-<hash>.md".
        WorkspaceNotesStore.FileNameFor("Мой проект").Should().StartWith("мой-проект-");
    }

    [Fact]
    public void LongNames_AreTruncated_ToAWorkableFileName()
    {
        var fileName = WorkspaceNotesStore.FileNameFor(new string('x', 300));

        fileName.Length.Should().BeLessThan(60);
        fileName.Should().EndWith(".md");
    }

    [Fact]
    public void FileNameFor_IsStableAcrossCalls()
    {
        // The hash has to survive a restart, so it cannot come from string.GetHashCode().
        WorkspaceNotesStore.FileNameFor("work")
            .Should().Be(WorkspaceNotesStore.FileNameFor("work"));
    }

    [Fact]
    public void SeparateWorkspaces_DoNotShareNotes()
    {
        var store = Store();
        store.Write("alpha", "A");
        store.Write("beta", "B");

        store.Read("alpha").Should().Be("A");
        store.Read("beta").Should().Be("B");
    }
}
