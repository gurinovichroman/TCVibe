using System.IO;
using FluentAssertions;
using TCV.App.Operations;
using TCV.App.Services;

namespace TCV.App.Tests;

/// <summary>Covers the saved operations and their macros, added in task 6.22.</summary>
public class SavedOperationTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "tcv-savedop-" + Guid.NewGuid().ToString("N"));

    public SavedOperationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static OperationMacros.Context Context() => new(
        ActivePath: @"D:\repo\src",
        OppositePath: @"E:\backup",
        FocusedName: "Program",
        FocusedExtension: "cs",
        Now: new DateTime(2026, 7, 27, 14, 5, 0));

    // ── Macros ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("%P", @"D:\repo\src")]
    [InlineData("%O", @"E:\backup")]
    [InlineData("%N", "Program")]
    [InlineData("%E", "cs")]
    [InlineData("%Y-%M-%D", "2026-07-27")]
    [InlineData("%H:%m", "14:05")]
    public void Expand_ReplacesEachMacro(string template, string expected) =>
        OperationMacros.Expand(template, Context()).Should().Be(expected);

    [Fact]
    public void Expand_CombinesMacrosWithLiteralText()
    {
        OperationMacros.Expand(@"%O\%Y-%M-%D\%N.%E", Context())
            .Should().Be(@"E:\backup\2026-07-27\Program.cs");
    }

    [Fact]
    public void Expand_DoublePercent_IsALiteralSign() =>
        OperationMacros.Expand("100%% done", Context()).Should().Be("100% done");

    [Fact]
    public void Expand_UnknownMacro_StaysVisible()
    {
        // Silently dropping it would produce a wrong directory with no clue why.
        OperationMacros.Expand("%Z-suffix", Context()).Should().Be("%Z-suffix");
    }

    [Fact]
    public void Expand_TrailingPercent_DoesNotThrow()
    {
        var act = () => OperationMacros.Expand("path%", Context());
        act.Should().NotThrow();
        OperationMacros.Expand("path%", Context()).Should().Be("path%");
    }

    [Fact]
    public void Expand_NullOrEmpty_IsEmpty()
    {
        OperationMacros.Expand(null, Context()).Should().BeEmpty();
        OperationMacros.Expand("", Context()).Should().BeEmpty();
    }

    [Fact]
    public void Expand_NoMacros_IsUnchanged() =>
        OperationMacros.Expand(@"E:\fixed\path", Context()).Should().Be(@"E:\fixed\path");

    // ── Model ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SavedOperationKind.Move, true)]
    [InlineData(SavedOperationKind.Delete, true)]
    [InlineData(SavedOperationKind.Copy, false)]
    [InlineData(SavedOperationKind.CreateDirectory, false)]
    public void IsDestructive_MarksTheKindsThatLoseData(SavedOperationKind kind, bool expected) =>
        new SavedOperation { Kind = kind }.IsDestructive.Should().Be(expected);

    // ── Replaying a log line (6.22) ───────────────────────────────────────────

    [Fact]
    public void FromLogEntry_Copy_BecomesAReplayableOperation()
    {
        var operation = SavedOperation.FromLogEntry(
            OperationKind.Copy, @"D:\src\notes.md", @"E:\backup", "repeat");

        operation.Should().NotBeNull();
        operation!.Kind.Should().Be(SavedOperationKind.Copy);
        operation.SourceMask.Should().Be("*.md");
        operation.Target.Should().Be(@"E:\backup");
        operation.Confirm.Should().BeTrue();
    }

    [Fact]
    public void FromLogEntry_Move_KeepsTheKind() =>
        SavedOperation.FromLogEntry(OperationKind.Move, @"D:\a.txt", @"E:\b", "x")!
            .Kind.Should().Be(SavedOperationKind.Move);

    [Fact]
    public void FromLogEntry_Delete_IsRefused()
    {
        // The log records a summary, not a file list; guessing which files a delete meant would
        // destroy the wrong ones.
        SavedOperation.FromLogEntry(OperationKind.Delete, @"D:\a.txt", null, "x").Should().BeNull();
    }

    [Fact]
    public void FromLogEntry_NoDestination_TargetsTheOppositePanel() =>
        SavedOperation.FromLogEntry(OperationKind.Copy, @"D:\a.txt", null, "x")!
            .Target.Should().Be("%O");

    [Theory]
    [InlineData(@"D:\proj\new", "new")]
    [InlineData("/D:/proj/build/", "build")]
    public void FromLogEntry_NewFolder_RecreatesTheFolderByNameWhereYouAre(string logged, string expected)
    {
        // "%O" would name the opposite panel's own folder, which always exists: the replay did nothing.
        var op = SavedOperation.FromLogEntry(OperationKind.CreateFolder, logged, null, "x")!;

        op.Kind.Should().Be(SavedOperationKind.CreateDirectory);
        op.Target.Should().Be(expected, "a relative target is taken from the active panel's folder");
    }

    [Theory]
    [InlineData(@"D:\src\notes.md", "*.md")]
    [InlineData("D:/src/notes.md", "*.md")]
    [InlineData(@"D:\src\LICENSE", "")]           // no extension to derive a mask from
    [InlineData(@"D:\src (12)", "")]              // a batch: the log kept the directory and a count
    [InlineData("", "")]
    public void MaskFromLoggedSource_DerivesWhatItCan(string source, string expected) =>
        SavedOperation.MaskFromLoggedSource(source).Should().Be(expected);

    // ── Store ─────────────────────────────────────────────────────────────────

    private SavedOperationStore Store() =>
        new(Path.Combine(_dir, "saved-operations.json"));

    [Fact]
    public void LoadAll_NoFile_IsEmpty() => Store().LoadAll().Should().BeEmpty();

    [Fact]
    public void Save_Then_LoadAll_RoundTripsEveryField()
    {
        var store = Store();
        store.Save(new SavedOperation
        {
            Name = "archive logs",
            Kind = SavedOperationKind.Move,
            SourceMask = "*.log",
            Target = @"%O\%Y",
            CreateTarget = true,
            Confirm = false,
        });

        var restored = store.LoadAll().Single();
        restored.Name.Should().Be("archive logs");
        restored.Kind.Should().Be(SavedOperationKind.Move);
        restored.SourceMask.Should().Be("*.log");
        restored.Target.Should().Be(@"%O\%Y");
        restored.Confirm.Should().BeFalse();
    }

    [Fact]
    public void Save_SameName_Replaces()
    {
        var store = Store();
        store.Save(new SavedOperation { Name = "job", SourceMask = "*.txt" });
        store.Save(new SavedOperation { Name = "JOB", SourceMask = "*.md" });

        store.LoadAll().Should().ContainSingle().Which.SourceMask.Should().Be("*.md");
    }

    [Fact]
    public void Save_BlankName_IsRejected()
    {
        var act = () => Store().Save(new SavedOperation { Name = "  " });
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Delete_RemovesByNameCaseInsensitively()
    {
        var store = Store();
        store.Save(new SavedOperation { Name = "job" });

        store.Delete("JOB").Should().BeTrue();
        store.LoadAll().Should().BeEmpty();
        store.Delete("job").Should().BeFalse();
    }

    [Fact]
    public void LoadAll_OrdersMostRecentlyUsedFirst()
    {
        var store = Store();
        store.Save(new SavedOperation { Name = "old", LastUsedUtc = new DateTime(2026, 1, 1) });
        store.Save(new SavedOperation { Name = "recent", LastUsedUtc = new DateTime(2026, 7, 1) });

        store.LoadAll().First().Name.Should().Be("recent");
    }

    [Fact]
    public void Touch_MovesAnOperationToTheTop()
    {
        var store = Store();
        store.Save(new SavedOperation { Name = "a", LastUsedUtc = new DateTime(2026, 7, 1) });
        store.Save(new SavedOperation { Name = "b", LastUsedUtc = new DateTime(2026, 1, 1) });

        store.Touch("b", new DateTime(2026, 12, 1)).Should().BeTrue();

        store.LoadAll().First().Name.Should().Be("b");
        store.Touch("missing", DateTime.UtcNow).Should().BeFalse();
    }
}
