using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.Network.Cloud;

/// <summary>
/// Dropbox as a virtual file system, <c>dropbox://</c>, over the Dropbox API v2.
/// </summary>
/// <remarks>
/// Dropbox addresses items by path (case-insensitive, case-preserving), so a VFS path maps straight
/// onto an API path; the root is <c>""</c>. Calls on files that carry bytes go to
/// <c>content.dropboxapi.com</c> with their arguments in the <c>Dropbox-API-Arg</c> header, which
/// must be ASCII (<see cref="DropboxApiArg"/>). Deleting moves into Dropbox's deleted files, from
/// where the user can restore it; a file replaced by a move or copy goes there too. Files that
/// cannot be downloaded (Paper documents and the like) are not listed.
/// </remarks>
public sealed class DropboxFileSystem : CloudFileSystem
{
    private const string ApiBase = "https://api.dropboxapi.com/2/";
    private const string ContentBase = "https://content.dropboxapi.com/2/";

    public DropboxFileSystem(ConnectionInfo connection)
        : this(connection, null)
    {
    }

    /// <summary>Test seam: runs against a caller-supplied client (and clock) without opening a socket.</summary>
    internal DropboxFileSystem(ConnectionInfo connection, HttpClient? client, TimeProvider? time = null)
        : base(connection, OAuthProvider.Dropbox, client, time)
    {
    }

    public override string Scheme => "dropbox";

    /// <summary>Dropbox takes up to 150 MB in one upload call; larger files go through an upload session.</summary>
    internal long SimpleUploadLimit { get; init; } = 150L * 1000 * 1000;

    /// <summary>One request of an upload session; Dropbox recommends a multiple of 4 MiB.</summary>
    internal int ChunkSize { get; init; } = 2 * 4 * 1024 * 1024;

    // ── Reading ──────────────────────────────────────────────────────────────

