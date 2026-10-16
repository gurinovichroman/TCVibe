using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.Network.Cloud;

/// <summary>
/// OneDrive (personal, or the signed-in user's work OneDrive) as a virtual file system,
/// <c>onedrive://</c>, over Microsoft Graph.
/// </summary>
/// <remarks>
/// Graph addresses items by path (<c>/me/drive/root:/a/b:</c>), case-insensitively, so no id cache is
/// needed. Downloads and upload sessions go to pre-authenticated addresses that must never see the
/// bearer token: the client follows no redirects on its own, and those requests carry no token.
/// Deleting moves to the recycle bin; a file replaced by a move or copy goes there too (OneDrive keeps
/// no two items of one name, and replacing on copy is not offered for personal accounts). OneNote
/// notebooks are listed as folders. Items shared with the user but not added to their drive are not shown.
/// </remarks>
public sealed class OneDriveFileSystem : CloudFileSystem
{
    private const string GraphBase = "https://graph.microsoft.com/v1.0/";
    private const string ListSelect = "id,name,size,folder,package,remoteItem,file,fileSystemInfo,lastModifiedDateTime";

    public OneDriveFileSystem(ConnectionInfo connection)
        : this(connection, null)
    {
    }

    /// <summary>Test seam: runs against a caller-supplied client (and clock) without opening a socket.</summary>
    internal OneDriveFileSystem(ConnectionInfo connection, HttpClient? client, TimeProvider? time = null)
        : base(connection, OAuthProvider.OneDrive, client, time)
    {
    }

    public override string Scheme => "onedrive";

    /// <summary>Graph takes up to 4 MB in one PUT; larger files go through an upload session.</summary>
    internal long SimpleUploadLimit { get; init; } = 4L * 1000 * 1000;

    /// <summary>One fragment of an upload session: Graph wants a multiple of 320 KiB (10 MiB here).</summary>
    internal int FragmentSize { get; init; } = 32 * 320 * 1024;

    /// <summary>How often a server-side copy's progress is asked for.</summary>
    internal TimeSpan CopyPollInterval { get; init; } = TimeSpan.FromSeconds(1);

    // ── Reading ──────────────────────────────────────────────────────────────

    public override async Task<IReadOnlyList<IFileEntry>> ListAsync(VfsPath path, CancellationToken ct = default)
    {
        var folder = CloudPath.Normalize(path);
        var entries = new List<IFileEntry>();
        string? next = $"{ChildrenUrl(folder)}?$select={ListSelect}&$top=1000";
        while (next is not null)
        {
            var url = next;
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), CloudRequestKind.Metadata, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw FolderNotFound(folder);
            }

            using var json = await ReadJsonAsync(response, ct);
            if (json.RootElement.TryGetProperty("value", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in items.EnumerateArray())
                {
                    if (GraphItem.Parse(element) is { } item)
                    {
                        entries.Add(ToEntry(CloudPath.Combine(folder, item.Name), item));
                    }
                }
            }

