using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using TCV.Core.Domain;
using TCV.Network.Cloud;

namespace TCV.Network;

/// <summary>Uploads: a body collected in a temporary file, then one Put Blob or a list of blocks.</summary>
public sealed partial class AzureBlobFileSystem
{
    /// <summary>The most blocks one blob may be committed from.</summary>
    private const int MaxBlocks = 50_000;

    /// <summary>
    /// A write stream that uploads its contents when it is committed or disposed. A Put Blob states its
    /// length up front, so the body goes to a temporary file first (not to memory: a multi-gigabyte
    /// upload must not become a multi-gigabyte array). An abandoned or cancelled body is never sent,
    /// and without <paramref name="overwrite"/> an existing blob is never replaced — refused here when
    /// it is already there, and by the service (<c>If-None-Match: *</c>) when it appeared meanwhile.
    /// </summary>
    public async Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite = false, CancellationToken ct = default)
    {
        var location = RequireBlob(path);
        var name = LeafName(location.Blob);

        // Asked before anything is collected: refusing after gigabytes went to the temporary file
        // would waste all of that.
        if (!overwrite && await GetPropertiesAsync(location.Container!, location.Blob, name, ct) is not null)
        {
            throw AlreadyExists(name);
        }

        return new CloudUploadStream(
            (body, progress, token) => UploadAsync(location, body, overwrite, progress, token), ct);
    }

    private Task UploadAsync(
        AzureBlobLocation location, FileStream body, bool overwrite, IProgress<long>? progress, CancellationToken ct) =>
        body.Length <= SingleUploadLimit
            ? PutBlobAsync(location, body, overwrite, progress, ct)
            : PutBlocksAsync(location, body, overwrite, progress, ct);

    private async Task PutBlobAsync(
        AzureBlobLocation location, FileStream body, bool overwrite, IProgress<long>? progress, CancellationToken ct)
    {
        var name = LeafName(location.Blob);
        body.Position = 0;
        using var content = new ProgressStreamContent(body, progress);
        using var request = new HttpRequestMessage(HttpMethod.Put, _endpoint.Blob(location.Container!, location.Blob, null))
        {
            Content = content,
        };
        request.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
        if (!overwrite)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        }

        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        if (!overwrite && IsAlreadyExists(response))
        {
            throw AlreadyExists(name);
        }

        await EnsureSuccessAsync(response, name, ct);
    }

    /// <summary>
    /// Stages the body block by block (Put Block), then commits the list (Put Block List): the blob
    /// appears, or changes, only at that commit, so a failure midway leaves the old one untouched.
    /// </summary>
    private async Task PutBlocksAsync(
        AzureBlobLocation location, FileStream body, bool overwrite, IProgress<long>? progress, CancellationToken ct)
    {
        var name = LeafName(location.Blob);
        var length = body.Length;
        var blockSize = BlockSizeFor(length);
        var count = (int)((length + blockSize - 1) / blockSize);

        // Ids are unique to this upload, so a commit cannot pick up blocks an abandoned earlier upload
        // left staged — and all of one length, which the service requires of a blob's blocks.
        var upload = Guid.NewGuid().ToString("N")[..8];
        var ids = new List<string>(count);
        for (var index = 0; index < count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var id = Convert.ToBase64String(Encoding.ASCII.GetBytes(
                upload + "-" + index.ToString("D6", CultureInfo.InvariantCulture)));
            ids.Add(id);

            var offset = index * blockSize;
            using var content = new FileRangeContent(body, offset, Math.Min(blockSize, length - offset), progress, offset);
            using var request = new HttpRequestMessage(HttpMethod.Put,
                _endpoint.Blob(location.Container!, location.Blob, "comp=block&blockid=" + Uri.EscapeDataString(id)))
            {
                Content = content,
            };
            using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            await EnsureSuccessAsync(response, name, ct);
        }

        var list = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><BlockList>");
        foreach (var id in ids)
        {
            list.Append("<Latest>").Append(id).Append("</Latest>");
        }

        list.Append("</BlockList>");

        using var commit = new HttpRequestMessage(HttpMethod.Put,
            _endpoint.Blob(location.Container!, location.Blob, "comp=blocklist"))
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(list.ToString())),
        };
        if (!overwrite)
        {
            commit.Headers.TryAddWithoutValidation("If-None-Match", "*");
        }

        using var committed = await SendMetadataAsync(commit, ct);
        if (!overwrite && IsAlreadyExists(committed))
        {
            throw AlreadyExists(name);
        }

        await EnsureSuccessAsync(committed, name, ct);
    }

    /// <summary>The block size: <see cref="BlockSize"/>, or whole mebibytes enough to stay within 50 000 blocks.</summary>
    private long BlockSizeFor(long length)
    {
        const long MiB = 1024 * 1024;
        var needed = (length + MaxBlocks - 1) / MaxBlocks;
        return needed <= BlockSize ? BlockSize : (needed + MiB - 1) / MiB * MiB;
    }
}
