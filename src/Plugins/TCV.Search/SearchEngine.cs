using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.Search;

/// <summary>How far a running search has got — for a status line, not for correctness.</summary>
/// <param name="Directories">Folders opened so far.</param>
/// <param name="Files">Files examined so far.</param>
/// <param name="CurrentDirectory">Folder being read right now.</param>
public sealed record SearchProgress(int Directories, int Files, string CurrentDirectory);

/// <summary>
/// Async file searcher that yields results incrementally via IAsyncEnumerable.
/// Works on any <see cref="IVirtualFileSystem"/> (INV-02).
/// </summary>
/// <remarks>
/// Ripgrep-shaped by default: <c>.gitignore</c> rules found in the walked tree — and in the folders
/// above the search root, up to the repository — are honoured, VCS metadata is skipped, binary files
/// are not grepped, and each hit carries its column and optional context lines. Everything that decides
/// "is this text and in what encoding" comes from <see cref="TextProbe"/>, so the viewer and the search
/// agree on the same file.
/// <para>
/// The whole pipeline (listing, reading, decoding, regex) runs on a pool thread and hands results to the
/// consumer through a channel (INV-06): a caller on the UI thread only ever pays for reading the channel.
/// Directories that are reparse points (junctions, symlinks) are listed but not entered, so a loop cannot
/// send the walk round in circles; cloud placeholders are not opened for content, which would download
/// them.
/// </para>
/// </remarks>
public sealed class SearchEngine
{
    /// <summary>Bytes probed to classify a file as text/binary and to guess its encoding.</summary>
    private const int ProbeBytes = 8192;

    private const int MaxGitignoreBytes = 256 * 1024;

    private const int MaxAncestorClimb = 64;