            // The next page's address comes whole, with its own query: it is used as given.
            next = CloudJson.String(json.RootElement, "@odata.nextLink");
        }

        return entries;
    }

    public override async Task<IFileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct = default)
    {
        var target = CloudPath.Normalize(path);
        if (CloudPath.IsRoot(target))
        {
            return RootEntry();
        }

        var item = await GetItemAsync(target, ct);
        return item is null ? null : ToEntry(CloudPath.Combine(CloudPath.Parent(target), item.Name), item);
    }

    public override async Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct = default)
    {
        var response = await DownloadContentAsync(CloudPath.Normalize(path), 0, ct);
        return await response.Content.ReadAsStreamAsync(ct);
    }

    /// <summary>
    /// A ranged download. The validator is the item's <c>cTag</c>, which changes with the content only:
    /// a file that changed since is sent whole from byte 0. The bytes come from the item's
    /// pre-authenticated download address, without the bearer token.
    /// </summary>
    public override async Task<(Stream Stream, long Offset, long Total, string? Validator)> OpenReadFromAsync(
        VfsPath path, long offset, string? validator, CancellationToken ct)
    {
        var target = CloudPath.Normalize(path);
        var item = await GetItemAsync(target, ct) ?? throw NotFound(target);
        if (item.IsFolder)
        {
            throw new IOException($"\"{target}\" is a folder.");
        }

        var current = item.CTag ?? item.ETag;
        var start = offset > 0 && (validator is null || validator == current) ? offset : 0;
        var response = item.DownloadUrl is { } address
            ? await DownloadPreauthenticatedAsync(new Uri(address), start, target, ct)
            : await DownloadContentAsync(target, start, ct);
        var resumed = start > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        return (await response.Content.ReadAsStreamAsync(ct), resumed ? start : 0, item.Size, current);
    }

    /// <summary><c>…:/content</c> answers with a redirect to a pre-authenticated address, followed here without the token.</summary>
    private async Task<HttpResponseMessage> DownloadContentAsync(string path, long start, CancellationToken ct)
    {
        if (CloudPath.IsRoot(path))
        {
            throw new IOException($"\"{path}\" is a folder.");
        }

        var response = await SendAsync(() => Ranged(new HttpRequestMessage(HttpMethod.Get, $"{ItemUrl(path)}/content"), start),
            CloudRequestKind.Download, ct);
        if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
        {
            response.Dispose();
            var target = location.IsAbsoluteUri ? location : new Uri(new Uri(GraphBase), location);
            return await DownloadPreauthenticatedAsync(target, start, path, ct);
        }

        return await EnsureDownloadAsync(response, start, path, () => DownloadContentAsync(path, 0, ct), ct);
    }

    private async Task<HttpResponseMessage> DownloadPreauthenticatedAsync(Uri address, long start, string path, CancellationToken ct)
    {
        var response = await SendAsync(() => Ranged(new HttpRequestMessage(HttpMethod.Get, address), start),
            CloudRequestKind.Download, ct, authorize: false);
        return await EnsureDownloadAsync(response, start, path, () => DownloadPreauthenticatedAsync(address, 0, path, ct), ct);
    }

    private async Task<HttpResponseMessage> EnsureDownloadAsync(
        HttpResponseMessage response, long start, string path, Func<Task<HttpResponseMessage>> fromTheStart, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && start > 0)
            {
                // The part already here is as long as the file, or the file shrank: start over.
                return await fromTheStart();
            }

            throw response.StatusCode == HttpStatusCode.NotFound ? NotFound(path) : await ErrorAsync(response, ct);
        }
    }

    // ── Writing ──────────────────────────────────────────────────────────────

    public override async Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite = false, CancellationToken ct = default)
    {
        var target = CloudPath.Normalize(path);
        if (CloudPath.IsRoot(target))
        {
            throw new IOException("The root of the drive is a folder, not a file.");
        }

        var name = CloudPath.Name(target);
        var existing = await GetItemAsync(target, ct);
        if (existing is { IsFolder: true })
        {
            throw new IOException($"\"{name}\" is a folder.");
        }

        if (existing is not null && !overwrite)
        {
            throw AlreadyExists(name);
        }

        return await UploadStream((body, progress, token) => UploadAsync(target, overwrite, body, progress, token), ct);
    }

    /// <summary>
    /// Sends the collected body. Without <paramref name="overwrite"/> the conflict behaviour is
    /// <c>fail</c>, so OneDrive itself refuses to replace a file that appeared meanwhile.
    /// </summary>
    private async Task UploadAsync(string path, bool overwrite, FileStream body, IProgress<long>? progress, CancellationToken ct)
    {
        var name = CloudPath.Name(path);
        var behavior = overwrite ? "replace" : "fail";
        var total = body.Length;

        if (total <= SimpleUploadLimit)
        {
            using var response = await SendAsync(() =>
            {
                var content = new FileRangeContent(body, 0, total, progress);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                return new HttpRequestMessage(HttpMethod.Put, $"{ItemUrl(path)}/content?@microsoft.graph.conflictBehavior={behavior}")
                {
                    Content = content,
                };
            }, CloudRequestKind.Upload, ct);
            await EnsureUploadedAsync(response, name, ct);
        }
        else
        {
            await UploadSessionAsync(path, behavior, body, progress, ct);
        }

        RaiseChanged(path, overwrite ? WatcherChangeTypes.Changed : WatcherChangeTypes.Created);
    }

    /// <summary>
    /// An upload session: Graph hands out a pre-authenticated address and takes the file in fragments
    /// (each one answered 202 with the ranges still expected) until the last makes the item. An
    /// unfinished session is cancelled, so no half upload lingers.
    /// </summary>
    private async Task UploadSessionAsync(string path, string behavior, FileStream body, IProgress<long>? progress, CancellationToken ct)
    {
        var name = CloudPath.Name(path);
        var total = body.Length;
        var sessionBody = new JsonObject
        {
            ["item"] = new JsonObject { ["@microsoft.graph.conflictBehavior"] = behavior },
        }.ToJsonString();

        Uri uploadUrl;
        using (var created = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"{ItemUrl(path)}/createUploadSession")
               {
                   Content = new StringContent(sessionBody, Encoding.UTF8, "application/json"),
               }, CloudRequestKind.Metadata, ct))
        {
            if (created.StatusCode == HttpStatusCode.Conflict)
            {
                throw AlreadyExists(name);
            }

            using var json = await ReadJsonAsync(created, ct);
            uploadUrl = new Uri(CloudJson.String(json.RootElement, "uploadUrl")
                                ?? throw new IOException("OneDrive opened no upload session."));
        }

        var finished = false;
        try
        {
            long offset = 0;
            var stalls = 0;
            while (true)
            {
                var start = offset;
                var length = Math.Min(FragmentSize, total - start);
                using var response = await SendAsync(() =>
                {
                    var content = new FileRangeContent(body, start, length, progress, start);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    content.Headers.ContentRange = new ContentRangeHeaderValue(start, start + length - 1, total);
                    return new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = content };
                }, CloudRequestKind.Upload, ct, authorize: false);

                if (response.StatusCode == HttpStatusCode.Accepted)
                {
                    using var json = await ReadJsonAsync(response, ct);
                    offset = NextExpected(json.RootElement) ?? start + length;
                    stalls = offset > start ? 0 : stalls + 1;
                    if (stalls >= 3)
                    {
                        throw new IOException("OneDrive keeps refusing the upload's next part.");
                    }

                    continue;
                }

                await EnsureUploadedAsync(response, name, ct);
                finished = true;
                return;
            }
        }
        finally
        {
            if (!finished)
            {
                await CancelSessionAsync(uploadUrl);
            }
        }
    }

    /// <summary>The first offset in <c>nextExpectedRanges</c> (<c>["26-"]</c>), or null when none is named.</summary>
    private static long? NextExpected(JsonElement session)
    {
        if (session.TryGetProperty("nextExpectedRanges", out var ranges) && ranges.ValueKind == JsonValueKind.Array)
        {
            foreach (var range in ranges.EnumerateArray())
            {
                var text = range.GetString();
                var dash = text?.IndexOf('-') ?? -1;
                if (dash > 0 && long.TryParse(text![..dash], NumberStyles.Integer, CultureInfo.InvariantCulture, out var next))
                {
                    return next;
                }
            }
        }

        return null;
    }

    private async Task CancelSessionAsync(Uri uploadUrl)
    {
        try
        {
            using var timeout = new CancellationTokenSource(MetadataTimeout);
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, uploadUrl),
                CloudRequestKind.Upload, timeout.Token, authorize: false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            // Best effort: an abandoned session expires on its own.
        }
    }

    private async Task EnsureUploadedAsync(HttpResponseMessage response, string name, CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw AlreadyExists(name);
        }

        await EnsureSuccessAsync(response, ct);
    }

    // ── Changing ─────────────────────────────────────────────────────────────

    public override async Task CopyAsync(VfsPath source, VfsPath destination, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        var from = CloudPath.Normalize(source);
        var to = CloudPath.Normalize(destination);
        var name = CloudPath.Name(to);
        if (from.Equals(to, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"\"{name}\" cannot be copied onto itself.");
        }

        var item = await GetItemAsync(from, ct) ?? throw NotFound(from);
        if (item.IsFolder)
        {
            throw new IOException($"OneDrive copies files here; the folder \"{CloudPath.Name(from)}\" is copied item by item.");
        }

        var parent = await GetFolderAsync(CloudPath.Parent(to), ct);
        if (await GetItemAsync(to, ct) is { } existing)
        {
            if (existing.IsFolder)
            {
                throw AlreadyExists(name);
            }

            await DeleteItemAsync(to, ct);
        }

        var reference = new JsonObject { ["id"] = parent.Id };
        if (parent.DriveId is not null)
        {
            reference["driveId"] = parent.DriveId;
        }

        var body = new JsonObject { ["parentReference"] = reference, ["name"] = name }.ToJsonString();
        Uri monitor;
        using (var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"{ItemUrl(from)}/copy")
               {
                   Content = new StringContent(body, Encoding.UTF8, "application/json"),
               }, CloudRequestKind.Metadata, ct))
        {
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                throw AlreadyExists(name);
            }

            await EnsureSuccessAsync(response, ct);
            monitor = response.Headers.Location ?? throw new IOException("OneDrive started the copy but gave no way to follow it.");
        }

        await WaitForCopyAsync(monitor, item.Size, CloudPath.Name(from), progress, started, ct);
        ReportCopied(progress, item.Size, CloudPath.Name(from), started);
        RaiseChanged(to, WatcherChangeTypes.Created);
    }

    /// <summary>
    /// A copy runs on the server: its monitor address (pre-authenticated, no token) reports progress
    /// until the status is <c>completed</c>, or redirects to the new item when it is done.
    /// </summary>
    private async Task WaitForCopyAsync(
        Uri monitor, long size, string name, IProgress<FileProgress>? progress, Stopwatch started, CancellationToken ct)
    {
        while (true)
        {
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, monitor),
                CloudRequestKind.Metadata, ct, authorize: false);
            if (IsRedirect(response.StatusCode))
            {
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw await ErrorAsync(response, ct);
            }

            using var json = await ReadJsonAsync(response, ct);
            var status = CloudJson.String(json.RootElement, "status");
            switch (status)
            {
                case "completed":
                    return;
                case "failed" or "cancelled" or "deleteFailed":
                    var reason = CloudJson.Object(json.RootElement, "error") is { } error ? CloudJson.String(error, "message") : null;
                    throw new IOException($"OneDrive could not copy \"{name}\": {reason ?? status}.");
            }

            if (json.RootElement.TryGetProperty("percentageComplete", out var percent)
                && percent.ValueKind == JsonValueKind.Number && size > 0)
            {
                progress?.Report(new FileProgress((long)(size * Math.Clamp(percent.GetDouble(), 0, 100) / 100), size, name, started.Elapsed));
            }

            await Task.Delay(CopyPollInterval, Time, ct);
        }
    }

    public override async Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct)
    {
        var from = CloudPath.Normalize(source);
        var to = CloudPath.Normalize(destination);
        if (CloudPath.IsRoot(from) || CloudPath.IsRoot(to))
        {
            throw new IOException("The root of the drive cannot be moved.");
        }

        if (from == to)
        {
            return;
        }

        var sameItem = from.Equals(to, StringComparison.OrdinalIgnoreCase);
        if (!sameItem && CloudPath.IsSameOrUnder(to, from, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"The folder \"{CloudPath.Name(from)}\" cannot be moved into itself.");
        }

        var changes = new JsonObject { ["name"] = CloudPath.Name(to) };
        if (!CloudPath.Parent(from).Equals(CloudPath.Parent(to), StringComparison.OrdinalIgnoreCase))
        {
            changes["parentReference"] = new JsonObject { ["id"] = (await GetFolderAsync(CloudPath.Parent(to), ct)).Id };
        }

        for (var attempt = 0; ; attempt++)
        {
            using var response = await PatchAsync(from, changes, ct);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                // A file is in the way. Never when it is the source under another case — OneDrive paths
                // ignore case, so the "file in the way" would be the source itself; never a folder.
                if (attempt > 0 || sameItem
                    || await GetItemAsync(to, ct) is not { IsFolder: false }
                    || await GetItemAsync(from, ct) is not { IsFolder: false })
                {
                    throw AlreadyExists(CloudPath.Name(to));
                }

                await DeleteItemAsync(to, ct);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw NotFound(from);
            }

            await EnsureSuccessAsync(response, ct);
            break;
        }

        RaiseChanged(from, WatcherChangeTypes.Deleted);
        RaiseChanged(to, WatcherChangeTypes.Created);
    }

    public override async Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct)
    {
        var target = CloudPath.Normalize(path);
        if (CloudPath.IsRoot(target))
        {
            throw new IOException("The root of the drive cannot be deleted.");
        }

        if (!recursive)
        {
            var item = await GetItemAsync(target, ct) ?? throw NotFound(target);
            if (item.IsFolder && item.ChildCount > 0)
            {
                throw new IOException($"The folder \"{target}\" is not empty.");
            }
        }

        await DeleteItemAsync(target, ct);
        RaiseChanged(target, WatcherChangeTypes.Deleted);
    }

    /// <summary>Deletes into the recycle bin.</summary>
    private async Task DeleteItemAsync(string path, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, ItemUrl(path)), CloudRequestKind.Metadata, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
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

        var parent = CloudPath.Parent(target);
        var name = CloudPath.Name(target);
        var body = new JsonObject
        {
            ["name"] = name,
            ["folder"] = new JsonObject(),
            ["@microsoft.graph.conflictBehavior"] = "fail",
        }.ToJsonString();

        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, ChildrenUrl(parent))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        }, CloudRequestKind.Metadata, ct);

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            // Already there: fine when it is a folder, as on the local disk.
            if (await GetItemAsync(target, ct) is { IsFolder: true })
            {
                return;
            }

            throw AlreadyExists(name);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw FolderNotFound(parent);
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
            throw new IOException("The root of the drive cannot be renamed.");
        }

        if (CloudPath.Name(target) == newName)
        {
            return;
        }

        using var response = await PatchAsync(target, new JsonObject { ["name"] = newName }, ct);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw AlreadyExists(newName);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw NotFound(target);
        }

        await EnsureSuccessAsync(response, ct);
        RaiseChanged(target, WatcherChangeTypes.Renamed);
    }

    private Task<HttpResponseMessage> PatchAsync(string path, JsonObject changes, CancellationToken ct)
    {
        var body = changes.ToJsonString();
        return SendAsync(() => new HttpRequestMessage(HttpMethod.Patch, ItemUrl(path))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        }, CloudRequestKind.Metadata, ct);
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    /// <summary>The item at a path, or null when Graph says it is not there.</summary>
    private async Task<GraphItem?> GetItemAsync(string path, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, ItemUrl(path)), CloudRequestKind.Metadata, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        using var json = await ReadJsonAsync(response, ct);
        return GraphItem.Parse(json.RootElement);
    }

    /// <summary>The folder a move or copy goes into: its id and drive, which a parent reference wants.</summary>
    private async Task<GraphItem> GetFolderAsync(string path, CancellationToken ct)
    {
        var folder = await GetItemAsync(path, ct);
        if (folder is null || folder.Id is null)
        {
            throw FolderNotFound(path);
        }

        return folder.IsFolder ? folder : throw new IOException($"\"{path}\" is not a folder.");
    }

    private static string ItemUrl(string path) =>
        CloudPath.IsRoot(path) ? GraphBase + "me/drive/root" : $"{GraphBase}me/drive/root:{Encode(path)}:";

    private static string ChildrenUrl(string path) => ItemUrl(path) + "/children";

    /// <summary>Percent-encodes each segment of a path, leaving the separators alone (<c>#</c>, <c>%</c> and spaces are legal in names).</summary>
    private static string Encode(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private static HttpRequestMessage Ranged(HttpRequestMessage request, long start)
    {
        if (start > 0)
        {
            request.Headers.Range = new RangeHeaderValue(start, null);
        }

        return request;
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.MovedPermanently or HttpStatusCode.PermanentRedirect;

    private static NetworkFileEntry ToEntry(string path, GraphItem item) => Entry(path, item.IsFolder, item.Size, item.Modified);

    // ── Provider details ─────────────────────────────────────────────────────

    private protected override async Task<string?> FetchAccountEmailAsync(CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{GraphBase}me?$select=mail,userPrincipalName"),
            CloudRequestKind.Metadata, ct);
        using var json = await ReadJsonAsync(response, ct);
        return CloudJson.String(json.RootElement, "mail") ?? CloudJson.String(json.RootElement, "userPrincipalName");
    }

    /// <summary><c>{"error": {"code": "itemNotFound", "message": "…"}}</c>.</summary>
    private protected override (string? Code, string? Message) ParseError(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return CloudJson.Object(json.RootElement, "error") is { } error
                ? (CloudJson.String(error, "code"), CloudJson.String(error, "message"))
                : (null, null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>One drive item as the listing and the transfers need it.</summary>
    private sealed record GraphItem(
        string Name, string? Id, string? DriveId, bool IsFolder, long Size, long ChildCount, DateTime Modified,
        string? CTag, string? ETag, string? DownloadUrl)
    {
        public static GraphItem? Parse(JsonElement element)
        {
            if (CloudJson.String(element, "name") is not { } name)
            {
                return null;
            }

            var folder = CloudJson.Object(element, "folder");
            var remoteFolder = CloudJson.Object(element, "remoteItem") is { } remote && CloudJson.Has(remote, "folder");
            var isFolder = folder is not null || CloudJson.Has(element, "package") || remoteFolder;

            // fileSystemInfo carries the time the file had where it came from; lastModifiedDateTime is
            // when OneDrive last touched it.
            var modified = CloudJson.Object(element, "fileSystemInfo") is { } info
                           && CloudJson.String(info, "lastModifiedDateTime") is { } local
                ? local
                : CloudJson.String(element, "lastModifiedDateTime");

            return new GraphItem(
                name,
                CloudJson.String(element, "id"),
                CloudJson.Object(element, "parentReference") is { } parent ? CloudJson.String(parent, "driveId") : null,
                isFolder,
                isFolder ? 0 : CloudJson.Int64(element, "size") ?? 0,
                folder is { } f ? CloudJson.Int64(f, "childCount") ?? 0 : 0,
                CloudJson.Time(modified),
                CloudJson.String(element, "cTag"),
                CloudJson.String(element, "eTag"),
                CloudJson.String(element, "@microsoft.graph.downloadUrl"));
        }
    }
}
