using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace TCV.Ai.Semantic;

/// <summary>One semantic hit: where it is, what it says, how close it was.</summary>
/// <param name="Score">1 = identical direction, 0 = unrelated. Converted from sqlite-vec's distance.</param>
public sealed record SemanticHit(string Path, int StartLine, int EndLine, string Text, double Score);

/// <summary>
/// The local vector index: SQLite plus the <c>sqlite-vec</c> extension, in one file next to the
/// executable (INV-03, ADR-0017).
/// <para>
/// Two things about this class are deliberate. First, the vectors never leave the machine — that is
/// the ADR-0008 privacy line, and a local file is how it is kept. Second, <c>vec0</c> fixes the
/// vector width at table-creation time, so the dimension is recorded in <c>meta</c> and a changed
/// embedding model rebuilds rather than silently mixing incompatible vectors — the failure that
/// mixing produces is not an error, it is quietly meaningless search results.
/// </para>
/// </summary>
public sealed class VectorStore : IDisposable
{
    public const string FileName = "semantic-index.db";

    private readonly string _databasePath;
    private readonly ILogger<VectorStore>? _logger;

    // One SQLite connection is shared by the indexing pass (a background task), the search dialog and
    // the MCP server's threads, and a SqliteConnection is not safe for concurrent use. Every public
    // member that touches it takes this lock; it is never held across an await.
    private readonly object _sync = new();

    private SqliteConnection? _connection;
    private int _dimensions;

    public VectorStore(string? databasePath = null, ILogger<VectorStore>? logger = null)
    {
        _databasePath = databasePath ?? global::TCV.Core.Domain.AppPaths.Data(FileName);
        _logger = logger;
    }

    public string DatabasePath => _databasePath;

    /// <summary>Vector width the open index was built for; 0 before <see cref="Open"/>.</summary>
    public int Dimensions => _dimensions;

    /// <summary>Why the store is unavailable, when it is — shown instead of a silent no-op.</summary>
    public string? UnavailableReason { get; private set; }

    public bool IsOpen => _connection is not null;

    /// <summary>
    /// Opens (creating if needed) an index for vectors of <paramref name="dimensions"/> width.
    /// An existing index built for a different width is dropped and rebuilt.
    /// </summary>
    /// <returns>False when sqlite-vec could not be loaded; <see cref="UnavailableReason"/> says why.</returns>
    /// <param name="dimensions">Vector width of the model in use.</param>
    /// <param name="rebuildOnMismatch">
    /// True (indexing) drops an index built for another width. False (searching) refuses instead:
    /// a query must never destroy the index — least of all one an agent asked for through MCP — and
    /// vectors of another width cannot be compared with the query anyway.
    /// </param>
    public bool Open(int dimensions, bool rebuildOnMismatch = true)
    {
        lock (_sync) return OpenCore(dimensions, rebuildOnMismatch);
    }

    private bool OpenCore(int dimensions, bool rebuildOnMismatch)
    {
        if (dimensions <= 0)
        {
            UnavailableReason = "The embedding model reported no vector width.";
            return false;
        }

        if (IsOpen && _dimensions == dimensions) return true;
        Close();

        var extension = FindExtension();
        if (extension is null)
        {
            UnavailableReason =
                "vec0.dll was not found next to the application, so the semantic index cannot be opened.";
            _logger?.LogWarning("{Reason}", UnavailableReason);
            return false;
        }

        try
        {
            var connection = new SqliteConnection($"Data Source={_databasePath}");
            connection.Open();
            connection.EnableExtensions(true);
            connection.LoadExtension(extension);

            _connection = connection;
            _dimensions = dimensions;

            if (!EnsureSchema(dimensions, rebuildOnMismatch))
            {
                Close();
                return false;
            }

            UnavailableReason = null;
            return true;
        }
        catch (Exception ex)
        {
            UnavailableReason = "The semantic index could not be opened: " + ex.Message;
            _logger?.LogWarning(ex, "Failed to open the vector store at {Path}", _databasePath);
            Close();
            return false;
        }
    }

    private bool EnsureSchema(int dimensions, bool rebuildOnMismatch)
    {
        Execute("""
            create table if not exists meta (key text primary key, value text not null);
            create table if not exists chunks (
                id         integer primary key autoincrement,
                root       text not null,
                path       text not null,
                start_line integer not null,
                end_line   integer not null,
                text       text not null,
                mtime      integer not null
            );
            create index if not exists chunks_path on chunks(path);
            create index if not exists chunks_root on chunks(root);
            """);

        var stored = ReadMeta("dimensions");
        if (stored is not null && stored != dimensions.ToString())
        {
            if (!rebuildOnMismatch)
            {
                UnavailableReason =
                    "The index was built with another embedding model. Rebuild it from Semantic search (Index).";
                return false;
            }

            _logger?.LogInformation(
                "Embedding width changed ({Old} → {New}); rebuilding the semantic index", stored, dimensions);
            Execute("drop table if exists chunk_vectors; delete from chunks;");
        }

        Execute($"create virtual table if not exists chunk_vectors using vec0(id integer primary key, embedding float[{dimensions}])");
        WriteMeta("dimensions", dimensions.ToString());
        return true;
    }

