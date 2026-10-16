using System.Security.Cryptography;
using System.Text;

namespace TCV.Tools;

public enum ChecksumAlgorithm { MD5, SHA1, SHA256, SHA512, CRC32 }

public sealed class ChecksumResult
{
    public string FilePath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public long FileSize { get; init; }
    public string Hash { get; init; } = string.Empty;
    public ChecksumAlgorithm Algorithm { get; init; }
}

/// <summary>Where a multi-file checksum run stands.</summary>
/// <param name="FileIndex">Zero-based index of the file being hashed (equals the total when finished).</param>
/// <param name="TotalFiles">How many files are in the run.</param>
/// <param name="BytesDone">Bytes of the current file hashed so far.</param>
/// <param name="CurrentFileSize">Size of the current file, or 0 when not known yet.</param>
/// <param name="FileName">Name of the current file.</param>
public readonly record struct ChecksumProgress(int FileIndex, int TotalFiles, long BytesDone, long CurrentFileSize, string FileName);

/// <summary>An <see cref="IProgress{T}"/> that runs on the reporting thread (unlike <see cref="Progress{T}"/>, which posts).</summary>
internal sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}

public sealed class ChecksumEngine
{
    /// <summary>Computes the checksum for a single file.</summary>
    public async Task<string> ComputeAsync(
        string filePath,
        ChecksumAlgorithm algorithm,
        IProgress<long>? progress = null,
        CancellationToken ct = default)
    {
        using var hashAlg = CreateAlgorithm(algorithm);
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 81920, useAsync: true);

        var buffer = new byte[81920];
        long bytesRead = 0;
        int read;

