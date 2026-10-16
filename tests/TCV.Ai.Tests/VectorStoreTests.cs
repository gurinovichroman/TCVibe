using FluentAssertions;
using TCV.Ai.Semantic;

namespace TCV.Ai.Tests;

/// <summary>
/// The local vector index against a real SQLite file with the real <c>sqlite-vec</c> extension.
/// <para>
/// Not a pure-core test, and deliberately so: this is a wrapper over a native extension, and the same
/// reasoning that made <c>TCV.Git.Tests</c> build a real temporary repository applies here (ADR-0015).
/// The failure this catches is the expensive one — an index that silently stores nothing, or mixes
/// vectors of different widths and answers with quietly meaningless results.
/// </para>
/// </summary>
public sealed class VectorStoreTests : IDisposable
{
    private const int Dimensions = 4;

    private readonly string _temp;

    public VectorStoreTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "tcv-vec-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); }
        catch { /* the file may still be held by SQLite on a failed test */ }
    }

    private VectorStore NewStore(string name = "index.db") =>
        new(Path.Combine(_temp, name));

    /// <summary>
    /// Opens a store, or returns null when the native extension is not next to the test binaries —
    /// then the test reports success rather than failing for a reason unrelated to the code.
    /// </summary>
    private VectorStore? OpenOrSkip(string name = "index.db")
    {
        var store = NewStore(name);
        if (store.Open(Dimensions)) return store;

        store.Dispose();
        return null;
    }

    private static float[] Vector(params float[] values) => values;

    private static IReadOnlyList<TextChunk> Chunk(string text, int start = 1, int end = 3) =>
        [new TextChunk(0, start, end, text)];

    [Fact]
    public void Open_CreatesTheDatabaseAndReportsItsWidth()
    {
        using var store = OpenOrSkip();
        if (store is null) return;

        store.IsOpen.Should().BeTrue();
        store.Dimensions.Should().Be(Dimensions);
        store.UnavailableReason.Should().BeNull();
        File.Exists(store.DatabasePath).Should().BeTrue();
    }

    [Fact]
    public void Open_ZeroWidth_IsRefusedWithAReason()
    {
        using var store = NewStore();

        store.Open(0).Should().BeFalse();
        store.UnavailableReason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Replace_ThenSearch_FindsTheNearestChunk()
    {
        using var store = OpenOrSkip();
        if (store is null) return;

        var now = DateTime.UtcNow;
        store.Replace(@"D:\repo", @"D:\repo\a.cs", now, Chunk("alpha"), [Vector(1, 0, 0, 0)]);
        store.Replace(@"D:\repo", @"D:\repo\b.cs", now, Chunk("beta"), [Vector(0, 1, 0, 0)]);

        var hits = store.Search(Vector(0.9f, 0.1f, 0, 0), root: null, limit: 2);

        hits.Should().HaveCount(2);
        hits[0].Path.Should().EndWith("a.cs");
        hits[0].Text.Should().Be("alpha");
        hits[0].Score.Should().BeGreaterThan(hits[1].Score);
    }

    [Fact]
    public void Search_ReturnsTheChunkLineRange()
    {
        using var store = OpenOrSkip();
        if (store is null) return;

        store.Replace(@"D:\repo", @"D:\repo\a.cs", DateTime.UtcNow,
            [new TextChunk(0, 42, 57, "body")], [Vector(1, 0, 0, 0)]);

        var hit = store.Search(Vector(1, 0, 0, 0), null, 1).Single();

        hit.StartLine.Should().Be(42);
        hit.EndLine.Should().Be(57);
    }

    [Fact]
    public void Search_ScoreIsOneForAnIdenticalUnitVector()
    {
        using var store = OpenOrSkip();
        if (store is null) return;

        store.Replace(@"D:\repo", @"D:\repo\a.cs", DateTime.UtcNow, Chunk("alpha"), [Vector(1, 0, 0, 0)]);

        store.Search(Vector(1, 0, 0, 0), null, 1).Single().Score.Should().BeApproximately(1.0, 1e-4);
    }

    [Fact]
    public void Search_ScopedToARoot_ExcludesOtherRoots()
    {
        using var store = OpenOrSkip();
        if (store is null) return;

        var now = DateTime.UtcNow;
        store.Replace(@"D:\one", @"D:\one\a.cs", now, Chunk("alpha"), [Vector(1, 0, 0, 0)]);
        store.Replace(@"D:\two", @"D:\two\b.cs", now, Chunk("alpha too"), [Vector(1, 0, 0, 0)]);

        var hits = store.Search(Vector(1, 0, 0, 0), @"D:\one", 10);

        hits.Should().ContainSingle();
        hits[0].Path.Should().Contain("one");
    }

    [Fact]
    public void Search_ScopeMatchesOnWholeSegments()
    {
        using var store = OpenOrSkip();
        if (store is null) return;

        store.Replace(@"D:\repo-old", @"D:\repo-old\a.cs", DateTime.UtcNow,
            Chunk("alpha"), [Vector(1, 0, 0, 0)]);

        store.Search(Vector(1, 0, 0, 0), @"D:\repo", 10).Should().BeEmpty();
    }

    [Fact]
    public void Replace_RunTwice_DoesNotDuplicateTheFile()
    {
        using var store = OpenOrSkip();
        if (store is null) return;

        var now = DateTime.UtcNow;
        store.Replace(@"D:\repo", @"D:\repo\a.cs", now, Chunk("first"), [Vector(1, 0, 0, 0)]);
        store.Replace(@"D:\repo", @"D:\repo\a.cs", now, Chunk("second"), [Vector(0, 1, 0, 0)]);

        store.FileCount().Should().Be(1);
        store.ChunkCount().Should().Be(1);
        store.Search(Vector(0, 1, 0, 0), null, 1).Single().Text.Should().Be("second");
    }

    [Fact]
    public void Remove_DropsBothTheRowsAndTheVectors()
    {
        using var store = OpenOrSkip();
        if (store is null) return;

        store.Replace(@"D:\repo", @"D:\repo\a.cs", DateTime.UtcNow, Chunk("alpha"), [Vector(1, 0, 0, 0)]);
        store.Remove(@"D:\repo\a.cs");

        store.ChunkCount().Should().Be(0);
        // An orphaned vector would keep matching and point at a chunk row that no longer exists.
        store.Search(Vector(1, 0, 0, 0), null, 10).Should().BeEmpty();
    }

    [Fact]
    public void IsCurrent_TracksTheModificationTime()
    {
        using var store = OpenOrSkip();
        if (store is null) return;

        var now = DateTime.UtcNow;
        store.Replace(@"D:\repo", @"D:\repo\a.cs", now, Chunk("alpha"), [Vector(1, 0, 0, 0)]);

        store.IsCurrent(@"D:\repo\a.cs", now).Should().BeTrue();
        store.IsCurrent(@"D:\repo\a.cs", now.AddSeconds(1)).Should().BeFalse();
        store.IsCurrent(@"D:\repo\never.cs", now).Should().BeFalse();
    }

    [Fact]
    public void IsCurrent_MatchesRegardlessOfSlashDirection()
    {
        using var store = OpenOrSkip();
        if (store is null) return;

        var now = DateTime.UtcNow;
        store.Replace(@"D:\repo", @"D:\repo\a.cs", now, Chunk("alpha"), [Vector(1, 0, 0, 0)]);

        store.IsCurrent("D:/repo/a.cs", now).Should().BeTrue();
    }

    [Fact]
    public void Replace_WithNoChunks_RemovesTheFile()
    {
        using var store = OpenOrSkip();
        if (store is null) return;

        var now = DateTime.UtcNow;
        store.Replace(@"D:\repo", @"D:\repo\a.cs", now, Chunk("alpha"), [Vector(1, 0, 0, 0)]);
        store.Replace(@"D:\repo", @"D:\repo\a.cs", now, [], []);

        store.ChunkCount().Should().Be(0);
    }

    [Fact]
    public void Replace_SkipsAVectorOfTheWrongWidth()
    {
        using var store = OpenOrSkip();
        if (store is null) return;

        store.Replace(@"D:\repo", @"D:\repo\a.cs", DateTime.UtcNow, Chunk("alpha"), [Vector(1, 0)]);

        store.ChunkCount().Should().Be(0);
    }

    [Fact]
    public void Search_WithAWrongWidthQuery_IsEmptyRatherThanWrong()
    {
        using var store = OpenOrSkip();
        if (store is null) return;

        store.Replace(@"D:\repo", @"D:\repo\a.cs", DateTime.UtcNow, Chunk("alpha"), [Vector(1, 0, 0, 0)]);

        store.Search(Vector(1, 0), null, 10).Should().BeEmpty();
    }

    [Fact]
    public void Open_WithADifferentWidth_RebuildsRatherThanMixingVectors()
    {
        // Mixing widths does not error, it quietly produces meaningless results — so the index is
        // dropped instead.
        var path = Path.Combine(_temp, "rebuild.db");

        using (var first = new VectorStore(path))
        {
            if (!first.Open(Dimensions)) return;
            first.Replace(@"D:\repo", @"D:\repo\a.cs", DateTime.UtcNow, Chunk("alpha"), [Vector(1, 0, 0, 0)]);
            first.ChunkCount().Should().Be(1);
        }

        using var second = new VectorStore(path);
        second.Open(8).Should().BeTrue();
        second.Dimensions.Should().Be(8);
        second.ChunkCount().Should().Be(0);
    }

    [Fact]
    public void Open_WithTheSameWidthAgain_KeepsWhatWasIndexed()
    {
        var path = Path.Combine(_temp, "persist.db");

        using (var first = new VectorStore(path))
        {
            if (!first.Open(Dimensions)) return;
            first.Replace(@"D:\repo", @"D:\repo\a.cs", DateTime.UtcNow, Chunk("alpha"), [Vector(1, 0, 0, 0)]);
        }

        using var second = new VectorStore(path);
        second.Open(Dimensions).Should().BeTrue();
        second.ChunkCount().Should().Be(1);
    }

    [Fact]
    public void Clear_EmptiesTheIndexButKeepsItUsable()
    {
        using var store = OpenOrSkip();
        if (store is null) return;

        store.Replace(@"D:\repo", @"D:\repo\a.cs", DateTime.UtcNow, Chunk("alpha"), [Vector(1, 0, 0, 0)]);
        store.Clear();

        store.ChunkCount().Should().Be(0);

        store.Replace(@"D:\repo", @"D:\repo\b.cs", DateTime.UtcNow, Chunk("beta"), [Vector(0, 1, 0, 0)]);
        store.ChunkCount().Should().Be(1);
    }

    [Fact]
    public void Search_OnAClosedStore_IsEmptyRatherThanAThrow()
    {
        using var store = NewStore();

        store.Search(Vector(1, 0, 0, 0), null, 10).Should().BeEmpty();
        store.ChunkCount().Should().Be(0);
        store.IsCurrent(@"D:\a.cs", DateTime.UtcNow).Should().BeFalse();
    }

    [Theory]
    [InlineData(0.0, 1.0)]      // identical direction
    [InlineData(2.0, -1.0)]     // opposite: d² = 4 → cos = −1, clamped to 0
    public void ToScore_ConvertsL2DistanceToCosine(double distance, double expected)
    {
        var score = VectorStore.ToScore(distance);

        score.Should().BeApproximately(Math.Clamp(expected, 0, 1), 1e-6);
    }
}