    /// <summary>OneDrive-style placeholders: reading them makes Windows download the file.</summary>
    private const FileAttributes CloudPlaceholder =
        FileAttributes.Offline | (FileAttributes)0x00400000 /* RECALL_ON_DATA_ACCESS */ | (FileAttributes)0x00040000 /* RECALL_ON_OPEN */;

    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);

    private readonly IVirtualFileSystem _vfs;
    private int _directories;
    private int _files;
    private int _unreadable;
    private int _timedOut;

    public SearchEngine(IVirtualFileSystem vfs)
    {
        _vfs = vfs;
    }

    /// <summary>True when the walk stopped early because <see cref="SearchCriteria.MaxResults"/>
    /// was reached. Read it once the enumeration finishes; one engine serves one pass.</summary>
    public bool WasTruncated { get; private set; }

    /// <summary>Folders opened so far (readable ones).</summary>
    public int DirectoriesScanned => Volatile.Read(ref _directories);

    /// <summary>Files examined so far.</summary>
    public int FilesScanned => Volatile.Read(ref _files);

    /// <summary>Folders that could not be listed (access denied, vanished). The search goes on without them.</summary>
    public int UnreadableDirectories => Volatile.Read(ref _unreadable);

    /// <summary>Files skipped because the regex ran out of its time budget on one of their lines.</summary>
    public int TimedOutFiles => Volatile.Read(ref _timedOut);

    /// <summary>Optional progress sink, called from the search thread at most a few times a second.</summary>
    public IProgress<SearchProgress>? Progress { get; set; }

    /// <summary>
    /// Searches <paramref name="rootPath"/> recursively, yielding matches as they are found.
    /// </summary>
    public IAsyncEnumerable<SearchResult> SearchAsync(
        VfsPath rootPath, SearchCriteria criteria, CancellationToken ct = default)
    {
        return PumpAsync<SearchResult>(
            (writer, token) => ProduceSearchAsync(rootPath, criteria, writer, token),
            () =>
            {
                criteria.Compile();   // a malformed pattern surfaces to the caller before any work starts
                Reset();
            },
            ct);
    }

    /// <summary>
    /// Walks <paramref name="rootPath"/> with the same structural rules as a search (hidden files,
    /// <c>.gitignore</c>, exclude masks, skipped directories, depth, reparse points) and yields the files
    /// for which <paramref name="predicate"/> is true. Name, size, date and content filters do not apply.
    /// Used to find archives to look into.
    /// </summary>
    public IAsyncEnumerable<IFileEntry> EnumerateFilesAsync(
        VfsPath rootPath, SearchCriteria criteria, Func<IFileEntry, bool> predicate, CancellationToken ct = default)
    {
        return PumpAsync<IFileEntry>(
            async (writer, token) =>
            {
                await foreach (var node in WalkAsync(rootPath, criteria, token).ConfigureAwait(false))
                {
                    if (!node.Entry.IsDirectory && predicate(node.Entry))
                    {
                        await writer.WriteAsync(node.Entry, token).ConfigureAwait(false);
                    }
                }
            },
            () =>
            {
                criteria.Compile();
                Reset();
            },
            ct);
    }

    private void Reset()
    {
        WasTruncated = false;
        _directories = _files = _unreadable = _timedOut = 0;
    }

    // ── Pipeline plumbing ─────────────────────────────────────────────────────

    /// <summary>
    /// Runs <paramref name="produce"/> on the thread pool and streams what it writes. A consumer that
    /// stops early (or is cancelled) cancels the producer; a producer that fails fails the consumer.
    /// <paramref name="prepare"/> runs first, on the consumer's first MoveNext.
    /// </summary>
    private static async IAsyncEnumerable<T> PumpAsync<T>(
        Func<ChannelWriter<T>, CancellationToken, Task> produce,
        Action prepare,
        [EnumeratorCancellation] CancellationToken ct)
    {
        prepare();

        var channel = Channel.CreateBounded<T>(new BoundedChannelOptions(512)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var producer = Task.Run(async () =>
        {
            try
            {
                await produce(channel.Writer, linked.Token).ConfigureAwait(false);
                channel.Writer.TryComplete();
            }
            catch (OperationCanceledException)
            {
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                // ReadAllAsync hands out everything already buffered without looking at the token, so a
                // consumer that cancelled would still receive up to a channel-full of stale results.
                ct.ThrowIfCancellationRequested();
                yield return item;
            }
        }
        finally
        {
            linked.Cancel();
            await producer.ConfigureAwait(false);
        }
    }

    // ── Search over the walk ──────────────────────────────────────────────────

    private async Task ProduceSearchAsync(
        VfsPath root, SearchCriteria criteria, ChannelWriter<SearchResult> writer, CancellationToken ct)
    {
        var produced = 0;

        // Returns false once the result cap is hit.
        async ValueTask<bool> Emit(SearchResult result)
        {
            await writer.WriteAsync(result, ct).ConfigureAwait(false);
            if (criteria.MaxResults > 0 && ++produced >= criteria.MaxResults)
            {
                WasTruncated = true;
                return false;
            }
            return true;
        }

        await foreach (var node in WalkAsync(root, criteria, ct).ConfigureAwait(false))
        {
            var entry = node.Entry;

            if (entry.IsDirectory)
            {
                if (!criteria.FilesOnly && criteria.CompiledContentRegex is null && MatchesEntry(entry, criteria))
                {
                    if (!await Emit(new SearchResult(entry, entry.Path.Value)).ConfigureAwait(false))
                    {
                        return;
                    }
                }
                continue;
            }

            if (criteria.DirectoriesOnly)
            {
                continue;
            }

            Interlocked.Increment(ref _files);
            if (!MatchesEntry(entry, criteria))
            {
                continue;
            }

            if (criteria.CompiledContentRegex is null)
            {
                if (!await Emit(new SearchResult(entry, entry.Path.Value)).ConfigureAwait(false))
                {
                    return;
                }

                continue;
            }

            // Content search runs on a fully buffered file, so its per-file failures are contained.
            foreach (var match in await SearchContentAsync(entry, criteria, ct).ConfigureAwait(false))
            {
                if (!await Emit(match).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
    }

    // ── Traversal ─────────────────────────────────────────────────────────────

    private readonly record struct WalkNode(IFileEntry Entry, string Relative);

    private sealed class Frame(
        IReadOnlyList<IFileEntry> entries, string relative, string ignoreRelative, int depth, GitignoreScope scope)
    {
        public IReadOnlyList<IFileEntry> Entries { get; } = entries;
        public int Index { get; set; }
        /// <summary>Path from the search root, for exclude masks and result display.</summary>
        public string Relative { get; } = relative;
        /// <summary>Path from the repository root (the search root's ancestors included), for .gitignore.</summary>
        public string IgnoreRelative { get; } = ignoreRelative;
        public int Depth { get; } = depth;
        public GitignoreScope Scope { get; } = scope;
    }

    /// <summary>
    /// Pre-order walk with an explicit stack: a directory is reported before its contents, and no
    /// iterator is nested per level, so a deep tree costs a stack frame per directory, not per file.
    /// </summary>
    private async IAsyncEnumerable<WalkNode> WalkAsync(
        VfsPath root, SearchCriteria criteria, [EnumeratorCancellation] CancellationToken ct)
    {
        var (ancestorScope, ignorePrefix) = criteria.RespectGitignore
            ? await LoadAncestorScopeAsync(root, ct).ConfigureAwait(false)
            : (GitignoreScope.Empty, string.Empty);

        var stack = new Stack<Frame>();
        var clock = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;

        var first = await OpenAsync(root, string.Empty, ignorePrefix, 0, ancestorScope, criteria, ct).ConfigureAwait(false);
        if (first is not null)
        {
            stack.Push(first);
        }

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var frame = stack.Peek();
            if (frame.Index >= frame.Entries.Count)
            {
                stack.Pop();
                continue;
            }

            var entry = frame.Entries[frame.Index++];

            // Skip hidden/system unless requested
            if (!criteria.IncludeHidden && entry.Attributes.HasFlag(FileAttributes.Hidden))
            {
                continue;
            }

            if (!criteria.IncludeSystem && entry.Attributes.HasFlag(FileAttributes.System))
            {
                continue;
            }

            var relative = frame.Relative.Length == 0 ? entry.Name : frame.Relative + "/" + entry.Name;
            var ignoreRelative = frame.IgnoreRelative.Length == 0 ? entry.Name : frame.IgnoreRelative + "/" + entry.Name;

            if (criteria.RespectGitignore && frame.Scope.IsIgnoredEntry(ignoreRelative, entry.IsDirectory))
            {
                continue;
            }

            if (criteria.ExcludeRules.MatchEntry(relative, entry.IsDirectory) == true)
            {
                continue;
            }

            if (entry.IsDirectory)
            {
                if (criteria.SkipDirectories.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return new WalkNode(entry, relative);

                // Listed (so its name can match) but not entered: a junction or symlink may point
                // back up the tree, or at another drive.
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                if (criteria.MaxDepth > 0 && frame.Depth + 1 > criteria.MaxDepth)
                {
                    continue;
                }

                var child = await OpenAsync(
                    entry.Path, relative, ignoreRelative, frame.Depth + 1, frame.Scope, criteria, ct).ConfigureAwait(false);
                if (child is not null)
                {
                    stack.Push(child);
                }
            }
            else
            {
                yield return new WalkNode(entry, relative);
            }

            if (Progress is not null && clock.Elapsed - lastReport > ProgressInterval)
            {
                lastReport = clock.Elapsed;
                Progress.Report(new SearchProgress(DirectoriesScanned, FilesScanned, frame.Relative));
            }
        }

        Progress?.Report(new SearchProgress(DirectoriesScanned, FilesScanned, string.Empty));
    }

    private async Task<Frame?> OpenAsync(
        VfsPath dirPath, string relative, string ignoreRelative, int depth, GitignoreScope scope,
        SearchCriteria criteria, CancellationToken ct)
    {
        IReadOnlyList<IFileEntry> entries;
        try
        {
            entries = await _vfs.ListAsync(dirPath, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Access denied, vanished, or a share that went away: one folder must not end the search,
            // but it is counted so the caller can say some were skipped.
            Interlocked.Increment(ref _unreadable);
            return null;
        }

        Interlocked.Increment(ref _directories);

        if (criteria.RespectGitignore)
        {
            scope = await PushGitignoreAsync(scope, ignoreRelative, entries, ct).ConfigureAwait(false);
        }

        return new Frame(entries, relative, ignoreRelative, depth, scope);
    }

    /// <summary>
    /// Rules from the folders above the search root, up to the working copy's root: searching from
    /// <c>src/app</c> must honour the <c>.gitignore</c> at the repository top, as ripgrep does. Outside a
    /// repository there is nothing to inherit. Returns the scope and the search root's path from the
    /// repository root (empty when the root is the repository).
    /// </summary>
    private async Task<(GitignoreScope Scope, string Prefix)> LoadAncestorScopeAsync(VfsPath root, CancellationToken ct)
    {
        var chain = new List<(VfsPath Path, IReadOnlyList<IFileEntry> Entries)>();
        var current = root;
        var foundRepository = false;

        for (var i = 0; i < MaxAncestorClimb; i++)
        {
            IReadOnlyList<IFileEntry> entries;
            try { entries = await _vfs.ListAsync(current, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { break; }   // cannot read this level: stop climbing, inherit nothing above it

            chain.Add((current, entries));
            if (entries.Any(e => e.Name.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            {
                foundRepository = true;
                break;
            }

            var parent = current.Parent;
            if (parent is null || parent.Value == current.Value)
            {
                break;
            }

            current = parent;
        }

        // The root itself is read again by the walk; only real ancestors matter here.
        if (!foundRepository || chain.Count <= 1)
        {
            return (GitignoreScope.Empty, string.Empty);
        }

        chain.Reverse();   // repository root first, search root last
        var scope = GitignoreScope.Empty;
        var relative = string.Empty;
        for (var i = 0; i < chain.Count - 1; i++)
        {
            if (i > 0)
            {
                var name = chain[i].Path.FileName;
                relative = relative.Length == 0 ? name : relative + "/" + name;
            }
            scope = await PushGitignoreAsync(scope, relative, chain[i].Entries, ct).ConfigureAwait(false);
        }

        var searchRootName = chain[^1].Path.FileName;
        var prefix = relative.Length == 0 ? searchRootName : relative + "/" + searchRootName;
        return (scope, prefix);
    }

    // ── Entry matching (name, size, date) ─────────────────────────────────────

    private static bool MatchesEntry(IFileEntry entry, SearchCriteria c)
    {
        if (!c.NameMatches(entry.Name))
        {
            return false;
        }

        if (!entry.IsDirectory)
        {
            // Size
            if (c.MinSizeBytes.HasValue && entry.Size < c.MinSizeBytes.Value)
            {
                return false;
            }

            if (c.MaxSizeBytes.HasValue && entry.Size > c.MaxSizeBytes.Value)
            {
                return false;
            }
        }

        // Date
        if (c.ModifiedAfter.HasValue && entry.LastModified < c.ModifiedAfter.Value)
        {
            return false;
        }

        if (c.ModifiedBefore.HasValue && entry.LastModified > c.ModifiedBefore.Value)
        {
            return false;
        }

        return true;
    }

    // ── Content search ────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<SearchResult>> SearchContentAsync(
        IFileEntry entry, SearchCriteria criteria, CancellationToken ct)
    {
        if (criteria.MaxContentBytes > 0 && entry.Size > criteria.MaxContentBytes)
        {
            return Array.Empty<SearchResult>();
        }

        // Opening a cloud placeholder makes Windows download it: a grep over a OneDrive folder would
        // pull the whole account. Name search still lists them.
        if ((entry.Attributes & CloudPlaceholder) != 0)
        {
            return Array.Empty<SearchResult>();
        }

        byte[] data;
        try
        {
            await using var stream = await _vfs.OpenReadAsync(entry.Path, ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
            data = buffer.ToArray();
        }
        catch (OperationCanceledException) { throw; }
        catch { return Array.Empty<SearchResult>(); }   // unreadable file — not a search failure

        if (criteria.SkipBinary && TextProbe.LooksBinary(data.AsSpan(0, Math.Min(data.Length, ProbeBytes))))
        {
            return Array.Empty<SearchResult>();
        }

        var encoding = criteria.ForcedEncoding ?? TextProbe.DetectEncoding(data).Encoding;
        string text;
        try { text = TextProbe.DecodeText(data, encoding); }
        catch { return Array.Empty<SearchResult>(); }

        try
        {
            return MatchLines(entry, text, criteria, ct);
        }
        catch (RegexMatchTimeoutException)
        {
            // A pathological pattern on one pathological line: give up on this file, not on the search.
            Interlocked.Increment(ref _timedOut);
            return Array.Empty<SearchResult>();
        }
    }

    /// <summary>
    /// Splits decoded text into lines and reports every hit with its column and context. Pure
    /// apart from the cancellation check, which is what makes the matcher unit-testable.
    /// </summary>
    private static IReadOnlyList<SearchResult> MatchLines(
        IFileEntry entry, string text, SearchCriteria criteria, CancellationToken ct)
    {
        var lines = SplitLines(text);
        var results = new List<SearchResult>();
        var regex = criteria.CompiledContentRegex!;
        var context = Math.Max(0, criteria.ContextLines);

        for (var i = 0; i < lines.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var match = regex.Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }

            results.Add(new SearchResult(
                entry,
                entry.Path.Value,
                i + 1,
                lines[i],
                match.Index,
                match.Length,
                context == 0 ? null : Slice(lines, i - context, i),
                context == 0 ? null : Slice(lines, i + 1, i + 1 + context)));

            if (criteria.MaxMatchesPerFile > 0 && results.Count >= criteria.MaxMatchesPerFile)
            {
                break;
            }
        }

        return results;
    }

    private static IReadOnlyList<string> Slice(IReadOnlyList<string> lines, int from, int toExclusive)
    {
        from = Math.Max(0, from);
        toExclusive = Math.Min(lines.Count, toExclusive);
        if (toExclusive <= from)
        {
            return Array.Empty<string>();
        }

        var slice = new string[toExclusive - from];
        for (var i = 0; i < slice.Length; i++)
        {
            slice[i] = lines[from + i];
        }

        return slice;
    }

    /// <summary>
    /// Splits on LF and drops a trailing CR, so CRLF, LF and CR-only files all report the same
    /// line numbers an editor would show. A trailing newline does not create an empty last line.
    /// </summary>
    private static IReadOnlyList<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
            {
                continue;
            }

            var end = i > start && text[i - 1] == '\r' ? i - 1 : i;
            lines.Add(text[start..end]);
            start = i + 1;
        }
        if (start < text.Length)
        {
            lines.Add(text[start..].TrimEnd('\r'));
        }

        return lines;
    }

    // ── Gitignore ─────────────────────────────────────────────────────────────

    private async Task<GitignoreScope> PushGitignoreAsync(
        GitignoreScope scope, string relativeDir, IReadOnlyList<IFileEntry> entries, CancellationToken ct)
    {
        var file = entries.FirstOrDefault(e =>
            !e.IsDirectory && string.Equals(e.Name, ".gitignore", StringComparison.OrdinalIgnoreCase));
        if (file is null)
        {
            return scope;
        }

        try
        {
            await using var stream = await _vfs.OpenReadAsync(file.Path, ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[MaxGitignoreBytes];
            var read = await reader.ReadAsync(buffer, ct).ConfigureAwait(false);
            return scope.Push(relativeDir, GitignoreRules.Parse(new string(buffer, 0, read)));
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return scope;   // an unreadable .gitignore must not abort the search
        }
    }
}