        // ConfigureAwait(false): the hashing between reads belongs on the thread pool, not on the
        // UI thread that happened to call in — a large file would otherwise keep the window busy.
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            hashAlg.TransformBlock(buffer, 0, read, null, 0);
            bytesRead += read;
            progress?.Report(bytesRead);
        }

        hashAlg.TransformFinalBlock(buffer, 0, 0);
        return Convert.ToHexString(hashAlg.Hash!).ToLowerInvariant();
    }

    /// <summary>Computes checksums for multiple files, yielding results as they complete.</summary>
    public async IAsyncEnumerable<ChecksumResult> ComputeManyAsync(
        IReadOnlyList<string> filePaths,
        ChecksumAlgorithm algorithm,
        IProgress<(int fileIndex, int totalFiles)>? progress = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var adapter = progress is null
            ? null
            : new InlineProgress<ChecksumProgress>(p => progress.Report((p.FileIndex, p.TotalFiles)));

        await foreach (var result in ComputeManyWithProgressAsync(filePaths, algorithm, adapter, ct).ConfigureAwait(false))
        {
            yield return result;
        }
    }

    /// <summary>
    /// As <see cref="ComputeManyAsync"/>, but reports byte-level progress inside each file — a single
    /// multi-gigabyte file otherwise sits at 0 % for minutes. Reports are throttled, so a fast disk
    /// does not flood the UI thread with thousands of updates per second.
    /// </summary>
    public async IAsyncEnumerable<ChecksumResult> ComputeManyWithProgressAsync(
        IReadOnlyList<string> filePaths,
        ChecksumAlgorithm algorithm,
        IProgress<ChecksumProgress>? progress = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        for (int i = 0; i < filePaths.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var path = filePaths[i];
            var index = i;
            progress?.Report(new ChecksumProgress(index, filePaths.Count, 0, 0, Path.GetFileName(path)));

            string hash;
            long size;
            try
            {
                var fi = new FileInfo(path);
                size = fi.Exists ? fi.Length : 0;

                var clock = System.Diagnostics.Stopwatch.StartNew();
                var lastReport = TimeSpan.Zero;
                var bytes = progress is null
                    ? null
                    : new InlineProgress<long>(done =>
                    {
                        if (clock.Elapsed - lastReport < TimeSpan.FromMilliseconds(100))
                        {
                            return;
                        }

                        lastReport = clock.Elapsed;
                        progress.Report(new ChecksumProgress(index, filePaths.Count, done, size, Path.GetFileName(path)));
                    });

                hash = await ComputeAsync(path, algorithm, bytes, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                hash = $"[ERROR: {ex.Message}]";
                size = 0;
            }

            yield return new ChecksumResult
            {
                FilePath = path,
                FileName = Path.GetFileName(path),
                FileSize = size,
                Hash = hash,
                Algorithm = algorithm,
            };
        }

        progress?.Report(new ChecksumProgress(filePaths.Count, filePaths.Count, 0, 0, string.Empty));
    }

    /// <summary>Writes an .sfv file: <c>filename checksum</c> per line.</summary>
    public async Task WriteSfvFileAsync(string outputPath, IReadOnlyList<ChecksumResult> results, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("; SFV file generated by TCVibe");
        sb.AppendLine($"; {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        foreach (var r in results)
        {
            // An .sfv line is a CRC-32; any other digest in it makes the file unusable for every SFV tool.
            if (r.Algorithm != ChecksumAlgorithm.CRC32)
            {
                throw new ArgumentException($"An SFV file holds CRC32 checksums, not {r.Algorithm}.", nameof(results));
            }

            sb.AppendLine($"{r.FileName} {r.Hash}");
        }

        await File.WriteAllTextAsync(outputPath, sb.ToString(), Encoding.UTF8, ct);
    }

    /// <summary>
    /// Writes a BSD-style checksum file: <c>ALGO (filename) = hash</c> per line, each line
    /// labelled by that result's own algorithm. Serves .md5/.sha1/.sha256/.sha512 alike.
    /// </summary>
    public async Task WriteChecksumFileAsync(string outputPath, IReadOnlyList<ChecksumResult> results, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("; Generated by TCVibe");
        foreach (var r in results)
            sb.AppendLine($"{Label(r.Algorithm)} ({r.FileName}) = {r.Hash}");

        await File.WriteAllTextAsync(outputPath, sb.ToString(), Encoding.UTF8, ct);
    }

    /// <summary>Loads and verifies checksums from an existing .md5 file (kept for compatibility).</summary>
    public Task<IReadOnlyList<VerifyResult>> VerifyMd5FileAsync(
        string md5FilePath, CancellationToken ct = default) => VerifyChecksumFileAsync(md5FilePath, ct);

    /// <summary>
    /// Loads and verifies an existing checksum file. Handles .md5/.sha1/.sha256/.sha512 (BSD or
    /// GNU layout) and .sfv (CRC-32); the algorithm of each line is taken from its label or, for
    /// SFV/GNU lines, inferred from the digest length.
    /// </summary>
    public async Task<IReadOnlyList<VerifyResult>> VerifyChecksumFileAsync(
        string checksumFilePath, CancellationToken ct = default)
    {
        var lines = await File.ReadAllLinesAsync(checksumFilePath, ct);
        var dir = Path.GetDirectoryName(checksumFilePath) ?? string.Empty;
        var results = new List<VerifyResult>();

        foreach (var line in lines)
        {
            ct.ThrowIfCancellationRequested();
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(';') || trimmed.StartsWith('#')) continue;

            if (!TryParseChecksumLine(trimmed, out var fileName, out var expectedHash, out var algorithm))
                continue;

            var filePath = Path.Combine(dir, fileName);

            string status;
            string actualHash = string.Empty;
            if (!File.Exists(filePath))
            {
                status = "Missing";
            }
            else
            {
                try
                {
                    actualHash = await ComputeAsync(filePath, algorithm, null, ct).ConfigureAwait(false);
                    status = string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase)
                        ? "OK" : "FAILED";
                }
                catch
                {
                    status = "Error";
                }
            }

            results.Add(new VerifyResult
            {
                FileName = fileName,
                ExpectedHash = expectedHash,
                ActualHash = actualHash,
                Status = status,
            });
        }

        return results;
    }

    private static HashAlgorithm CreateAlgorithm(ChecksumAlgorithm algorithm) => algorithm switch
    {
        ChecksumAlgorithm.MD5 => System.Security.Cryptography.MD5.Create(),
        ChecksumAlgorithm.SHA1 => System.Security.Cryptography.SHA1.Create(),
        ChecksumAlgorithm.SHA256 => System.Security.Cryptography.SHA256.Create(),
        ChecksumAlgorithm.SHA512 => System.Security.Cryptography.SHA512.Create(),
        ChecksumAlgorithm.CRC32 => new Crc32HashAlgorithm(),
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
    };

    /// <summary>The label written into BSD-style checksum files, e.g. <c>SHA1 (name) = hash</c>.</summary>
    public static string Label(ChecksumAlgorithm algorithm) => algorithm switch
    {
        ChecksumAlgorithm.MD5 => "MD5",
        ChecksumAlgorithm.SHA1 => "SHA1",
        ChecksumAlgorithm.SHA256 => "SHA256",
        ChecksumAlgorithm.SHA512 => "SHA512",
        ChecksumAlgorithm.CRC32 => "CRC32",
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
    };

    /// <summary>The conventional file extension (without dot) for a checksum file of this algorithm.</summary>
    public static string Extension(ChecksumAlgorithm algorithm) => algorithm switch
    {
        ChecksumAlgorithm.MD5 => "md5",
        ChecksumAlgorithm.SHA1 => "sha1",
        ChecksumAlgorithm.SHA256 => "sha256",
        ChecksumAlgorithm.SHA512 => "sha512",
        ChecksumAlgorithm.CRC32 => "sfv",
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
    };

    /// <summary>Extensions of the checksum files <see cref="VerifyChecksumFileAsync"/> reads (and the dialog writes).</summary>
    private static readonly HashSet<string> ChecksumExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md5", ".sha1", ".sha256", ".sha512", ".sfv",
    };

    /// <summary>The coreutils names a release ships its digests under: <c>SHA256SUMS</c> and kin, no extension.</summary>
    private static readonly HashSet<string> ChecksumListNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "MD5SUMS", "SHA1SUMS", "SHA256SUMS", "SHA512SUMS",
    };

    /// <summary>
    /// True when <paramref name="fileName"/> names a checksum list this engine can verify:
    /// <c>.md5/.sha1/.sha256/.sha512/.sfv</c>, or a coreutils <c>SHA256SUMS</c>-style file. Hashing such
    /// a file is almost never what the user meant; checking the files it lists is.
    /// </summary>
    public static bool IsChecksumFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var name = Path.GetFileName(fileName.Trim());
        return ChecksumExtensions.Contains(Path.GetExtension(name)) || ChecksumListNames.Contains(name);
    }

    private static ChecksumAlgorithm? AlgorithmFromLabel(string label) => label.ToUpperInvariant() switch
    {
        "MD5" => ChecksumAlgorithm.MD5,
        "SHA1" => ChecksumAlgorithm.SHA1,
        "SHA256" => ChecksumAlgorithm.SHA256,
        "SHA512" => ChecksumAlgorithm.SHA512,
        "CRC32" => ChecksumAlgorithm.CRC32,
        _ => null,
    };

    /// <summary>Infers the algorithm from a hex digest length (CRC32=8, MD5=32, SHA1=40, SHA256=64, SHA512=128).</summary>
    private static ChecksumAlgorithm? AlgorithmFromHash(string token)
    {
        if (token.Length == 0 || !token.All(Uri.IsHexDigit)) return null;
        return token.Length switch
        {
            8 => ChecksumAlgorithm.CRC32,
            32 => ChecksumAlgorithm.MD5,
            40 => ChecksumAlgorithm.SHA1,
            64 => ChecksumAlgorithm.SHA256,
            128 => ChecksumAlgorithm.SHA512,
            _ => null,
        };
    }

    /// <summary>
    /// Parses one line of a checksum file into (name, hash, algorithm). Understands the BSD
    /// style (<c>ALGO (name) = hash</c>), the SFV style (<c>name hash</c>, hash last), and the
    /// GNU coreutils style (<c>hash  name</c>, hash first). SFV/GNU lines infer the algorithm
    /// from the digest length, so files this app never wrote still verify.
    /// </summary>
    internal static bool TryParseChecksumLine(
        string line, out string fileName, out string hash, out ChecksumAlgorithm algorithm)
    {
        fileName = string.Empty;
        hash = string.Empty;
        algorithm = ChecksumAlgorithm.MD5;

        var bsd = System.Text.RegularExpressions.Regex.Match(
            line, @"^(MD5|SHA1|SHA256|SHA512|CRC32)\s+\((.+)\)\s*=\s*([0-9a-fA-F]+)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (bsd.Success && AlgorithmFromLabel(bsd.Groups[1].Value) is { } labelled)
        {
            algorithm = labelled;
            fileName = bsd.Groups[2].Value;
            hash = bsd.Groups[3].Value.ToLowerInvariant();
            return true;
        }

        // GNU coreutils (checked before SFV): "hash  filename" or "hash *filename" — the canonical
        // two-space / space-star separator disambiguates it from an SFV line whose own filename
        // happens to be a bare hex string of digest length.
        var gnu = System.Text.RegularExpressions.Regex.Match(
            line, @"^([0-9a-fA-F]+) [ *](.+)$");
        if (gnu.Success && AlgorithmFromHash(gnu.Groups[1].Value) is { } head)
        {
            algorithm = head;
            hash = gnu.Groups[1].Value.ToLowerInvariant();
            fileName = gnu.Groups[2].Value.Trim();
            return true;
        }

        // SFV / app-written: "filename ... hash" — the hash is the last whitespace-separated token,
        // so a filename with spaces stays intact.
        var lastSpace = line.LastIndexOfAny(new[] { ' ', '\t' });
        if (lastSpace > 0 && AlgorithmFromHash(line[(lastSpace + 1)..].Trim()) is { } tail)
        {
            algorithm = tail;
            hash = line[(lastSpace + 1)..].Trim().ToLowerInvariant();
            fileName = line[..lastSpace].Trim();
            return true;
        }

        // GNU coreutils with a single-space separator (non-canonical, best effort): "hash filename".
        var firstSpace = line.IndexOfAny(new[] { ' ', '\t' });
        if (firstSpace > 0 && AlgorithmFromHash(line[..firstSpace].Trim()) is { } single)
        {
            algorithm = single;
            hash = line[..firstSpace].Trim().ToLowerInvariant();
            fileName = line[(firstSpace + 1)..].Trim().TrimStart('*');
            return true;
        }

        return false;
    }

    /// <summary>
    /// CRC-32 (IEEE 802.3, reflected, poly 0xEDB88320) as a <see cref="HashAlgorithm"/> so it
    /// streams through the same <see cref="ComputeAsync"/> path as the SHA family. This is what a
    /// real .sfv file stores.
    /// </summary>
    private sealed class Crc32HashAlgorithm : HashAlgorithm
    {
        private static readonly uint[] Table = BuildTable();
        private uint _crc = 0xFFFFFFFF;

        private static uint[] BuildTable()
        {
            const uint poly = 0xEDB88320u;
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                var c = i;
                for (var k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? poly ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }

        public override void Initialize() => _crc = 0xFFFFFFFF;

        protected override void HashCore(byte[] array, int ibStart, int cbSize)
        {
            for (var i = ibStart; i < ibStart + cbSize; i++)
                _crc = Table[(_crc ^ array[i]) & 0xFF] ^ (_crc >> 8);
        }

        protected override byte[] HashFinal()
        {
            var value = _crc ^ 0xFFFFFFFF;
            // Big-endian so Convert.ToHexString yields the conventional CRC digit order.
            return new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };
        }
    }
}

public sealed class VerifyResult
{
    public string FileName { get; init; } = string.Empty;
    public string ExpectedHash { get; init; } = string.Empty;
    public string ActualHash { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;

    public bool IsOk => Status == "OK";
}
