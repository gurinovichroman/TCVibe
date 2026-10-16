using System.Collections.Concurrent;
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
/// Google Drive ("My Drive") as a virtual file system, <c>gdrive://</c>, over the Drive API v3.
/// </summary>
/// <remarks>
/// <para>
/// Drive addresses items by id, not by path. A path is resolved name by name from <c>root</c>; the ids
/// of folders are cached for the session, refreshed by every listing of their parent and forgotten by
/// every move, rename and delete made here. A file's id is always looked up fresh, because "is it
/// there?" must not be answered from memory. A folder deleted elsewhere for good answers 404 on its
/// cached id, and the path is then walked again once.
/// </para>
/// <para>
/// A Drive folder may hold several items of one name. A path names one of them, always the same: a
/// folder before a file, an ordinary file before a Google document, then the oldest (earliest
/// <c>createdTime</c>, then the smaller id). Listings show only that one; the others are still in the
/// Drive web interface. A <c>/</c> or <c>\</c> inside a Drive name is shown as <c>∕</c> or <c>⧵</c>,
/// which cannot be confused with a path separator.
/// </para>
/// <para>
/// Google Docs, Sheets, Slides and Drawings have no bytes of their own. They are listed with the
/// extension of the format they are exported to (<c>.docx</c>, <c>.xlsx</c>, <c>.pptx</c>, <c>.pdf</c>),
/// with an unknown size (0), and read as that export (Drive caps exports at 10 MB). They can be renamed,
/// moved, copied and deleted, but never overwritten. Other Google types (Forms, Sites, shortcuts…) are
/// left out. Shared drives and "Shared with me" are not shown. Deleting moves to the trash.
/// </para>
/// </remarks>
public sealed class GoogleDriveFileSystem : CloudFileSystem
{
    internal const string FolderMime = "application/vnd.google-apps.folder";
    private const string ApiBase = "https://www.googleapis.com/drive/v3/";
    private const string UploadBase = "https://www.googleapis.com/upload/drive/v3/";
    private const string GoogleAppsPrefix = "application/vnd.google-apps.";
    private const string ItemFields = "id,name,mimeType,size,modifiedTime,createdTime,headRevisionId,md5Checksum";
    private const int ResumeIncomplete = 308;

    /// <summary>Native Google documents and what they are read as.</summary>
    private static readonly Dictionary<string, (string Extension, string ExportMime)> Exports = new(StringComparer.Ordinal)
    {
        ["application/vnd.google-apps.document"] =
            (".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
        ["application/vnd.google-apps.spreadsheet"] =
            (".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
        ["application/vnd.google-apps.presentation"] =
            (".pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation"),
        ["application/vnd.google-apps.drawing"] = (".pdf", "application/pdf"),
    };

    private readonly ConcurrentDictionary<string, string> _folderIds = new(StringComparer.Ordinal);

    public GoogleDriveFileSystem(ConnectionInfo connection)
        : this(connection, null)
    {
    }

    /// <summary>Test seam: runs against a caller-supplied client (and clock) without opening a socket.</summary>
    internal GoogleDriveFileSystem(ConnectionInfo connection, HttpClient? client, TimeProvider? time = null)
        : base(connection, OAuthProvider.GoogleDrive, client, time)
    {
        _folderIds["/"] = "root";
    }

    public override string Scheme => "gdrive";

    /// <summary>Uploads up to this size go in one multipart request; larger ones through a resumable session.</summary>
    internal long MultipartLimit { get; init; } = 5 * 1024 * 1024;

    /// <summary>One request of a resumable session. Drive wants a multiple of 256 KiB.</summary>
    internal int ChunkSize { get; init; } = 32 * 256 * 1024;

    // ── Reading ──────────────────────────────────────────────────────────────

    public override async Task<IReadOnlyList<IFileEntry>> ListAsync(VfsPath path, CancellationToken ct = default)
    {
        var folder = CloudPath.Normalize(path);
        var children = await WithFreshFolderIdsAsync(async () =>
        {
            var folderId = await FindFolderIdAsync(folder, ct) ?? throw FolderNotFound(folder);
            return await QueryAsync($"{Quote(folderId)} in parents and trashed = false", ct);
        });

        var shown = children
            .Where(i => i.IsVisible)
            .GroupBy(i => i.DisplayName, StringComparer.Ordinal)
            .Select(g => Pick(g)!)
            .ToList();

        // The listing is the freshest word on this folder's subfolders: what it does not show is gone.
        ForgetFolders(folder, keepSelf: true);
        foreach (var sub in shown.Where(i => i.IsFolder))
        {
            _folderIds[CloudPath.Combine(folder, sub.DisplayName)] = sub.Id;
        }

        return shown.Select(i => (IFileEntry)ToEntry(CloudPath.Combine(folder, i.DisplayName), i)).ToList();
    }

    public override async Task<IFileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct = default)
    {
        var target = CloudPath.Normalize(path);
        if (CloudPath.IsRoot(target))
        {
            return RootEntry();
        }

        var item = await WithFreshFolderIdsAsync(() => FindAsync(target, ct));
        return item is null ? null : ToEntry(target, item);
    }

    public override async Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct = default)
    {
        var item = await RequireFileAsync(CloudPath.Normalize(path), ct);
        var response = await DownloadAsync(item, 0, ct);
        return await response.Content.ReadAsStreamAsync(ct);
    }

