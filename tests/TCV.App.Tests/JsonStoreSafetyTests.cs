using System.IO;
using FluentAssertions;
using TCV.App.Navigation;
using TCV.App.Operations;
using TCV.App.Services;
using TCV.Search;

namespace TCV.App.Tests;

/// <summary>
/// The portable JSON stores must not turn a bad moment into lost data: a corrupt file is copied
/// aside before it is replaced, a locked file is never overwritten from an empty list, and deleting
/// every preset does not bring the built-ins back.
/// </summary>
public sealed class JsonStoreSafetyTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "tcv-jsonstore-" + Guid.NewGuid().ToString("N"));

    public JsonStoreSafetyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private string[] Backups(string name) => Directory.GetFiles(_dir, name + ".corrupt-*.bak");

    // ── Search presets ────────────────────────────────────────────────────────

    [Fact]
    public void SearchPresets_NoFile_OffersTheBuiltIns()
    {
        new SearchPresetStore(P("p.json")).LoadAll().Should().BeEquivalentTo(SearchPreset.Builtin);
    }

    [Fact]
    public void SearchPresets_DeletingEveryPreset_DoesNotResurrectTheBuiltIns()
    {
        var store = new SearchPresetStore(P("p.json"));
        foreach (var preset in store.LoadAll().ToList())
        {
            store.Delete(preset.Name).Should().BeTrue();
        }

        store.LoadAll().Should().BeEmpty("an empty list on disk means the user deleted them all");
    }

    [Fact]
    public void SearchPresets_SavingIntoAFreshFile_KeepsTheBuiltIns()
    {
        var store = new SearchPresetStore(P("p.json"));

        store.Save(new SearchPreset { Name = "mine" }).Should().BeTrue();

        store.LoadAll().Select(p => p.Name).Should().Contain("mine").And.Contain("TODO / FIXME");
    }

    [Fact]
    public void SearchPresets_CorruptFile_IsCopiedAsideBeforeTheNextSave()
    {
        File.WriteAllText(P("p.json"), "{ not json");
        var store = new SearchPresetStore(P("p.json"));

        store.Save(new SearchPreset { Name = "mine" }).Should().BeTrue();

        Backups("p.json").Should().ContainSingle();
        File.ReadAllText(Backups("p.json")[0]).Should().Be("{ not json");
    }

    [Fact]
    public void SearchPresets_LockedFile_IsNotOverwritten()
    {
        var store = new SearchPresetStore(P("p.json"));
        store.Save(new SearchPreset { Name = "keep" }).Should().BeTrue();
        var before = File.ReadAllText(P("p.json"));

        using (new FileStream(P("p.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            store.Save(new SearchPreset { Name = "other" }).Should().BeFalse();
        }

        File.ReadAllText(P("p.json")).Should().Be(before);
    }

    // ── Saved operations ──────────────────────────────────────────────────────

    [Fact]
    public void SavedOperations_CorruptFile_IsCopiedAsideThenReplaced()
    {
        File.WriteAllText(P("ops.json"), "[{ broken");
        var store = new SavedOperationStore(P("ops.json"));
        store.LoadAll().Should().BeEmpty();

        store.Save(new SavedOperation { Name = "nightly" }).Should().BeTrue();

        Backups("ops.json").Should().ContainSingle();
        store.LoadAll().Should().ContainSingle(o => o.Name == "nightly");
    }

    [Fact]
    public void SavedOperations_LockedFile_FailsTheSaveInsteadOfWipingIt()
    {
        var store = new SavedOperationStore(P("ops.json"));
        store.Save(new SavedOperation { Name = "one" }).Should().BeTrue();

        using (new FileStream(P("ops.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            store.Save(new SavedOperation { Name = "two" }).Should().BeFalse();
            store.Touch("one", DateTime.UtcNow).Should().BeFalse();
            store.Delete("one").Should().BeFalse();
        }

        store.LoadAll().Should().ContainSingle(o => o.Name == "one");
    }

    // ── File usage history ────────────────────────────────────────────────────

    [Fact]
    public void FileUsage_CorruptFile_IsCopiedAsideThenRecordingResumes()
    {
        File.WriteAllText(P("usage.json"), "garbage");
        var store = new FileUsageStore(P("usage.json"));

        store.Record(@"D:\a.cs", DateTime.UtcNow);

        Backups("usage.json").Should().ContainSingle();
        store.LoadAll().Should().ContainSingle(e => e.Path == @"D:\a.cs");
    }

    [Fact]
    public void FileUsage_LockedFile_SkipsTheHintAndKeepsTheHistory()
    {
        var store = new FileUsageStore(P("usage.json"));
        store.Record(@"D:\a.cs", DateTime.UtcNow);

        using (new FileStream(P("usage.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            store.Record(@"D:\b.cs", DateTime.UtcNow);
        }

        store.LoadAll().Should().ContainSingle(e => e.Path == @"D:\a.cs");
    }

    // ── The helper itself ─────────────────────────────────────────────────────

    [Fact]
    public void WriteAtomic_ReplacesTheFileAndLeavesNoTempBehind()
    {
        File.WriteAllText(P("f.json"), "old");

        JsonListFile.WriteAtomic(P("f.json"), "new").Should().BeTrue();

        File.ReadAllText(P("f.json")).Should().Be("new");
        File.Exists(P("f.json.tmp")).Should().BeFalse();
    }

    [Fact]
    public void TryRead_ReportsMissingCorruptAndOk()
    {
        var options = new System.Text.Json.JsonSerializerOptions();

        JsonListFile.TryRead<string>(P("none.json"), options, out _).Should().Be(JsonFileState.Missing);

        File.WriteAllText(P("bad.json"), "[1,");
        JsonListFile.TryRead<string>(P("bad.json"), options, out _).Should().Be(JsonFileState.Corrupt);

        File.WriteAllText(P("ok.json"), "[\"a\",\"b\"]");
        JsonListFile.TryRead<string>(P("ok.json"), options, out var list).Should().Be(JsonFileState.Ok);
        list.Should().Equal("a", "b");
    }
}
