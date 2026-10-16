using FluentAssertions;
using TCV.Ai.Semantic;

namespace TCV.Ai.Tests;

/// <summary>
/// The parts of the vector index that protect it: one connection shared by the indexing pass and an
/// agent's searches, a query that must never destroy the index, and files that leave it when they
/// leave the project. Like <see cref="VectorStoreTests"/> these need the native sqlite-vec library and
/// report success without it.
/// </summary>
public sealed class VectorStoreSafetyTests : IDisposable
{
    private const int Dimensions = 4;
    private const string Root = "D:/repo";

    private readonly string _temp =
        Path.Combine(Path.GetTempPath(), "tcv-vec-safe-" + Guid.NewGuid().ToString("n")[..8]);

    public VectorStoreSafetyTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); }
        catch { /* the file may still be held by SQLite on a failed test */ }
    }

    private VectorStore? OpenOrSkip(int dimensions = Dimensions)
    {
        var store = new VectorStore(Path.Combine(_temp, "index.db"));
        if (store.Open(dimensions)) return store;

        store.Dispose();
        return null;
    }

    private static TextChunk Chunk(int index, string text = "a fragment long enough to be kept as a chunk of its own") =>
        new(index, 1, 3, text);

    private static void Put(VectorStore store, string path, params float[] vector) =>
        store.Replace(Root, path, DateTime.UtcNow, [Chunk(0)], [vector]);

    [Fact]
    public void Search_WithAVectorOfAnotherWidth_RefusesInsteadOfDroppingTheIndex()
    {
        using var store = OpenOrSkip();
        if (store is null) return;
        Put(store, "D:/repo/a.cs", 1, 0, 0, 0);

        // A different embedding model is now configured, and an agent searches.
        store.Open(8, rebuildOnMismatch: false).Should().BeFalse();
        store.UnavailableReason.Should().Contain("another embedding model");

        // The index is intact and opens again with the width it was built for.
        store.Open(Dimensions).Should().BeTrue();
        store.FileCount().Should().Be(1);
    }

    [Fact]
    public void Open_WithAnotherWidthAndRebuildAllowed_StillRebuilds()
    {
        using var store = OpenOrSkip();
        if (store is null) return;
        Put(store, "D:/repo/a.cs", 1, 0, 0, 0);

        store.Open(8).Should().BeTrue();

        store.FileCount().Should().Be(0);
    }

    [Fact]
    public void PruneRoot_RemovesFilesThatLeftTheProject_AndOnlyThose()
    {
        using var store = OpenOrSkip();
        if (store is null) return;
        Put(store, "D:/repo/keep.cs", 1, 0, 0, 0);
        Put(store, "D:/repo/gone.cs", 0, 1, 0, 0);
        store.Replace("D:/other", "D:/other/x.cs", DateTime.UtcNow, [Chunk(0)], [new float[] { 0, 0, 1, 0 }]);

        var removed = store.PruneRoot(Root, [@"D:\repo\keep.cs"]);

        removed.Should().Be(1);
        store.FileCount().Should().Be(2);   // keep.cs and the other project's x.cs
        store.Search([0f, 1f, 0f, 0f], Root, 5).Should().NotContain(h => h.Path.EndsWith("gone.cs"));
    }

    [Fact]
    public void Remove_DropsOneFile()
    {
        using var store = OpenOrSkip();
        if (store is null) return;
        Put(store, "D:/repo/a.cs", 1, 0, 0, 0);

        store.Remove(@"D:\repo\a.cs");

        store.FileCount().Should().Be(0);
    }

    [Fact]
    public async Task ConcurrentIndexingAndSearching_DoNotCorruptOrThrow()
    {
        using var store = OpenOrSkip();
        if (store is null) return;

        // An indexing pass and an agent's searches share one connection; without the lock this fails
        // with «connection busy» / disposed-object errors within a few iterations.
        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 150; i++)
                Put(store, $"D:/repo/f{i % 20}.cs", 1, i % 3, 0, 0);
        });

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 150; i++)
            {
                store.Search([1f, 0f, 0f, 0f], Root, 5);
                store.ChunkCount();
                store.IsCurrent("D:/repo/f1.cs", DateTime.UtcNow);
            }
        })).ToArray();

        var act = async () => await Task.WhenAll(readers.Append(writer));

        await act.Should().NotThrowAsync();
        store.FileCount().Should().BeInRange(1, 20);
    }
}