    /// <summary>
    /// A ranged download. The validator is the file's head revision (its MD5 when Drive keeps no
    /// revision id): a file that changed since is sent whole from byte 0. A Google document is
    /// exported, which has no ranges, so it always starts over.
    /// </summary>
    public override async Task<(Stream Stream, long Offset, long Total, string? Validator)> OpenReadFromAsync(
        VfsPath path, long offset, string? validator, CancellationToken ct)
    {
        var item = await RequireFileAsync(CloudPath.Normalize(path), ct);
        if (item.Export is not null)
        {
            var export = await DownloadAsync(item, 0, ct);
            return (await export.Content.ReadAsStreamAsync(ct), 0, 0, null);
        }

        var start = offset > 0 && (validator is null || validator == item.Revision) ? offset : 0;
        var response = await DownloadAsync(item, start, ct);
        var resumed = start > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        return (await response.Content.ReadAsStreamAsync(ct), resumed ? start : 0, item.Size, item.Revision);
    }

    private async Task<HttpResponseMessage> DownloadAsync(DriveItem item, long start, CancellationToken ct)
    {
        var url = item.Export is { } export
            ? $"{ApiBase}files/{Uri.EscapeDataString(item.Id)}/export?mimeType={Uri.EscapeDataString(export.ExportMime)}"
            : $"{ApiBase}files/{Uri.EscapeDataString(item.Id)}?alt=media";

        var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (start > 0)
            {
                request.Headers.Range = new RangeHeaderValue(start, null);
            }

            return request;
        }, CloudRequestKind.Download, ct);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && start > 0)
        {
            // The part already here is as long as the file (or longer, the file shrank): start over.
            response.Dispose();
            return await DownloadAsync(item, 0, ct);
        }

        if (!response.IsSuccessStatusCode)
        {
            using (response)
            {
                throw await ErrorAsync(response, ct);
            }
        }

        return response;
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
        var (parentId, existing) = await FindParentAndChildAsync(target, ct);
        CheckWritable(existing, name, overwrite);
        return await UploadStream((body, progress, token) => UploadAsync(target, parentId, name, overwrite, body, progress, token), ct);
    }

    private async Task UploadAsync(
        string path, string parentId, string name, bool overwrite, FileStream body, IProgress<long>? progress, CancellationToken ct)
    {
        // Again, at the last moment: the body took time to collect, and Drive itself would keep two
        // files of one name side by side rather than refuse.
        var existing = await FindChildAsync(parentId, name, ct);
        CheckWritable(existing, name, overwrite);

        if (body.Length <= MultipartLimit)
        {
            await UploadSmallAsync(existing?.Id, parentId, name, body, progress, ct);
        }
        else
        {
            await UploadResumableAsync(existing?.Id, parentId, name, body, progress, ct);
        }

        RaiseChanged(path, existing is null ? WatcherChangeTypes.Created : WatcherChangeTypes.Changed);
    }

    /// <summary>A new file: metadata and bytes in one multipart request. An existing one: its bytes replaced in place.</summary>
    private async Task UploadSmallAsync(
        string? existingId, string parentId, string name, FileStream body, IProgress<long>? progress, CancellationToken ct)
    {
        var length = body.Length;
        var metadata = NewFileMetadata(name, parentId);
        using var response = await SendAsync(() =>
        {
            var media = new FileRangeContent(body, 0, length, progress);
            media.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            if (existingId is not null)
            {
                // Updating keeps the file's id, its sharing and its revision history.
                return new HttpRequestMessage(HttpMethod.Patch,
                    $"{UploadBase}files/{Uri.EscapeDataString(existingId)}?uploadType=media&fields=id") { Content = media };
            }

            var content = new MultipartContent("related")
            {
                new StringContent(metadata, Encoding.UTF8, "application/json"),
                media,
            };
            return new HttpRequestMessage(HttpMethod.Post, $"{UploadBase}files?uploadType=multipart&fields=id") { Content = content };
        }, CloudRequestKind.Upload, ct);
        await EnsureSuccessAsync(response, ct);
    }

    /// <summary>
    /// A resumable session: one request opens it, then the bytes go in chunks. Drive answers 308 with a
    /// <c>Range</c> of what it holds, and the next chunk starts right after — a chunk may land in part.
    /// </summary>
    private async Task UploadResumableAsync(
        string? existingId, string parentId, string name, FileStream body, IProgress<long>? progress, CancellationToken ct)
    {
        var total = body.Length;
        var metadata = existingId is null ? NewFileMetadata(name, parentId) : "{}";
        var openUrl = existingId is null
            ? $"{UploadBase}files?uploadType=resumable&fields=id"
            : $"{UploadBase}files/{Uri.EscapeDataString(existingId)}?uploadType=resumable&fields=id";

        Uri session;
        using (var opened = await SendAsync(() =>
               {
                   var request = new HttpRequestMessage(existingId is null ? HttpMethod.Post : HttpMethod.Patch, openUrl)
                   {
                       Content = new StringContent(metadata, Encoding.UTF8, "application/json"),
                   };
                   request.Headers.TryAddWithoutValidation("X-Upload-Content-Type", "application/octet-stream");
                   request.Headers.TryAddWithoutValidation("X-Upload-Content-Length", total.ToString(CultureInfo.InvariantCulture));
                   return request;
               }, CloudRequestKind.Metadata, ct))
        {
            await EnsureSuccessAsync(opened, ct);
            session = opened.Headers.Location ?? throw new IOException("Google Drive opened no upload session.");
        }

        long offset = 0;
        var stalls = 0;
        while (true)
        {
            var start = offset;
            var length = Math.Min(ChunkSize, total - start);
            using var response = await SendAsync(() =>
            {
                var content = new FileRangeContent(body, start, length, progress, start);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                content.Headers.ContentRange = new ContentRangeHeaderValue(start, start + length - 1, total);
                return new HttpRequestMessage(HttpMethod.Put, session) { Content = content };
            }, CloudRequestKind.Upload, ct);

            if ((int)response.StatusCode != ResumeIncomplete)
            {
                await EnsureSuccessAsync(response, ct);
                return;
            }

            offset = ReceivedUpTo(response);
            stalls = offset > start ? 0 : stalls + 1;
            if (stalls >= 3)
            {
                throw new IOException("Google Drive keeps refusing the upload's next part.");
            }
        }
    }

    /// <summary>The byte after the last one a 308 says Drive holds; none named means none held.</summary>
    private static long ReceivedUpTo(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Range", out var values)
            && values.FirstOrDefault() is { } range
            && range.LastIndexOf('-') is var dash and > 0
            && long.TryParse(range[(dash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var last))
        {
            return last + 1;
        }

        return 0;
    }

    private static string NewFileMetadata(string name, string parentId) =>
        new JsonObject { ["name"] = name, ["parents"] = new JsonArray(parentId) }.ToJsonString();

    private static void CheckWritable(DriveItem? existing, string name, bool overwrite)
    {
        if (existing is null)
        {
            return;
        }

        if (existing.IsFolder)
        {
            throw new IOException($"\"{name}\" is a folder.");
        }

        if (!overwrite)
        {
            throw AlreadyExists(name);
        }

        if (existing.IsNative)
        {
            throw new IOException($"\"{name}\" is a Google document and cannot be overwritten. Save the file under another name.");
        }
    }

    // ── Changing ─────────────────────────────────────────────────────────────

    public override async Task CopyAsync(VfsPath source, VfsPath destination, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        var from = CloudPath.Normalize(source);
        var to = CloudPath.Normalize(destination);
        var item = await RequireFileAsync(from, ct);
        var name = CloudPath.Name(to);
        var (parentId, existing) = await FindParentAndChildAsync(to, ct);
        if (existing?.Id == item.Id)
        {
            throw new IOException($"\"{name}\" cannot be copied onto itself.");
        }

        CheckWritable(existing, name, overwrite: true);

        using (var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post,
                   $"{ApiBase}files/{Uri.EscapeDataString(item.Id)}/copy?fields=id")
               {
                   Content = JsonBody(new JsonObject { ["name"] = DriveNameFor(item, name), ["parents"] = new JsonArray(parentId) }),
               }, CloudRequestKind.Metadata, ct))
        {
            await EnsureSuccessAsync(response, ct);
        }

        // The copy is in place before the file it replaces goes, and that one goes to the trash.
        if (existing is not null)
        {
            await PatchAsync(existing.Id, new JsonObject { ["trashed"] = true }, null, ct);
        }

        ReportCopied(progress, item.Size, CloudPath.Name(from), started);
        RaiseChanged(to, WatcherChangeTypes.Created);
    }

    public override async Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct)
    {
        var from = CloudPath.Normalize(source);
        var to = CloudPath.Normalize(destination);
        if (CloudPath.IsRoot(from) || CloudPath.IsRoot(to))
        {
            throw new IOException("The root of the drive cannot be moved.");
        }

        var item = await WithFreshFolderIdsAsync(() => FindAsync(from, ct)) ?? throw NotFound(from);
        if (item.IsFolder && CloudPath.IsSameOrUnder(to, from, StringComparison.Ordinal))
        {
            throw new IOException($"The folder \"{CloudPath.Name(from)}\" cannot be moved into itself.");
        }

        var name = CloudPath.Name(to);
        var fromParentId = await WithFreshFolderIdsAsync(() => FindFolderIdAsync(CloudPath.Parent(from), ct))
                           ?? throw FolderNotFound(CloudPath.Parent(from));
        var (toParentId, existing) = await FindParentAndChildAsync(to, ct);
        if (existing?.Id == item.Id)
        {
            // The item itself, perhaps under another case: only the name may change.
            existing = null;
        }

        if (existing is not null && (existing.IsFolder || item.IsFolder))
        {
            throw AlreadyExists(name);
        }

        CheckWritable(existing, name, overwrite: true);

        var changes = new JsonObject();
        if (name != item.DisplayName)
        {
            changes["name"] = DriveNameFor(item, name);
        }

        var parents = fromParentId == toParentId
            ? null
            : $"addParents={Uri.EscapeDataString(toParentId)}&removeParents={Uri.EscapeDataString(fromParentId)}";
        if (parents is not null || changes.Count > 0)
        {
            await PatchAsync(item.Id, changes, parents, ct);
        }

        // Replaced only once the move has landed, and into the trash.
        if (existing is not null)
        {
            await PatchAsync(existing.Id, new JsonObject { ["trashed"] = true }, null, ct);
        }

        ForgetFolders(from, keepSelf: false);
        ForgetFolders(to, keepSelf: false);
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

        var item = await WithFreshFolderIdsAsync(() => FindAsync(target, ct)) ?? throw NotFound(target);
        if (item.IsFolder && !recursive
            && (await QueryAsync($"{Quote(item.Id)} in parents and trashed = false", ct, firstPageOnly: true, pageSize: 1)).Count > 0)
        {
            throw new IOException($"The folder \"{target}\" is not empty.");
        }

        // Into the trash, not gone: Drive keeps it there for 30 days.
        await PatchAsync(item.Id, new JsonObject { ["trashed"] = true }, null, ct);
        ForgetFolders(target, keepSelf: false);
        RaiseChanged(target, WatcherChangeTypes.Deleted);
    }

    public override async Task CreateDirectoryAsync(VfsPath path, CancellationToken ct)
    {
        var target = CloudPath.Normalize(path);
        if (CloudPath.IsRoot(target))
        {
            return;
        }

        var name = CloudPath.Name(target);
        var (parentId, existing) = await FindParentAndChildAsync(target, ct);
        if (existing is not null)
        {
            // Drive would make a second folder of the same name; an existing one is simply used.
            if (existing.IsFolder)
            {
                _folderIds[target] = existing.Id;
                return;
            }

            throw AlreadyExists(name);
        }

        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}files?fields=id")
        {
            Content = JsonBody(new JsonObject
            {
                ["name"] = name,
                ["mimeType"] = FolderMime,
                ["parents"] = new JsonArray(parentId),
            }),
        }, CloudRequestKind.Metadata, ct);
        using var json = await ReadJsonAsync(response, ct);
        if (CloudJson.String(json.RootElement, "id") is { } id)
        {
            _folderIds[target] = id;
        }

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

        var item = await WithFreshFolderIdsAsync(() => FindAsync(target, ct)) ?? throw NotFound(target);
        if (newName == item.DisplayName)
        {
            return;
        }

        var (_, clash) = await FindParentAndChildAsync(CloudPath.Combine(CloudPath.Parent(target), newName), ct);
        if (clash is not null && clash.Id != item.Id)
        {
            throw AlreadyExists(newName);
        }

        await PatchAsync(item.Id, new JsonObject { ["name"] = DriveNameFor(item, newName) }, null, ct);
        ForgetFolders(target, keepSelf: false);
        RaiseChanged(target, WatcherChangeTypes.Renamed);
    }

    private async Task PatchAsync(string id, JsonObject changes, string? query, CancellationToken ct)
    {
        var url = $"{ApiBase}files/{Uri.EscapeDataString(id)}?fields=id" + (query is null ? string.Empty : "&" + query);
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Patch, url) { Content = JsonBody(changes) }, CloudRequestKind.Metadata, ct);
        await EnsureSuccessAsync(response, ct);
    }

    // ── Paths to ids ─────────────────────────────────────────────────────────

    private async Task<DriveItem> RequireFileAsync(string path, CancellationToken ct)
    {
        var item = CloudPath.IsRoot(path) ? null : await WithFreshFolderIdsAsync(() => FindAsync(path, ct));
        if (item is null)
        {
            throw NotFound(path);
        }

        return item.IsFolder ? throw new IOException($"\"{path}\" is a folder.") : item;
    }

    /// <summary>The id of <paramref name="path"/>'s folder and what already has its name there, if anything.</summary>
    private Task<(string ParentId, DriveItem? Existing)> FindParentAndChildAsync(string path, CancellationToken ct) =>
        WithFreshFolderIdsAsync(async () =>
        {
            var parent = CloudPath.Parent(path);
            var parentId = await FindFolderIdAsync(parent, ct) ?? throw FolderNotFound(parent);
            return (parentId, await FindChildAsync(parentId, CloudPath.Name(path), ct));
        });

    private async Task<DriveItem?> FindAsync(string path, CancellationToken ct)
    {
        var parentId = await FindFolderIdAsync(CloudPath.Parent(path), ct);
        return parentId is null ? null : await FindChildAsync(parentId, CloudPath.Name(path), ct);
    }

    private async Task<string?> FindFolderIdAsync(string path, CancellationToken ct)
    {
        if (_folderIds.TryGetValue(path, out var cached))
        {
            return cached;
        }

        var parentId = await FindFolderIdAsync(CloudPath.Parent(path), ct);
        if (parentId is null)
        {
            return null;
        }

        var item = await FindChildAsync(parentId, CloudPath.Name(path), ct);
        if (item is not { IsFolder: true })
        {
            return null;
        }

        _folderIds[path] = item.Id;
        return item.Id;
    }

    /// <summary>
    /// The item a name means in a folder. The query asks for the name as shown, the Drive name it
    /// stands for (separators put back) and, for a name ending in an export extension, the Google
    /// document it may be; an exact match wins over one that differs only in case.
    /// </summary>
    private async Task<DriveItem?> FindChildAsync(string parentId, string displayName, CancellationToken ct)
    {
        var driveName = CloudNames.FromDisplay(displayName);
        var alternatives = new List<string> { $"name = {Quote(displayName)}" };
        if (driveName != displayName)
        {
            alternatives.Add($"name = {Quote(driveName)}");
        }

        foreach (var (mime, export) in Exports)
        {
            if (driveName.Length > export.Extension.Length && driveName.EndsWith(export.Extension, StringComparison.OrdinalIgnoreCase))
            {
                alternatives.Add($"(name = {Quote(driveName[..^export.Extension.Length])} and mimeType = {Quote(mime)})");
            }
        }

        var found = (await QueryAsync(
                $"{Quote(parentId)} in parents and trashed = false and ({string.Join(" or ", alternatives)})", ct))
            .Where(i => i.IsVisible)
            .ToList();
        return Pick(found.Where(i => i.DisplayName == displayName))
               ?? Pick(found.Where(i => string.Equals(i.DisplayName, displayName, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The one of several same-named items a path means (see the class remarks).</summary>
    internal static DriveItem? Pick(IEnumerable<DriveItem> candidates) =>
        candidates
            .OrderByDescending(i => i.IsFolder)
            .ThenBy(i => i.IsNative)
            .ThenBy(i => i.Created)
            .ThenBy(i => i.Id, StringComparer.Ordinal)
            .FirstOrDefault();

    private async Task<List<DriveItem>> QueryAsync(string query, CancellationToken ct, bool firstPageOnly = false, int pageSize = 1000)
    {
        var items = new List<DriveItem>();
        var fields = Uri.EscapeDataString($"nextPageToken,files({ItemFields})");
        string? pageToken = null;
        do
        {
            var url = $"{ApiBase}files?q={Uri.EscapeDataString(query)}&fields={fields}&pageSize={pageSize}&spaces=drive"
                      + (pageToken is null ? string.Empty : "&pageToken=" + Uri.EscapeDataString(pageToken));
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), CloudRequestKind.Metadata, ct);
            using var json = await ReadJsonAsync(response, ct);
            if (json.RootElement.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
            {
                foreach (var file in files.EnumerateArray())
                {
                    if (DriveItem.Parse(file) is { } item)
                    {
                        items.Add(item);
                    }
                }
            }

            pageToken = CloudJson.String(json.RootElement, "nextPageToken");
        }
        while (!firstPageOnly && !string.IsNullOrEmpty(pageToken));

        return items;
    }

    /// <summary>
    /// Runs a lookup; when a cached folder id turns out to be unknown to Drive (404 — the folder was
    /// deleted for good elsewhere), forgets the cache and walks the path once more.
    /// </summary>
    private async Task<T> WithFreshFolderIdsAsync<T>(Func<Task<T>> lookup)
    {
        try
        {
            return await lookup();
        }
        catch (CloudDriveException ex) when (ex.StatusCode == HttpStatusCode.NotFound && _folderIds.Count > 1)
        {
            ForgetFolders("/", keepSelf: true);
            return await lookup();
        }
    }

    private void ForgetFolders(string path, bool keepSelf)
    {
        foreach (var key in _folderIds.Keys)
        {
            if (key != "/" && CloudPath.IsSameOrUnder(key, path, StringComparison.Ordinal) && !(keepSelf && key == path))
            {
                _folderIds.TryRemove(key, out _);
            }
        }
    }

    /// <summary>The Drive name to give an item shown as <paramref name="displayName"/>: a document drops its export extension.</summary>
    private static string DriveNameFor(DriveItem item, string displayName) =>
        item.Export is { } export
        && displayName.Length > export.Extension.Length
        && displayName.EndsWith(export.Extension, StringComparison.OrdinalIgnoreCase)
            ? displayName[..^export.Extension.Length]
            : displayName;

    /// <summary>A literal for a Drive query: single quotes, with <c>'</c> and <c>\</c> escaped.</summary>
    internal static string Quote(string value) => "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    private static StringContent JsonBody(JsonObject body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    private static NetworkFileEntry ToEntry(string path, DriveItem item) =>
        Entry(path, item.IsFolder, item.IsNative ? 0 : item.Size, item.Modified);

    // ── Provider details ─────────────────────────────────────────────────────

    private protected override async Task<string?> FetchAccountEmailAsync(CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
            $"{ApiBase}about?fields={Uri.EscapeDataString("user(emailAddress)")}"), CloudRequestKind.Metadata, ct);
        using var json = await ReadJsonAsync(response, ct);
        return CloudJson.Object(json.RootElement, "user") is { } user ? CloudJson.String(user, "emailAddress") : null;
    }

    /// <summary><c>{"error": {"code": 404, "message": "…", "errors": [{"reason": "notFound"}], "status": "NOT_FOUND"}}</c>.</summary>
    private protected override (string? Code, string? Message) ParseError(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            if (CloudJson.Object(json.RootElement, "error") is not { } error)
            {
                return (null, null);
            }

            string? reason = null;
            if (error.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                reason = errors.EnumerateArray().Select(e => CloudJson.String(e, "reason")).FirstOrDefault(r => r is not null);
            }

            return (reason ?? CloudJson.String(error, "status"), CloudJson.String(error, "message"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>Drive also says "slow down" with a 403 whose reason is a rate limit.</summary>
    private protected override async ValueTask<bool> IsThrottledAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (await base.IsThrottledAsync(response, ct))
        {
            return true;
        }

        return response.StatusCode == HttpStatusCode.Forbidden
               && ParseError(await ReadBodyAsync(response, ct)).Code is "rateLimitExceeded" or "userRateLimitExceeded";
    }

    /// <summary>One Drive file or folder as the listing needs it.</summary>
    internal sealed record DriveItem(
        string Id, string Name, string MimeType, long Size, DateTime Modified, DateTimeOffset Created, string? Revision)
    {
        public bool IsFolder => MimeType == FolderMime;

        /// <summary>A Google document, sheet, form…: no bytes of its own.</summary>
        public bool IsNative => !IsFolder && MimeType.StartsWith(GoogleAppsPrefix, StringComparison.Ordinal);

        public (string Extension, string ExportMime)? Export => Exports.TryGetValue(MimeType, out var export) ? export : null;

        /// <summary>Folders, ordinary files, and the Google documents that can be exported.</summary>
        public bool IsVisible => !IsNative || Export is not null;

        /// <summary>The name a panel shows: separators replaced, an export extension added.</summary>
        public string DisplayName
        {
            get
            {
                var shown = CloudNames.ToDisplay(Name);
                return Export is { } export && !shown.EndsWith(export.Extension, StringComparison.OrdinalIgnoreCase)
                    ? shown + export.Extension
                    : shown;
            }
        }

        public static DriveItem? Parse(JsonElement element)
        {
            var id = CloudJson.String(element, "id");
            var name = CloudJson.String(element, "name");
            if (id is null || name is null)
            {
                return null;
            }

            var created = DateTimeOffset.TryParse(CloudJson.String(element, "createdTime"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : DateTimeOffset.MinValue;
            return new DriveItem(
                id,
                name,
                CloudJson.String(element, "mimeType") ?? "application/octet-stream",
                CloudJson.Int64(element, "size") ?? 0,
                CloudJson.Time(CloudJson.String(element, "modifiedTime")),
                created,
                CloudJson.String(element, "headRevisionId") ?? CloudJson.String(element, "md5Checksum"));
        }
    }
}