    /// <summary>True when this file's chunks are already indexed at this modification time.</summary>
    public bool IsCurrent(string path, DateTime modifiedUtc)
    {
        lock (_sync) return IsCurrentCore(path, modifiedUtc);
    }

    private bool IsCurrentCore(string path, DateTime modifiedUtc)
    {
        if (_connection is null) return false;

        using var command = _connection.CreateCommand();
        command.CommandText = "select 1 from chunks where path = $path and mtime = $mtime limit 1";
        command.Parameters.AddWithValue("$path", Normalize(path));
        command.Parameters.AddWithValue("$mtime", modifiedUtc.Ticks);

        return command.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Replaces everything indexed for one file. Delete-then-insert in a transaction rather than a
    /// diff: chunk boundaries shift when a file is edited, so there is no stable identity to update.
    /// </summary>
    public void Replace(
        string root,
        string path,
        DateTime modifiedUtc,
        IReadOnlyList<TextChunk> chunks,
        IReadOnlyList<float[]> vectors)
    {
        lock (_sync) ReplaceCore(root, path, modifiedUtc, chunks, vectors);
    }

    private void ReplaceCore(
        string root,
        string path,
        DateTime modifiedUtc,
        IReadOnlyList<TextChunk> chunks,
        IReadOnlyList<float[]> vectors)
    {
        if (_connection is null) return;
        if (chunks.Count == 0) { RemoveCore(path); return; }

        var count = Math.Min(chunks.Count, vectors.Count);
        using var transaction = _connection.BeginTransaction();

        RemoveInternal(path, transaction);

        for (int i = 0; i < count; i++)
        {
            if (vectors[i] is null || vectors[i].Length != _dimensions) continue;

            long id;
            using (var insert = _connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    insert into chunks (root, path, start_line, end_line, text, mtime)
                    values ($root, $path, $start, $end, $text, $mtime);
                    select last_insert_rowid();
                    """;
                insert.Parameters.AddWithValue("$root", Normalize(root));
                insert.Parameters.AddWithValue("$path", Normalize(path));
                insert.Parameters.AddWithValue("$start", chunks[i].StartLine);
                insert.Parameters.AddWithValue("$end", chunks[i].EndLine);
                insert.Parameters.AddWithValue("$text", chunks[i].Text);
                insert.Parameters.AddWithValue("$mtime", modifiedUtc.Ticks);
                id = Convert.ToInt64(insert.ExecuteScalar());
            }

            using var vector = _connection.CreateCommand();
            vector.Transaction = transaction;
            vector.CommandText = "insert into chunk_vectors (id, embedding) values ($id, $embedding)";
            vector.Parameters.AddWithValue("$id", id);
            vector.Parameters.AddWithValue("$embedding", ToBlob(vectors[i]));
            vector.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Drops one file from the index — used when it is deleted or stops being indexable.</summary>
    public void Remove(string path)
    {
        lock (_sync) RemoveCore(path);
    }

    private void RemoveCore(string path)
    {
        if (_connection is null) return;

        using var transaction = _connection.BeginTransaction();
        RemoveInternal(path, transaction);
        transaction.Commit();
    }

    /// <summary>
    /// Drops every file indexed under <paramref name="root"/> that is not in <paramref name="keep"/>:
    /// deleted files, renamed ones, and files that became ignored or unindexable. Without this an
    /// index only ever grows, and search keeps serving text from files that are gone — or, worse,
    /// from a file that has since been added to .gitignore because it holds a secret.
    /// </summary>
    /// <returns>How many files were removed.</returns>
    public int PruneRoot(string root, IEnumerable<string> keep)
    {
        lock (_sync)
        {
            if (_connection is null) return 0;

            var kept = new HashSet<string>(keep.Select(Normalize), StringComparer.OrdinalIgnoreCase);
            var stale = new List<string>();

            using (var command = _connection.CreateCommand())
            {
                command.CommandText = "select distinct path from chunks where root = $root";
                command.Parameters.AddWithValue("$root", Normalize(root));

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var path = reader.GetString(0);
                    if (!kept.Contains(path)) stale.Add(path);
                }
            }

            if (stale.Count == 0) return 0;

            using var transaction = _connection.BeginTransaction();
            foreach (var path in stale) RemoveInternal(path, transaction);
            transaction.Commit();

            return stale.Count;
        }
    }

    private void RemoveInternal(string path, SqliteTransaction transaction)
    {
        if (_connection is null) return;

        // vec0 is a separate virtual table with no foreign keys, so its rows have to go explicitly —
        // orphaned vectors would keep matching and point at chunk rows that no longer exist.
        using var vectors = _connection.CreateCommand();
        vectors.Transaction = transaction;
        vectors.CommandText =
            "delete from chunk_vectors where id in (select id from chunks where path = $path)";
        vectors.Parameters.AddWithValue("$path", Normalize(path));
        vectors.ExecuteNonQuery();

        using var rows = _connection.CreateCommand();
        rows.Transaction = transaction;
        rows.CommandText = "delete from chunks where path = $path";
        rows.Parameters.AddWithValue("$path", Normalize(path));
        rows.ExecuteNonQuery();
    }

    /// <summary>
    /// Nearest chunks to <paramref name="queryVector"/>, optionally restricted to one root.
    /// </summary>
    /// <remarks>
    /// The root filter is applied <em>after</em> the KNN join rather than inside the <c>match</c>
    /// clause, and <paramref name="limit"/> is over-fetched to compensate: vec0's k applies to the
    /// vector scan, so filtering inside it would silently return fewer than k rows.
    /// </remarks>
    public IReadOnlyList<SemanticHit> Search(float[] queryVector, string? root, int limit)
    {
        lock (_sync) return SearchCore(queryVector, root, limit);
    }

    private IReadOnlyList<SemanticHit> SearchCore(float[] queryVector, string? root, int limit)
    {
        if (_connection is null || queryVector.Length != _dimensions) return Array.Empty<SemanticHit>();

        var scoped = !string.IsNullOrEmpty(root);
        var fetch = Math.Clamp(scoped ? limit * 8 : limit, 1, 2_000);

        using var command = _connection.CreateCommand();
        command.CommandText = """
            select c.path, c.start_line, c.end_line, c.text, v.distance
            from chunk_vectors v
            join chunks c on c.id = v.id
            where v.embedding match $query and k = $k
            order by v.distance
            """;
        command.Parameters.AddWithValue("$query", ToBlob(queryVector));
        command.Parameters.AddWithValue("$k", fetch);

        var prefix = scoped ? Normalize(root!) : string.Empty;
        var hits = new List<SemanticHit>();

        using var reader = command.ExecuteReader();
        while (reader.Read() && hits.Count < limit)
        {
            var path = reader.GetString(0);
            if (scoped && !IsUnder(prefix, path)) continue;

            hits.Add(new SemanticHit(
                path,
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetString(3),
                ToScore(reader.GetDouble(4))));
        }

        return hits;
    }

    /// <summary>Indexed chunk count — what the status line reports.</summary>
    public long ChunkCount()
    {
        lock (_sync)
        {
            if (_connection is null) return 0;

            using var command = _connection.CreateCommand();
            command.CommandText = "select count(*) from chunks";
            return Convert.ToInt64(command.ExecuteScalar());
        }
    }

    /// <summary>Indexed file count.</summary>
    public long FileCount()
    {
        lock (_sync)
        {
            if (_connection is null) return 0;

            using var command = _connection.CreateCommand();
            command.CommandText = "select count(distinct path) from chunks";
            return Convert.ToInt64(command.ExecuteScalar());
        }
    }

    /// <summary>Empties the index, keeping the schema — the "rebuild from scratch" action.</summary>
    public void Clear()
    {
        lock (_sync)
        {
            if (_connection is null) return;
            Execute("delete from chunk_vectors; delete from chunks;");
        }
    }

    /// <summary>
    /// sqlite-vec returns an L2 distance. Vectors from every embedding API in this family are
    /// normalised, and for unit vectors <c>d² = 2 − 2·cos</c>, so this recovers cosine similarity
    /// exactly — and is clamped so a non-normalised model cannot produce a score outside 0..1.
    /// </summary>
    public static double ToScore(double distance) =>
        Math.Clamp(1.0 - (distance * distance) / 2.0, 0, 1);

    internal static byte[] ToBlob(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static bool IsUnder(string root, string path) =>
        path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
        && (path.Length == root.Length || path[root.Length] == '/' || root.EndsWith('/'));

    /// <summary>Stored paths use forward slashes so a query built either way matches.</summary>
    private static string Normalize(string path) =>
        path.Replace('\\', '/').TrimEnd('/');

    /// <summary>
    /// Locates the sqlite-vec native library. Checked next to the executable first (where a
    /// self-contained publish flattens it) and then in the RID subfolder (where a plain build leaves
    /// it). The absolute path is passed to <c>LoadExtension</c> on purpose: SQLite resolves a bare
    /// name through <c>LoadLibrary</c>, which does not know about .NET's runtime asset layout.
    /// </summary>
    private static string? FindExtension()
    {
        string[] candidates =
        [
            "vec0.dll",
            Path.Combine("runtimes", "win-x64", "native", "vec0.dll"),
            Path.Combine("runtimes", "win-arm64", "native", "vec0.dll"),
        ];

        foreach (var candidate in candidates)
        {
            var full = Path.Combine(AppContext.BaseDirectory, candidate);
            if (File.Exists(full)) return full;
        }

        return null;
    }

    private void Execute(string sql)
    {
        if (_connection is null) return;

        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private string? ReadMeta(string key)
    {
        if (_connection is null) return null;

        using var command = _connection.CreateCommand();
        command.CommandText = "select value from meta where key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private void WriteMeta(string key, string value)
    {
        if (_connection is null) return;

        using var command = _connection.CreateCommand();
        command.CommandText =
            "insert into meta (key, value) values ($key, $value) on conflict(key) do update set value = $value";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private void Close()
    {
        _connection?.Dispose();
        _connection = null;
        _dimensions = 0;
    }

    public void Dispose()
    {
        lock (_sync) Close();
    }
}
