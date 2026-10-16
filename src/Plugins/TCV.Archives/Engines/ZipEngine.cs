using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Readers;
using TCV.Core.PluginSdk;

namespace TCV.Archives.Engines;

/// <summary>
/// ZIP engine. Plain archives use System.IO.Compression (no native dependencies, full write
/// support). An archive with encrypted entries is read through SharpCompress instead, which
/// understands ZipCrypto and WinZip AES — the BCL cannot decrypt either.
/// </summary>
public sealed class ZipEngine : IArchiveEngine
{
    private readonly string? _password;

    public ZipEngine(string? password = null)
    {
        _password = string.IsNullOrEmpty(password) ? null : password;
    }

    public bool CanWrite => true;

    // ── Reading ──────────────────────────────────────────────────────────────

    public IReadOnlyList<ArchiveEntry> ListAll(string archivePath)
    {
        var builder = new EntryListBuilder();
        using (var zip = ZipFile.OpenRead(archivePath))
        {
            foreach (var ze in zip.Entries)
            {
                var raw = ze.FullName.Replace('\\', '/');
                if (string.IsNullOrEmpty(raw))
                {
                    continue;
                }

                if (raw.EndsWith('/'))
                {
                    builder.AddDirectory(raw, ze.LastWriteTime.LocalDateTime);
                }
                else
                {
                    builder.AddFile(raw, ze.Length, ze.LastWriteTime.LocalDateTime);
                }
            }
        }

        // Names of an encrypted ZIP are readable, so the panel would open fine and only fail on the
        // first file. Ask for the password here, where the UI can prompt for it.
        if (IsEncrypted(archivePath))
        {
            Secure().ListAll(archivePath);
        }

        return builder.Build();
    }

