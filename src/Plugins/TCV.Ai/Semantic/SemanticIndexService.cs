using Microsoft.Extensions.Logging;
using TCV.Ai.Llm;
using TCV.Core.Domain;

namespace TCV.Ai.Semantic;

/// <summary>How an index pass is going, for the progress line.</summary>
public sealed record SemanticIndexProgress(
    int FilesSeen, int FilesIndexed, int FilesSkipped, int ChunksEmbedded, string CurrentFile);

/// <summary>What an index pass ended up doing.</summary>
public sealed record SemanticIndexResult(
    bool Success,
    int FilesIndexed,
    int FilesSkipped,
    int ChunksEmbedded,
    string Message)
{
    public static SemanticIndexResult Fail(string message) => new(false, 0, 0, 0, message);
}

/// <summary>
/// Builds and queries the local semantic index (6.16): read a file, split it, embed the pieces
/// through the configured endpoint, keep the vectors in a local SQLite file.
/// <para>
/// Semantic search here <em>complements</em> exact search, it does not replace it — a developer
/// looking for <c>RejectIfMountedAsync</c> wants ripgrep, and one looking for "where do we decide a
/// panel is remote" wants this. That is why the roadmap keeps 6.17 and this as separate features and
/// why the search dialog offers a switch rather than a replacement.
/// </para>
/// </summary>
public sealed class SemanticIndexService
{
    /// <summary>Files above this size are skipped: minified bundles and data dumps embed into noise.</summary>
    public const long MaxFileBytes = 512 * 1024;

    /// <summary>Chunks per embedding request. Keeps one HTTP round trip per ~30 chunks.</summary>
    public const int BatchSize = 32;

    private readonly LlmClient _llm;
    private readonly VectorStore _store;
    private readonly ILogger<SemanticIndexService>? _logger;

    public SemanticIndexService(
        LlmClient llm, VectorStore store, ILogger<SemanticIndexService>? logger = null)
    {
        _llm = llm;
        _store = store;
        _logger = logger;
    }

    public VectorStore Store => _store;

    public bool IsConfigured => _llm.Options.CanEmbed;

    /// <summary>
    /// Indexes the given files under <paramref name="root"/>. Files already indexed at their current
    /// modification time are skipped, so a second pass over an unchanged tree costs one query per file
    /// and no embedding calls at all.
    /// </summary>
    /// <param name="files">Absolute paths, already filtered by the caller (gitignore-aware).</param>
    public async Task<SemanticIndexResult> IndexAsync(
        string root,
        IReadOnlyList<string> files,
        IProgress<SemanticIndexProgress>? progress = null,
        CancellationToken ct = default,
        bool filesAreComplete = true)
    {
        if (!_llm.Options.CanEmbed) return SemanticIndexResult.Fail(LlmClient.NotConfiguredForEmbeddings);

        // The vector width is not configurable — it is whatever the model returns, so it is measured
        // once with a probe before the store is opened.
        var dimensions = await ProbeDimensionsAsync(ct);
        if (dimensions <= 0)
            return SemanticIndexResult.Fail(_probeError ?? "The embedding endpoint returned no vector.");

        if (!_store.Open(dimensions))
            return SemanticIndexResult.Fail(_store.UnavailableReason ?? "The semantic index is unavailable.");

        int seen = 0, indexed = 0, skipped = 0, embedded = 0;
        string? lastError = null;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            seen++;

            // A long stretch of unchanged files reports nothing below, so the line would look frozen.
            if (seen % 25 == 0)
                progress?.Report(new SemanticIndexProgress(seen, indexed, skipped, embedded, file));

            // A file that stopped being indexable (gone, grown past the cap, now binary) must also
            // leave the index — skipping it would leave its old text searchable.
            var info = SafeInfo(file);
            if (info is null || info.Length == 0 || info.Length > MaxFileBytes)
            {
                _store.Remove(file);
                skipped++;
                continue;
            }

            var modifiedUtc = info.LastWriteTimeUtc;
            if (_store.IsCurrent(file, modifiedUtc))
            {
                skipped++;
                continue;
            }

            var text = ReadText(file);
            if (text is null)
            {
                _store.Remove(file);
                skipped++;
                continue;
            }

            var chunks = TextChunker.Split(text);
            if (chunks.Count == 0)
            {
                _store.Remove(file);
                skipped++;
                continue;
            }

            progress?.Report(new SemanticIndexProgress(seen, indexed, skipped, embedded, file));

            var vectors = new List<float[]>(chunks.Count);
            var complete = true;

            for (int offset = 0; offset < chunks.Count; offset += BatchSize)
            {
                ct.ThrowIfCancellationRequested();

                var batch = chunks.Skip(offset).Take(BatchSize).Select(c => c.Text).ToList();
                var result = await _llm.EmbedAsync(batch, error => lastError = error, ct);

                if (result.Count != batch.Count)
                {
                    // A partial file would answer queries with half its content and look indexed, so
                    // the whole file is abandoned and retried on the next pass.
                    complete = false;
                    break;
                }

                vectors.AddRange(result);
            }

            if (!complete)
            {
                skipped++;
                continue;
            }

            _store.Replace(root, file, modifiedUtc, chunks, vectors);
            indexed++;
            embedded += vectors.Count;
        }