    public override async Task<IReadOnlyList<IFileEntry>> ListAsync(VfsPath path, CancellationToken ct = default)
    {
        var folder = CloudPath.Normalize(path);
        var entries = new List<IFileEntry>();
        var endpoint = "files/list_folder";
        var args = new JsonObject
        {
            ["path"] = ApiPath(folder),
            ["limit"] = 2000,
            ["include_non_downloadable_files"] = false,
        };

        while (true)
        {
            using var response = await RpcAsync(endpoint, args, ct);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                var summary = await SummaryAsync(response, ct);
                throw summary switch
                {
                    _ when summary.Contains("not_found", StringComparison.Ordinal) => FolderNotFound(folder),
                    _ when summary.Contains("not_folder", StringComparison.Ordinal) => new IOException($"\"{folder}\" is not a folder."),
                    _ => await ErrorAsync(response, ct),
                };
            }

            using var json = await ReadJsonAsync(response, ct);
            var root = json.RootElement;
            if (root.TryGetProperty("entries", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (ToEntry(folder, item) is { } entry)
                    {
                        entries.Add(entry);
                    }
                }
            }

            if (root.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True
                && CloudJson.String(root, "cursor") is { } cursor)
            {
                endpoint = "files/list_folder/continue";
                args = new JsonObject { ["cursor"] = cursor };
                continue;
            }

            return entries;
        }
    }

    public override async Task<IFileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct = default)
    {
        var target = CloudPath.Normalize(path);
        if (CloudPath.IsRoot(target))
        {
            // Dropbox has no metadata for its root; it always exists.
            return RootEntry();
        }

        using var json = await GetMetadataAsync(target, ct);
        return json is null ? null : ToEntry(CloudPath.Parent(target), json.RootElement);
    }

    public override async Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct = default)
    {
        var response = await DownloadAsync(CloudPath.Normalize(path), 0, ct);
        return await response.Content.ReadAsStreamAsync(ct);
    }

    /// <summary>
    /// A ranged download. The validator is the file's <c>rev</c>, which every download names in its
    /// <c>Dropbox-API-Result</c> header: when the rest came from another revision than the part already
    /// here, it is dropped and the whole new file is sent from byte 0.
    /// </summary>
    public override async Task<(Stream Stream, long Offset, long Total, string? Validator)> OpenReadFromAsync(
        VfsPath path, long offset, string? validator, CancellationToken ct)
    {
        var target = CloudPath.Normalize(path);
        var start = Math.Max(0, offset);
        var response = await DownloadAsync(target, start, ct);
        var (rev, size) = ResultOf(response);
        var resumed = start > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (resumed && validator is not null && rev != validator)
        {
            // The rest of the new version would not fit the start of the old one.
            response.Dispose();
            response = await DownloadAsync(target, 0, ct);
            (rev, size) = ResultOf(response);
            resumed = false;
        }

        if (size <= 0 && response.Content.Headers.ContentLength is { } length)
        {
            size = resumed ? start + length : length;
        }

        return (await response.Content.ReadAsStreamAsync(ct), resumed ? start : 0, size, rev);
    }

    private async Task<HttpResponseMessage> DownloadAsync(string path, long start, CancellationToken ct)
    {
        if (CloudPath.IsRoot(path))
        {
            throw new IOException($"\"{path}\" is a folder.");
        }

        var arg = DropboxApiArg.Serialize(new JsonObject { ["path"] = path });
        var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, ContentBase + "files/download");
            request.Headers.TryAddWithoutValidation(DropboxApiArg.Header, arg);
            if (start > 0)
            {
                request.Headers.Range = new RangeHeaderValue(start, null);
            }

            return request;
        }, CloudRequestKind.Download, ct);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && start > 0)
        {
            // The part already here is as long as the file, or the file shrank: start over.
            response.Dispose();
            return await DownloadAsync(path, 0, ct);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                var summary = await SummaryAsync(response, ct);
                if (summary.Contains("not_found", StringComparison.Ordinal))
                {
                    throw NotFound(path);
                }

                if (summary.Contains("not_file", StringComparison.Ordinal))
                {
                    throw new IOException($"\"{path}\" is a folder.");
                }
            }

            throw await ErrorAsync(response, ct);
        }
    }

    /// <summary>The revision and size a download names in its <c>Dropbox-API-Result</c> header.</summary>
    private static (string? Rev, long Size) ResultOf(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Dropbox-API-Result", out var values) || values.FirstOrDefault() is not { } header)
        {
            return (null, 0);
        }

        try
        {
            using var json = JsonDocument.Parse(header);
            return (CloudJson.String(json.RootElement, "rev"), CloudJson.Int64(json.RootElement, "size") ?? 0);
        }
        catch (JsonException)
        {
            return (null, 0);
        }
    }

    // ── Writing ──────────────────────────────────────────────────────────────

    public override async Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite = false, CancellationToken ct = default)
    {
        var target = CloudPath.Normalize(path);
        if (CloudPath.IsRoot(target))
        {
            throw new IOException("The root of the Dropbox is a folder, not a file.");
        }

        var name = CloudPath.Name(target);
        using (var existing = await GetMetadataAsync(target, ct))
        {
            if (existing is not null)
            {
                if (CloudJson.String(existing.RootElement, ".tag") == "folder")
                {
                    throw new IOException($"\"{name}\" is a folder.");
                }

                if (!overwrite)
                {
                    throw AlreadyExists(name);
                }
            }
        }

        return await UploadStream((body, progress, token) => UploadAsync(target, overwrite, body, progress, token), ct);
    }

    /// <summary>
    /// Sends the collected body. Without <paramref name="overwrite"/> the write mode is <c>add</c> with
    /// no automatic renaming, so Dropbox itself refuses to replace a file that appeared meanwhile.
    /// </summary>
    private async Task UploadAsync(string path, bool overwrite, FileStream body, IProgress<long>? progress, CancellationToken ct)
    {
        var name = CloudPath.Name(path);
        var total = body.Length;
        JsonObject Commit() => new()
        {
            ["path"] = path,
            ["mode"] = overwrite ? "overwrite" : "add",
            ["autorename"] = false,
            ["mute"] = true,
        };

        if (total <= SimpleUploadLimit)
        {
            var arg = DropboxApiArg.Serialize(Commit());
            using var response = await SendAsync(
                () => ContentRequest("files/upload", arg, new FileRangeContent(body, 0, total, progress)),
                CloudRequestKind.Upload, ct);
            await EnsureUploadedAsync(response, name, ct);
        }
        else
        {
            var first = Math.Min(ChunkSize, total);
            string sessionId;
            var startArg = DropboxApiArg.Serialize(new JsonObject { ["close"] = false });
            using (var started = await SendAsync(
                       () => ContentRequest("files/upload_session/start", startArg, new FileRangeContent(body, 0, first, progress)),
                       CloudRequestKind.Upload, ct))
            {
                using var json = await ReadJsonAsync(started, ct);
                sessionId = CloudJson.String(json.RootElement, "session_id")
                            ?? throw new IOException("Dropbox opened no upload session.");
            }

            var offset = first;
            while (total - offset > ChunkSize)
            {
                var at = offset;
                var appendArg = DropboxApiArg.Serialize(new JsonObject
                {
                    ["cursor"] = new JsonObject { ["session_id"] = sessionId, ["offset"] = at },
                    ["close"] = false,
                });
                using var appended = await SendAsync(
                    () => ContentRequest("files/upload_session/append_v2", appendArg, new FileRangeContent(body, at, ChunkSize, progress, at)),
                    CloudRequestKind.Upload, ct);
                await EnsureSuccessAsync(appended, ct);
                offset += ChunkSize;
            }

            var last = offset;
            var finishArg = DropboxApiArg.Serialize(new JsonObject
            {
                ["cursor"] = new JsonObject { ["session_id"] = sessionId, ["offset"] = last },
                ["commit"] = Commit(),
            });
            using var finished = await SendAsync(
                () => ContentRequest("files/upload_session/finish", finishArg, new FileRangeContent(body, last, total - last, progress, last)),
                CloudRequestKind.Upload, ct);
            await EnsureUploadedAsync(finished, name, ct);
        }

        RaiseChanged(path, overwrite ? WatcherChangeTypes.Changed : WatcherChangeTypes.Created);
    }

    private async Task EnsureUploadedAsync(HttpResponseMessage response, string name, CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.Conflict
            && (await SummaryAsync(response, ct)).Contains("conflict", StringComparison.Ordinal))
        {
            throw AlreadyExists(name);
        }

        await EnsureSuccessAsync(response, ct);
    }

    private static HttpRequestMessage ContentRequest(string endpoint, string arg, HttpContent body)
    {
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var request = new HttpRequestMessage(HttpMethod.Post, ContentBase + endpoint) { Content = body };
        request.Headers.TryAddWithoutValidation(DropboxApiArg.Header, arg);
        return request;
    }

    // ── Changing ─────────────────────────────────────────────────────────────

    public override async Task CopyAsync(VfsPath source, VfsPath destination, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        var from = CloudPath.Normalize(source);
        var to = CloudPath.Normalize(destination);
        if (from.Equals(to, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"\"{CloudPath.Name(to)}\" cannot be copied onto itself.");
        }

        using var json = await RelocateAsync("files/copy_v2", from, to, ct);
        var size = CloudJson.Object(json.RootElement, "metadata") is { } metadata ? CloudJson.Int64(metadata, "size") ?? 0 : 0;
        ReportCopied(progress, size, CloudPath.Name(from), started);
        RaiseChanged(to, WatcherChangeTypes.Created);
    }

    public override async Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct)
    {
        var from = CloudPath.Normalize(source);
        var to = CloudPath.Normalize(destination);
        if (from == to)
        {
            return;
        }

        using var _ = await RelocateAsync("files/move_v2", from, to, ct);
        RaiseChanged(from, WatcherChangeTypes.Deleted);
        RaiseChanged(to, WatcherChangeTypes.Created);
    }

    /// <summary>
    /// A move or copy that replaces an existing destination file: Dropbox refuses with a conflict, the
    /// file in the way goes to the deleted files, and the call is made once more. Never when the
    /// destination is the source under another case — Dropbox paths ignore case, so that "file in
    /// the way" would be the source itself.
    /// </summary>
    private async Task<JsonDocument> RelocateAsync(string endpoint, string from, string to, CancellationToken ct)
    {
        if (CloudPath.IsRoot(from) || CloudPath.IsRoot(to))
        {
            throw new IOException("The root of the Dropbox cannot be moved or copied.");
        }

        JsonObject Args() => new()
        {
            ["from_path"] = from,
            ["to_path"] = to,
            ["autorename"] = false,
            ["allow_ownership_transfer"] = false,
        };

        for (var attempt = 0; ; attempt++)
        {
            using var response = await RpcAsync(endpoint, Args(), ct);
            if (response.StatusCode != HttpStatusCode.Conflict)
            {
                return await ReadJsonAsync(response, ct);
            }

            var summary = await SummaryAsync(response, ct);
            if (summary.StartsWith("from_lookup/not_found", StringComparison.Ordinal))
            {
                throw NotFound(from);
            }

            if (!summary.StartsWith("to/conflict", StringComparison.Ordinal))
            {
                throw await ErrorAsync(response, ct);
            }

            if (attempt > 0 || from.Equals(to, StringComparison.OrdinalIgnoreCase)
                || summary.Contains("folder", StringComparison.Ordinal) || await IsFolderAsync(from, ct))
            {
                // A folder is never replaced wholesale; the panel merges folders item by item.
                throw AlreadyExists(CloudPath.Name(to));
            }

            await DeleteOneAsync(to, ct);
        }
    }

    public override async Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct)
    {
        var target = CloudPath.Normalize(path);
        if (CloudPath.IsRoot(target))
        {
            throw new IOException("The root of the Dropbox cannot be deleted.");
        }

        if (!recursive)
        {
            using var response = await RpcAsync("files/list_folder", new JsonObject { ["path"] = target, ["limit"] = 1 }, ct);
            if (response.IsSuccessStatusCode)
            {
                using var json = await ReadJsonAsync(response, ct);
                if (json.RootElement.TryGetProperty("entries", out var entries) && entries.GetArrayLength() > 0)
                {
                    throw new IOException($"The folder \"{target}\" is not empty.");
                }
            }

            // A file answers "not_folder", which is fine; a missing path is reported by the delete.
        }

        await DeleteOneAsync(target, ct);
        RaiseChanged(target, WatcherChangeTypes.Deleted);
    }

    private async Task DeleteOneAsync(string path, CancellationToken ct)
    {
        using var response = await RpcAsync("files/delete_v2", new JsonObject { ["path"] = path }, ct);
        if (response.StatusCode == HttpStatusCode.Conflict
            && (await SummaryAsync(response, ct)).Contains("not_found", StringComparison.Ordinal))
        {
            throw NotFound(path);
        }

        await EnsureSuccessAsync(response, ct);
    }

    public override async Task CreateDirectoryAsync(VfsPath path, CancellationToken ct)
    {
        var target = CloudPath.Normalize(path);
        if (CloudPath.IsRoot(target))
        {
            return;
        }

        using var response = await RpcAsync("files/create_folder_v2", new JsonObject { ["path"] = target, ["autorename"] = false }, ct);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var summary = await SummaryAsync(response, ct);
            if (summary.Contains("conflict/folder", StringComparison.Ordinal))
            {
                // Already there, as a folder: nothing to do, as on the local disk.
                return;
            }

            if (summary.Contains("conflict", StringComparison.Ordinal))
            {
                throw AlreadyExists(CloudPath.Name(target));
            }
        }

        await EnsureSuccessAsync(response, ct);
        RaiseChanged(target, WatcherChangeTypes.Created);
    }

    public override async Task RenameAsync(VfsPath path, string newName, CancellationToken ct)
    {
        var target = CloudPath.Normalize(path);
        CloudNames.Validate(newName);
        if (CloudPath.IsRoot(target))
        {
            throw new IOException("The root of the Dropbox cannot be renamed.");
        }

        if (CloudPath.Name(target) == newName)
        {
            return;
        }

        var renamed = CloudPath.Combine(CloudPath.Parent(target), newName);
        using var response = await RpcAsync("files/move_v2", new JsonObject
        {
            ["from_path"] = target,
            ["to_path"] = renamed,
            ["autorename"] = false,
        }, ct);

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var summary = await SummaryAsync(response, ct);
            if (summary.StartsWith("to/conflict", StringComparison.Ordinal))
            {
                throw AlreadyExists(newName);
            }

            if (summary.StartsWith("from_lookup/not_found", StringComparison.Ordinal))
            {
                throw NotFound(target);
            }
        }

        await EnsureSuccessAsync(response, ct);
        RaiseChanged(target, WatcherChangeTypes.Renamed);
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    private async Task<bool> IsFolderAsync(string path, CancellationToken ct)
    {
        using var json = await GetMetadataAsync(path, ct);
        return json is not null && CloudJson.String(json.RootElement, ".tag") == "folder";
    }

    /// <summary>The metadata of a path, or null when Dropbox says it is not there.</summary>
    private async Task<JsonDocument?> GetMetadataAsync(string path, CancellationToken ct)
    {
        using var response = await RpcAsync("files/get_metadata", new JsonObject { ["path"] = path }, ct);
        if (response.StatusCode == HttpStatusCode.Conflict
            && (await SummaryAsync(response, ct)).Contains("not_found", StringComparison.Ordinal))
        {
            return null;
        }

        return await ReadJsonAsync(response, ct);
    }

    private Task<HttpResponseMessage> RpcAsync(string endpoint, JsonObject args, CancellationToken ct)
    {
        var body = args.ToJsonString();
        return SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, ApiBase + endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            },
            CloudRequestKind.Metadata, ct);
    }

    /// <summary>The <c>error_summary</c> of a 409, without its trailing <c>/...</c>; empty when there is none.</summary>
    private async Task<string> SummaryAsync(HttpResponseMessage response, CancellationToken ct) =>
        ParseError(await ReadBodyAsync(response, ct)).Code ?? string.Empty;

    private static string ApiPath(string path) => CloudPath.IsRoot(path) ? string.Empty : path;

    private static NetworkFileEntry? ToEntry(string parent, JsonElement item)
    {
        var tag = CloudJson.String(item, ".tag");
        var name = CloudJson.String(item, "name");
        if (name is null || tag is not ("file" or "folder"))
        {
            return null;
        }

        // client_modified is the time the file had where it came from (the Dropbox app sets it from
        // the disk); the server's own time is only the fallback.
        var modified = CloudJson.Time(CloudJson.String(item, "client_modified") ?? CloudJson.String(item, "server_modified"));
        return Entry(CloudPath.Combine(parent, name), tag == "folder", CloudJson.Int64(item, "size") ?? 0, modified);
    }

    // ── Provider details ─────────────────────────────────────────────────────

    private protected override async Task<string?> FetchAccountEmailAsync(CancellationToken ct)
    {
        // An RPC without arguments: Dropbox wants the JSON null as its body.
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, ApiBase + "users/get_current_account")
            {
                Content = new StringContent("null", Encoding.UTF8, "application/json"),
            },
            CloudRequestKind.Metadata, ct);
        using var json = await ReadJsonAsync(response, ct);
        return CloudJson.String(json.RootElement, "email");
    }

    /// <summary>
    /// <c>{"error_summary": "path/not_found/..", "error": {…}}</c>. The summary is the code; the message
    /// spells out the common ones. Other errors (400) come as plain text, which the base reports as is.
    /// </summary>
    private protected override (string? Code, string? Message) ParseError(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            if (CloudJson.String(json.RootElement, "error_summary") is not { } summary)
            {
                return (null, null);
            }

            var code = summary.TrimEnd('.', '/');
            return (code, Describe(code));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string Describe(string code) => code switch
    {
        _ when code.Contains("not_found", StringComparison.Ordinal) => "the path was not found",
        _ when code.Contains("conflict", StringComparison.Ordinal) => "something with that name is already there",
        _ when code.Contains("insufficient_space", StringComparison.Ordinal) => "there is not enough space in the Dropbox",
        _ when code.Contains("disallowed_name", StringComparison.Ordinal) => "Dropbox does not allow that name",
        _ when code.Contains("malformed_path", StringComparison.Ordinal) => "the path is not valid in Dropbox",
        _ when code.Contains("no_write_permission", StringComparison.Ordinal) => "there is no permission to write there",
        _ when code.Contains("too_many_write_operations", StringComparison.Ordinal) => "too many changes at once; try again",
        _ when code.Contains("missing_scope", StringComparison.Ordinal) =>
            "the app is missing a permission; allow files.* and account_info.read in the App Console and sign in again",
        _ => code.Replace('/', ' ').Replace('_', ' '),
    };
}

/// <summary>
/// The <c>Dropbox-API-Arg</c> header: the call's arguments as JSON in an HTTP header, which carries
/// only ASCII. Dropbox wants every character from 0x7F up escaped as <c>\uXXXX</c>; a name in
/// Cyrillic or with an emoji sent raw would arrive garbled or be refused.
/// </summary>
internal static class DropboxApiArg
{
    public const string Header = "Dropbox-API-Arg";

    // Escapes only what JSON itself requires; everything non-ASCII is escaped below, uniformly.
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Serialize(JsonNode arguments) => EscapeNonAscii(arguments.ToJsonString(Relaxed));

    /// <summary>Escapes each UTF-16 unit from 0x7F up; a surrogate pair becomes two escapes, which JSON reads back as one character.</summary>
    internal static string EscapeNonAscii(string json)
    {
        StringBuilder? escaped = null;
        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];
            if (c < 0x7F)
            {
                escaped?.Append(c);
                continue;
            }

            escaped ??= new StringBuilder(json.Length + 32).Append(json, 0, i);
            escaped.Append("\\u").Append(((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
        }

        return escaped?.ToString() ?? json;
    }
}