    public Stream ExtractEntry(string archivePath, string entryPath)
    {
        if (_password is not null || IsEncrypted(archivePath))
        {
            return Secure().ExtractEntry(archivePath, entryPath);
        }

        // Copy to a buffer so the zip can be closed before the caller reads.
        using var zip = ZipFile.OpenRead(archivePath);
        var wanted = EntryListBuilder.NormalizePath(entryPath);
        var entry = zip.GetEntry(entryPath)
            ?? zip.Entries.FirstOrDefault(e => Matches(e, wanted, StringComparison.Ordinal))
            ?? zip.Entries.FirstOrDefault(e => Matches(e, wanted, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"Entry '{entryPath}' not found in ZIP.");

        return EntryBuffer.Fill(entry.Length, buffer =>
        {
            using var entryStream = entry.Open();
            entryStream.CopyTo(buffer);
        });
    }

    public void ExtractAll(string archivePath, string destDir, IProgress<double>? progress = null) =>
        ExtractAll(archivePath, destDir, progress, CancellationToken.None);

    public void ExtractAll(string archivePath, string destDir, IProgress<double>? progress, CancellationToken ct)
    {
        if (_password is not null || IsEncrypted(archivePath))
        {
            Secure().ExtractAll(archivePath, destDir, progress, ct);
            return;
        }

        using var zip = ZipFile.OpenRead(archivePath);
        int total = Math.Max(1, zip.Entries.Count), done = 0;

        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var dest = ArchivePathSafety.ResolveDestination(destDir, entry.FullName);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(dest);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
            }

            progress?.Report((double)++done / total);
        }
    }

    public void ExtractEntries(string archivePath, IReadOnlyList<(string entryPath, string destPath)> entries,
        CancellationToken ct)
    {
        if (_password is not null || IsEncrypted(archivePath))
        {
            Secure().ExtractEntries(archivePath, entries, ct);
            return;
        }

        // One open for the whole batch: going through ExtractEntry per item would re-read the central
        // directory (twice, with the encryption probe) for every file, which is quadratic in the archive size.
        using var zip = ZipFile.OpenRead(archivePath);
        foreach (var (entryPath, destPath) in entries)
        {
            ct.ThrowIfCancellationRequested();
            var wanted = EntryListBuilder.NormalizePath(entryPath);
            var entry = zip.GetEntry(entryPath)
                ?? zip.Entries.FirstOrDefault(e => Matches(e, wanted, StringComparison.Ordinal))
                ?? zip.Entries.FirstOrDefault(e => Matches(e, wanted, StringComparison.OrdinalIgnoreCase))
                ?? throw new FileNotFoundException($"Entry '{entryPath}' not found in ZIP.");

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            using var source = entry.Open();
            using var target = File.Create(destPath);
            source.CopyTo(target);
        }
    }

    public void ReadEntries(string archivePath, IReadOnlyList<string> entryPaths, Action<string, Stream> consume,
        CancellationToken ct)
    {
        if (_password is not null || IsEncrypted(archivePath))
        {
            Secure().ReadEntries(archivePath, entryPaths, consume, ct);
            return;
        }

        // One open for the whole batch, read in stored order (the way through the file).
        using var zip = ZipFile.OpenRead(archivePath);
        var files = zip.Entries.Where(e => !e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\')).ToList();
        var requests = new EntryRequests(entryPaths, files.Select(e => e.FullName));
        foreach (var entry in files)
        {
            if (requests.Remaining == 0)
            {
                return;
            }

            ct.ThrowIfCancellationRequested();
            if (requests.Claim(entry.FullName) is not { } requested)
            {
                continue;
            }

            using var content = entry.Open();
            consume(requested, content);
        }
    }

    // ── Writing ──────────────────────────────────────────────────────────────
    //
    // An existing archive is never edited in place. ZipArchiveMode.Update rewrites the file it was
    // opened on when it is disposed, and a failure in the middle of that (a full disk, a crash, an
    // unreadable source) used to leave the only copy of the archive broken. Every edit goes to a copy
    // beside the archive, and the copy replaces it only once it is complete (see EditThroughScratch).

    public void DeleteEntry(string archivePath, string entryPath) =>
        DeleteEntries(archivePath, [entryPath], CancellationToken.None);

    /// <summary>Deletes every entry in <paramref name="entryPaths"/> (a directory takes its contents along) in one rewrite.</summary>
    public void DeleteEntries(string archivePath, IReadOnlyList<string> entryPaths, CancellationToken ct)
    {
        // Update mode would create the file it is told to open; a delete has nothing to create.
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException($"Archive '{archivePath}' not found.", archivePath);
        }

        // Nothing to delete is not worth a copy and a rewrite of the whole archive.
        if (entryPaths.Count == 0)
        {
            return;
        }

        EditThroughScratch(archivePath, ct, zip =>
        {
            // Normalised once: a batch of deletes in a big archive would otherwise normalise every
            // entry name again for every path.
            var entries = zip.Entries
                .Select(e => (Entry: e, Name: EntryListBuilder.NormalizePath(e.FullName)))
                .ToList();

            foreach (var entryPath in entryPaths)
            {
                ct.ThrowIfCancellationRequested();
                var wanted = EntryListBuilder.NormalizePath(entryPath);

                // Two names that differ only by case are distinct entries in a ZIP: match exactly first,
                // and fall back to the case-insensitive reading only when nothing matches that way.
                var doomed = entries.Where(e => IsOrIsBelow(e.Name, wanted, StringComparison.Ordinal)).ToList();
                if (doomed.Count == 0)
                {
                    doomed = entries.Where(e => IsOrIsBelow(e.Name, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
                }

                if (doomed.Count == 0)
                {
                    continue;
                }

                var gone = doomed.Select(e => e.Entry).ToHashSet();
                foreach (var entry in gone)
                {
                    entry.Delete();
                }

                entries.RemoveAll(e => gone.Contains(e.Entry));
            }
        });
    }

    /// <summary>
    /// Renames entries in one rewrite, all or nothing (<see cref="ArchiveRenamePlan"/> says which and to
    /// what). A ZIP entry cannot change its name in place through the BCL, so each one that changes is
    /// stored again under the new name — decompressed and compressed anew, stored entries kept stored —
    /// with its time, attributes and comment, and the old one goes. An encrypted entry cannot be read
    /// back by the BCL, so a batch that touches one is refused before anything is rewritten.
    /// </summary>
    public void RenameEntries(string archivePath, IReadOnlyList<(string EntryPath, string NewName)> renames,
        CancellationToken ct)
    {
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException($"Archive '{archivePath}' not found.", archivePath);
        }

        if (renames.Count == 0)
        {
            return;
        }

        EditThroughScratch(archivePath, ct, zip =>
        {
            var entries = zip.Entries.ToList();
            var plan = ArchiveRenamePlan.Build(entries.Select(e => e.FullName).ToList(), renames);
            if (plan.Changes.Select(c => entries[c.Index]).FirstOrDefault(e => e.IsEncrypted) is { } locked)
            {
                throw new ArchiveEntryEncryptedException(locked.FullName);
            }

            foreach (var (index, storedName) in plan.Changes)
            {
                ct.ThrowIfCancellationRequested();
                var old = entries[index];
                var stored = old.Length > 0 && old.CompressedLength >= old.Length;
                var copy = storedName.EndsWith('/')
                    ? zip.CreateEntry(storedName)
                    : zip.CreateEntry(storedName, stored ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                copy.LastWriteTime = old.LastWriteTime;
                copy.ExternalAttributes = old.ExternalAttributes;
                copy.Comment = old.Comment;

                if (!storedName.EndsWith('/'))
                {
                    using var source = old.Open();
                    using var target = copy.Open();
                    source.CopyTo(target);
                }

                old.Delete();
            }
        });
    }

    public void AddFiles(string archivePath, IEnumerable<(string localPath, string entryName)> files)
        => AddFiles(archivePath, files, CompressionLevel.Optimal, CancellationToken.None);

    public void AddFiles(string archivePath, IEnumerable<(string localPath, string entryName)> files,
        CancellationToken ct)
        => AddFiles(archivePath, files, CompressionLevel.Optimal, ct);

    public void AddFiles(string archivePath, IEnumerable<(string localPath, string entryName)> files,
        CompressionLevel compressionLevel, CancellationToken ct) =>
        EditThroughScratch(archivePath, ct, zip =>
        {
            foreach (var (localPath, entryName) in files)
            {
                ct.ThrowIfCancellationRequested();
                if (entryName.EndsWith('/'))
                {
                    if (zip.GetEntry(entryName) is null)
                    {
                        zip.CreateEntry(entryName);
                    }

                    continue;
                }

                // The source is opened before the old entry goes, so a file that cannot be read (locked
                // by another program) fails the edit before anything in the archive has changed.
                using var source = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                zip.GetEntry(entryName)?.Delete();
                var entry = zip.CreateEntry(entryName, compressionLevel);
                var modified = File.GetLastWriteTime(localPath);
                if (modified.Year is >= 1980 and <= 2107)
                {
                    entry.LastWriteTime = modified;   // outside the range a ZIP can hold, the entry keeps "now"
                }

                using var target = entry.Open();
                source.CopyTo(target);
            }
        });

    /// <summary>
    /// Applies <paramref name="edit"/> to a copy of the archive in the same folder and moves the copy
    /// over the original once it is complete. A failed or cancelled edit deletes the copy, and the
    /// archive stays exactly as it was. The price is room for a second copy on the same drive.
    /// A missing archive is started empty, as Update mode would have done.
    /// </summary>
    private static void EditThroughScratch(string archivePath, CancellationToken ct, Action<ZipArchive> edit)
    {
        ct.ThrowIfCancellationRequested();
        var target = Path.GetFullPath(archivePath);

        // A symlinked archive is edited where it really lives, as Update mode did through the link:
        // moving the copy over the link would turn it into a file and leave the real archive unchanged.
        var link = new FileInfo(target);
        if (link.LinkTarget is not null && link.ResolveLinkTarget(returnFinalTarget: true) is { } real)
        {
            target = real.FullName;
        }

        var exists = File.Exists(target);

        // Refused up front, as opening it for writing used to refuse it: the copy would inherit the
        // attribute, and a read-only file cannot be replaced by a move either.
        if (exists && File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly))
        {
            throw new UnauthorizedAccessException($"Access to the path '{target}' is denied.");
        }

        var scratch = ScratchPathFor(target);
        try
        {
            if (exists)
            {
                File.Copy(target, scratch);
            }

            using (var stream = new FileStream(scratch, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                // Disposed only on success. In Update mode ZipArchive writes the whole archive out when it
                // is disposed: for a cancelled or failed edit that is a full rewrite of a copy about to be
                // deleted (Cancel waiting as long as finishing would), and a failure there would replace
                // the real error. It holds nothing but memory and this stream, which is closed regardless.
                var zip = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true);
                edit(zip);
                ct.ThrowIfCancellationRequested();
                zip.Dispose();
            }

            ct.ThrowIfCancellationRequested();
            File.Move(scratch, target, overwrite: true);
        }
        catch
        {
            TryDelete(scratch);
            throw;
        }
    }

    /// <summary>The same scratch name <c>PackJob</c> uses: beside the archive, dot-prefixed, extension kept.</summary>
    private static string ScratchPathFor(string archivePath) =>
        Path.Combine(
            Path.GetDirectoryName(archivePath)!,
            $".tcv-{Guid.NewGuid():N}"[..14] + "-" + Path.GetFileName(archivePath));

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a stray scratch file must not mask the error that got us here.
        }
    }

    public void Pack(string archivePath, IEnumerable<(string localPath, string entryName)> files)
        => Pack(archivePath, files, CompressionLevel.Optimal, CancellationToken.None);

    public void Pack(string archivePath, IEnumerable<(string localPath, string entryName)> files,
        CompressionLevel compressionLevel, CancellationToken ct)
    {
        var completed = false;

        // Create mode refuses an existing file: that file is not ours to delete when the open fails.
        var existedBefore = File.Exists(archivePath);
        try
        {
            using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                foreach (var (localPath, entryName) in files)
                {
                    ct.ThrowIfCancellationRequested();
                    AddEntry(zip, localPath, entryName, compressionLevel);
                }
            }

            completed = true;
        }
        finally
        {
            // A cancelled or failed pack must not leave a half-written archive behind.
            if (!completed && !existedBefore)
            {
                try
                {
                    File.Delete(archivePath);
                }
                catch (IOException)
                {
                    // The caller reports the original failure.
                }
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void AddEntry(ZipArchive zip, string localPath, string entryName, CompressionLevel level)
    {
        if (entryName.EndsWith('/'))
        {
            // An empty directory: a zero-length entry whose name ends in a slash.
            zip.CreateEntry(entryName);
            return;
        }

        zip.CreateEntryFromFile(localPath, entryName, level);
    }

    private static bool Matches(ZipArchiveEntry entry, string normalizedPath, StringComparison comparison) =>
        string.Equals(EntryListBuilder.NormalizePath(entry.FullName), normalizedPath, comparison);

    /// <summary>True for the entry named <paramref name="wanted"/> and for everything inside it as a directory.</summary>
    private static bool IsOrIsBelow(string normalizedName, string wanted, StringComparison comparison) =>
        string.Equals(normalizedName, wanted, comparison)
        || (normalizedName.Length > wanted.Length
            && normalizedName[wanted.Length] == '/'
            && normalizedName.StartsWith(wanted, comparison));

    private SharpCompressEngineBase Secure() => new ZipSecureEngine(_password);

    /// <summary>True when any entry is encrypted (general-purpose flag bit 0).</summary>
    private static bool IsEncrypted(string archivePath)
    {
        // The stream is ours and closed here: an archive opened by path with default options leaves the
        // file it opened open (SharpCompress 0.48), and the archive then stayed locked (TRAP-79).
        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var archive = SharpCompress.Archives.Zip.ZipArchive.OpenArchive(stream, new ReaderOptions { LeaveStreamOpen = true });
        return archive.Entries.Any(e => !e.IsDirectory && e.IsEncrypted);
    }

    /// <summary>SharpCompress-backed reader used only for encrypted ZIPs.</summary>
    private sealed class ZipSecureEngine : SharpCompressEngineBase
    {
        public ZipSecureEngine(string? password) : base(password)
        {
        }

        protected override IArchive OpenArchive(Stream stream, ReaderOptions options) =>
            SharpCompress.Archives.Zip.ZipArchive.OpenArchive(stream, options);
    }
}