        // Files under this root that the walk no longer produced: deleted, renamed, or now ignored.
        // Only when the list is the whole picture — a walk cut short by its entry cap would otherwise
        // read as «everything past the cap was deleted».
        var pruned = filesAreComplete ? _store.PruneRoot(root, files) : 0;

        progress?.Report(new SemanticIndexProgress(seen, indexed, skipped, embedded, string.Empty));
        _logger?.LogInformation(
            "Semantic index: {Indexed} file(s) embedded, {Skipped} skipped, {Pruned} removed, {Chunks} chunk(s)",
            indexed, skipped, pruned, embedded);

        var message = lastError is null
            ? string.Empty
            : $"Some files were skipped: {lastError}";

        return new SemanticIndexResult(true, indexed, skipped, embedded, message);
    }

    /// <summary>Nearest chunks to a natural-language query.</summary>
    public async Task<IReadOnlyList<SemanticHit>> SearchAsync(
        string query, string? root, int limit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || !_llm.Options.CanEmbed)
            return Array.Empty<SemanticHit>();

        var vectors = await _llm.EmbedAsync([query], null, ct);
        if (vectors.Count == 0) return Array.Empty<SemanticHit>();

        // A search never rebuilds: an index built by another model is refused (the reason is left in
        // Store.UnavailableReason for the dialog to show), not dropped — a query, and above all one an
        // agent sent through MCP, must not be able to destroy an hour of indexing.
        if (!_store.Open(vectors[0].Length, rebuildOnMismatch: false)) return Array.Empty<SemanticHit>();

        return _store.Search(vectors[0], root, Math.Clamp(limit, 1, 200));
    }

    private string? _probeError;

    /// <summary>
    /// Asks the endpoint for one trivial embedding to learn the model's vector width. Cheap, and the
    /// only honest way to know: the width is a property of the model, not something we can configure.
    /// </summary>
    private async Task<int> ProbeDimensionsAsync(CancellationToken ct)
    {
        _probeError = null;
        var vectors = await _llm.EmbedAsync(["probe"], error => _probeError = error, ct);
        return vectors.Count > 0 ? vectors[0].Length : 0;
    }

    private static FileInfo? SafeInfo(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a file as text, or null when it is not text. Uses the shared <see cref="TextProbe"/>
    /// (ADR-0016) so "is this binary?" and "which encoding?" are answered the same way here, in the
    /// F3 viewer and in content search — three different answers to that question would mean three
    /// different sets of files being searchable.
    /// </summary>
    private static string? ReadText(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (TextProbe.LooksBinary(bytes)) return null;

            var (encoding, bomLength) = TextProbe.DetectEncoding(bytes);
            return TextProbe.DecodeText(bytes[bomLength..], encoding);
        }
        catch
        {
            return null;
        }
    }
}
