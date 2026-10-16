using SharpCompress.Archives;
using SharpCompress.Common;
using TCV.Core.PluginSdk;

namespace TCV.Archives.Engines;

/// <summary>
/// Shared read side of the SharpCompress-backed engines (7z, RAR, TAR, and encrypted ZIP): listing,
/// single-entry extraction, whole-archive and multi-entry extraction, and the password handling
/// that turns the library's assorted "cannot decrypt" failures into one
/// <see cref="ArchivePasswordRequiredException"/> the UI knows how to answer with a prompt.
/// </summary>
public abstract class SharpCompressEngineBase : IArchiveEngine
{
    /// <summary>Largest first file the open-time password probe will decode (bytes).</summary>
    private const long ProbeLimit = 4L * 1024 * 1024;

    internal SharpCompressEngineBase(string? password)
    {
        Password = string.IsNullOrEmpty(password) ? null : password;
    }

    protected string? Password { get; }

    public virtual bool CanWrite => false;

    /// <summary>Opens the archive over <paramref name="stream"/>, which the archive then owns.</summary>
    protected abstract IArchive OpenArchive(Stream stream, SharpCompress.Readers.ReaderOptions options);

    /// <summary>
    /// Opens the file ourselves so the handle is closed even when the library throws from its
    /// constructor (a wrong password does that, and it would otherwise leak the file handle and
    /// keep the archive locked).
    /// </summary>
    private IArchive OpenArchive(string archivePath)
    {
        var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            return OpenArchive(stream, new SharpCompress.Readers.ReaderOptions
            {
                Password = Password,
                LeaveStreamOpen = false,
            });
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// True when a missing password can be recognised from the entry flags alone, so the probe is
    /// only needed for archives that actually contain encrypted entries. 7z does not expose that
    /// flag, so its data has to be tried.
    /// </summary>
    protected virtual bool FlagsAreReliable => true;

    // ── Listing ──────────────────────────────────────────────────────────────

    public IReadOnlyList<ArchiveEntry> ListAll(string archivePath)
    {
        try
        {
            using var archive = OpenArchive(archivePath);
            var builder = new EntryListBuilder();

            foreach (var entry in archive.Entries)
            {
                if (entry.Key is null)
                {
                    continue;
                }

                var date = entry.LastModifiedTime ?? DateTime.Now;
                if (entry.IsDirectory)
                {
                    builder.AddDirectory(entry.Key, date);
                }
                else
                {
                    builder.AddFile(entry.Key, entry.Size, date);
                }
            }

            ProbePassword(archive, archivePath);
            return builder.Build();
        }
        catch (ArchivePasswordRequiredException)
        {
            throw;
        }
        catch (Exception ex) when (IsPasswordFailure(ex))
        {
            throw new ArchivePasswordRequiredException(archivePath, ex);
        }
    }

    /// <summary>
    /// Fails at open time — where the UI can prompt — instead of on the first extraction. Without
    /// a password it reacts to entries flagged as encrypted (or, for 7z, to a "password" failure
    /// while decoding the first small file). With one, a failure to decode that file means the
    /// password was wrong.
    /// </summary>
    private void ProbePassword(IArchive archive, string archivePath)
    {
        var files = archive.Entries.Where(e => !e.IsDirectory && e.Key is not null && e.Size > 0).ToList();
        var flagged = files.FirstOrDefault(e => e.IsEncrypted);

        if (FlagsAreReliable && flagged is null)
        {
            return;
        }

        if (flagged is not null && Password is null)
        {
            throw new ArchivePasswordRequiredException(archivePath);
        }

        // The smallest encrypted entry is the cheapest proof. Without flags (7z) the first file is kept:
        // in a solid block a later file can only be reached by decoding everything before it.
        var probe = FlagsAreReliable
            ? files.Where(e => e.IsEncrypted).MinBy(e => e.Size)
            : files.FirstOrDefault();
        if (probe is null)
        {
            return;
        }

        try
        {
            // Only a bounded prefix is decoded: skipping big entries let a wrong password through,
            // and it was then remembered for the whole session.
            using var entryStream = probe.OpenEntryStream();
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while (read < ProbeLimit && (n = entryStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                read += n;
            }
        }
        catch (Exception ex)
        {
            if (Password is not null || IsPasswordException(ex))
            {
                throw new ArchivePasswordRequiredException(archivePath, ex);
            }
        }
    }

    // ── Single entry ─────────────────────────────────────────────────────────

    public Stream ExtractEntry(string archivePath, string entryPath)
    {
        try
        {
            using var archive = OpenArchive(archivePath);
            var wanted = EntryListBuilder.NormalizePath(entryPath);
            var entry = archive.Entries.FirstOrDefault(e =>
                    e.Key is not null && !e.IsDirectory &&
                    string.Equals(EntryListBuilder.NormalizePath(e.Key), wanted, StringComparison.Ordinal))
                ?? archive.Entries.FirstOrDefault(e =>
                    e.Key is not null && !e.IsDirectory &&
                    string.Equals(EntryListBuilder.NormalizePath(e.Key), wanted, StringComparison.OrdinalIgnoreCase))
                ?? throw new FileNotFoundException($"Entry '{entryPath}' not found in archive.");

            return EntryBuffer.Fill(entry.Size, buffer => entry.WriteTo(buffer));
        }
        catch (Exception ex) when (ex is not ArchivePasswordRequiredException && IsPasswordFailure(ex))
        {
            throw new ArchivePasswordRequiredException(archivePath, ex);
        }
    }

    // ── Bulk extraction ──────────────────────────────────────────────────────

    public void ExtractAll(string archivePath, string destDir, IProgress<double>? progress = null) =>
        ExtractAll(archivePath, destDir, progress, CancellationToken.None);

    public void ExtractAll(string archivePath, string destDir, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(destDir);

        try
        {
            using var archive = OpenArchive(archivePath);
            var total = Math.Max(1, archive.Entries.Count(e => !e.IsDirectory));
            var done = 0;

            // ResolveDestination throws on a zip-slip name before that entry is written.
            foreach (var dir in archive.Entries.Where(e => e.IsDirectory && e.Key is not null))
            {
                ct.ThrowIfCancellationRequested();
                Directory.CreateDirectory(ArchivePathSafety.ResolveDestination(destDir, dir.Key!));
            }

            ForEachFile(archive, key =>
            {
                var dest = ArchivePathSafety.ResolveDestination(destDir, key);
                return content =>
                {
                    WriteFile(dest, content.CopyTo);
                    progress?.Report((double)++done / total);
                };
            }, () => false, ct);
        }
        catch (Exception ex) when (ex is not ArchivePasswordRequiredException && IsPasswordFailure(ex))
        {
            throw new ArchivePasswordRequiredException(archivePath, ex);
        }
    }

    /// <summary>
    /// One pass for the whole batch (see <see cref="ReadEntries"/>). An entry asked for under several
    /// destinations is decoded once and copied; one the archive does not have fails the batch with
    /// <see cref="FileNotFoundException"/> once the rest is out, as the default and ZIP do.
    /// </summary>
    public void ExtractEntries(string archivePath, IReadOnlyList<(string entryPath, string destPath)> entries,
        CancellationToken ct)
    {
        var destinations = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (entryPath, destPath) in entries)
        {
            var key = EntryListBuilder.NormalizePath(entryPath);
            if (!destinations.TryGetValue(key, out var targets))
            {
                destinations[key] = targets = [];
            }

            targets.Add(destPath);
        }

        var missing = destinations.Keys.ToHashSet(StringComparer.Ordinal);
        ReadEntries(archivePath, [.. destinations.Keys], (entryPath, content) =>
        {
            missing.Remove(entryPath);
            var targets = destinations[entryPath];
            WriteFile(targets[0], content.CopyTo);
            foreach (var copy in targets.Skip(1))
            {
                WriteFile(copy, target =>
                {
                    using var first = File.OpenRead(targets[0]);
                    first.CopyTo(target);
                });
            }
        }, ct);

        if (missing.Count > 0)
        {
            throw new FileNotFoundException($"Entry '{missing.First()}' not found in archive.");
        }
    }

    /// <summary>
    /// One pass for the whole batch: a solid archive is decoded once instead of from the start for
    /// every entry, and reading stops after the last entry asked for.
    /// </summary>
    public void ReadEntries(string archivePath, IReadOnlyList<string> entryPaths, Action<string, Stream> consume,
        CancellationToken ct)
    {
        // What the caller throws is the caller's: a disk error while writing a file must not come back
        // dressed as a wrong password. A decoding failure inside the entry is classified by EntryContent.
        var consumerFailed = false;
        try
        {
            using var archive = OpenArchive(archivePath);
            var requests = new EntryRequests(entryPaths,
                archive.Entries.Where(e => !e.IsDirectory && e.Key is not null).Select(e => e.Key!));

            ForEachFile(archive, key => requests.Claim(key) is { } requested
                    ? content =>
                    {
                        try
                        {
                            consume(requested, new EntryContent(content, IsPasswordFailure, archivePath));
                        }
                        catch
                        {
                            consumerFailed = true;
                            throw;
                        }
                    }
                    : null,
                () => requests.Remaining == 0, ct);
        }
        catch (Exception ex) when (!consumerFailed && ex is not ArchivePasswordRequiredException && IsPasswordFailure(ex))
        {
            throw new ArchivePasswordRequiredException(archivePath, ex);
        }
    }

    /// <summary>
    /// Walks the archive's files once, in stored order, and hands the content of each one
    /// <paramref name="handlerFor"/> returns a handler for to that handler. Solid formats and 7z are
    /// read sequentially through a reader, so an N-file batch decodes the stream once instead of N
    /// times. Stops as soon as <paramref name="isDone"/> says so.
    /// </summary>
    private static void ForEachFile(IArchive archive, Func<string, Action<Stream>?> handlerFor, Func<bool> isDone,
        CancellationToken ct)
    {
        if (archive.IsSolid || archive.Type == ArchiveType.SevenZip)
        {
            using var reader = archive.ExtractAllEntries();
            while (!isDone() && reader.MoveToNextEntry())
            {
                ct.ThrowIfCancellationRequested();
                var entry = reader.Entry;
                if (entry.IsDirectory || entry.Key is null || handlerFor(entry.Key) is not { } handle)
                {
                    continue;
                }

                using var content = reader.OpenEntryStream();
                try
                {
                    handle(content);
                }
                catch
                {
                    // Otherwise closing the entry stream decodes the rest of the entry first, and a
                    // cancelled extraction of a big file would wait for exactly that.
                    reader.Cancel();
                    throw;
                }
            }

            return;
        }

        foreach (var entry in archive.Entries)
        {
            if (isDone())
            {
                return;
            }

            ct.ThrowIfCancellationRequested();
            if (entry.IsDirectory || entry.Key is null || handlerFor(entry.Key) is not { } handle)
            {
                continue;
            }

            using var content = entry.OpenEntryStream();
            handle(content);
        }
    }

    /// <summary>
    /// An entry's content as a <see cref="ReadEntries"/> caller reads it. A decoding failure that means
    /// a missing or wrong password surfaces as <see cref="ArchivePasswordRequiredException"/>, as it does
    /// from <see cref="ExtractEntry"/>. The pass disposes the entry stream underneath.
    /// </summary>
    private sealed class EntryContent(Stream inner, Func<Exception, bool> isPasswordFailure, string archivePath) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            try
            {
                return inner.Read(buffer, offset, count);
            }
            catch (Exception ex) when (ex is not ArchivePasswordRequiredException && isPasswordFailure(ex))
            {
                throw new ArchivePasswordRequiredException(archivePath, ex);
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static void WriteFile(string destPath, Action<Stream> write)
    {
        var directory = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var target = File.Create(destPath);
        write(target);
    }

    // ── Writing is opt-in ────────────────────────────────────────────────────

    public virtual void Pack(string archivePath, IEnumerable<(string localPath, string entryName)> files) =>
        throw new NotSupportedException("Writing this archive format is not supported. Use ZIP instead.");

    public virtual void AddFiles(string archivePath, IEnumerable<(string localPath, string entryName)> files) =>
        throw new NotSupportedException("Adding to this archive format is not supported. Use ZIP instead.");

    public virtual void DeleteEntry(string archivePath, string entryPath) =>
        throw new NotSupportedException("Deleting entries from this archive format is not supported.");

    // ── Password classification ──────────────────────────────────────────────

    /// <summary>
    /// A decode failure once a password was supplied is almost always that password being wrong, so it
    /// is reported as a password problem and the user is asked again.
    /// </summary>
    private bool IsPasswordFailure(Exception ex) =>
        IsPasswordException(ex) || (Password is not null && IsDecodeFailure(ex));

    /// <summary>
    /// What a wrong key makes of the data. AES with a bad key hands the decompressor random bytes, and
    /// a 7z with readable names has no password check value, so the failure comes from wherever those
    /// bytes happen to break the decoder: usually LZMA's "data error", but now and then an LZMA2 chunk
    /// with an impossible properties byte (<c>InvalidParamException</c>) or a chunk size that runs past
    /// the end of the data (<c>IncompleteArchiveException</c>; <see cref="EndOfStreamException"/> in older
    /// versions). Only the first was recognised, and about one wrong password in seventy came back as a
    /// bare decoder error instead of a prompt (6.78, TRAP-80). Every exception the library raises derives
    /// from <c>SharpCompressException</c>, its internal ones included, so the base type covers them all.
    /// </summary>
    internal static bool IsDecodeFailure(Exception ex) =>
        ex is SharpCompress.Common.SharpCompressException or InvalidOperationException or InvalidDataException
            or EndOfStreamException;

    internal static bool IsPasswordException(Exception ex) =>
        ex is SharpCompress.Common.CryptographicException ||
        // Not a disk error: its message carries a path, and a folder called "encrypted" is not a password prompt.
        (ex is not (IOException or UnauthorizedAccessException) &&
            (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase) ||
             ex.Message.Contains("encrypted", StringComparison.OrdinalIgnoreCase)));
}
