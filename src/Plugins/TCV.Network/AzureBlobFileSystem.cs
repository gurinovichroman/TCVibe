using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.Network;

/// <summary>
/// Azure Blob Storage as a virtual file system: <c>azblob://account/container/path</c>. Folders are
/// what blob names make of their slashes; a connection limited to one container shows that
/// container at its root, otherwise the root lists the account's containers.
/// </summary>
/// <remarks>
/// Plain <see cref="HttpClient"/> and the BCL, no Azure SDK: requests are signed with the account key
/// (<see cref="AzureSharedKey"/>), carry a SAS token's query, or go anonymous to a public container.
/// Addressing lives in <see cref="AzureBlobEndpoint"/> and the XML in <see cref="AzureBlobResponse"/>,
/// so the awkward parts are testable without a server. Asynchronous all the way down, like WebDAV.
/// </remarks>
public sealed partial class AzureBlobFileSystem : IVirtualFileSystem, IRangeReader, IReadLinkSource
{
    /// <summary>The most a List Blobs page may hold.</summary>
    private const int ListPageSize = 5000;

    /// <summary>Requests in flight at once when a folder is deleted, moved or copied blob by blob.</summary>
    private const int Parallelism = 8;

    /// <summary>The longest wait between two looks at a server-side copy that is still running.</summary>
    private static readonly TimeSpan MaxCopyPollInterval = TimeSpan.FromSeconds(5);

    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly AzureBlobEndpoint _endpoint;
    private readonly byte[]? _accountKey;

    /// <summary>Whether the account has a hierarchical namespace; null until a definite answer came.</summary>
    private bool? _hierarchical;
    private bool _disposed;

    /// <summary>Connects to <paramref name="connection"/>: account, key or SAS token, endpoint and container.</summary>
    /// <exception cref="ArgumentException">The account name is missing, or the key is not base64.</exception>
    public AzureBlobFileSystem(ConnectionInfo connection)
        : this(connection, client: null)
    {
    }

    /// <summary>Test seam: runs against a caller-supplied client without opening a socket.</summary>
    internal AzureBlobFileSystem(ConnectionInfo connection, HttpClient? client)
    {
        ArgumentNullException.ThrowIfNull(connection);

        string? sas = null;
        var secret = connection.Password?.Trim();
        if (!string.IsNullOrEmpty(secret))
        {
            if (ConnectionInfo.IsSasToken(secret))
            {
                sas = AzureBlobEndpoint.NormalizeSasToken(secret);
            }
            else
            {
                _accountKey = DecodeAccountKey(secret);
            }
        }

        // Validated before the client exists, so a refused connection leaks no handler.
        _endpoint = new AzureBlobEndpoint(connection, sas);
        UsesSasToken = sas is not null;
        SendsCredentialsInClear = UsesSasToken && !_endpoint.IsHttps;

        var account = _endpoint.Account.Length > 0 ? _endpoint.Account : _endpoint.Host;
        DisplayName = _endpoint.FixedContainer is null ? account : $"{account}/{_endpoint.FixedContainer}";

        _ownsClient = client is null;
        _client = client ?? CreateClient();
    }

    public string Scheme => "azblob";

    /// <summary><c>account/container</c> for a connection limited to one container, otherwise <c>account</c>.</summary>
    public string DisplayName { get; }

    /// <summary>True for a SAS token over plain http: the token travels readable in every URL.</summary>
    public bool SendsCredentialsInClear { get; }

    /// <summary>True when requests carry a SAS token rather than an account-key signature.</summary>
    public bool UsesSasToken { get; }

    /// <summary>True when <see cref="CreateReadLink"/> works: only the account key can sign a link.</summary>
    public bool CanCreateReadLink => _accountKey is not null;

    /// <summary>
    /// How long a listing, stat, delete, copy request or block commit may take before it is called
    /// failed. The bytes of a transfer are not bound by it: a large file takes as long as it takes.
    /// </summary>
    public TimeSpan MetadataTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Uploads up to this size go in one Put Blob; larger ones in blocks.</summary>
    internal long SingleUploadLimit { get; init; } = 256L * 1024 * 1024;

    /// <summary>The block size of a large upload, raised when the file would need more than 50 000 blocks.</summary>
    internal long BlockSize { get; init; } = 8L * 1024 * 1024;

    /// <summary>The first wait before looking at a server-side copy again; it doubles up to five seconds.</summary>
    internal TimeSpan CopyPollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The time requests are stamped with and links expire from; tests fix it.</summary>
    internal Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

    public event EventHandler<VfsChangedEventArgs>? Changed;

    // ── Listing ──────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<IFileEntry>> ListAsync(VfsPath path, CancellationToken ct = default)
    {
        var location = _endpoint.Locate(path);
        if (location.IsServiceRoot)
        {
            return await ListContainersAsync(ct);
        }

        var blobs = new List<AzureListedBlob>();
        var prefixes = new List<string>();
        string? marker = null;
        do
        {
            ct.ThrowIfCancellationRequested();
            var page = await ListPageAsync(location.Container!, location.Prefix, delimited: true, ListPageSize, marker, ct)
                       ?? throw ContainerMissing(location.Container!);
            blobs.AddRange(page.Blobs);
            prefixes.AddRange(page.Prefixes);
            marker = page.NextMarker;
        }
        while (marker is not null);

        return AzureBlobResponse.BuildEntries(path, location.Prefix, blobs, prefixes);
    }

    private async Task<IReadOnlyList<IFileEntry>> ListContainersAsync(CancellationToken ct)
    {
        var containers = new List<(string Name, DateTime LastModified)>();
        string? marker = null;
        do
        {
            ct.ThrowIfCancellationRequested();
            var query = $"comp=list&maxresults={ListPageSize}" + (marker is null ? string.Empty : "&marker=" + Uri.EscapeDataString(marker));
            using var request = new HttpRequestMessage(HttpMethod.Get, _endpoint.Service(query));
            using var response = await SendMetadataAsync(request, ct);
            await EnsureSuccessAsync(response, null, ct);

            var page = AzureBlobResponse.ParseContainerList(await response.Content.ReadAsStringAsync(ct));
            containers.AddRange(page.Containers);
            marker = page.NextMarker;
        }
        while (marker is not null);

        return AzureBlobResponse.BuildContainerEntries(containers);
    }

    /// <summary>
    /// One List Blobs page under <paramref name="prefix"/>: one folder level when
    /// <paramref name="delimited"/>, otherwise every blob below it. Null when the container does not exist.
    /// </summary>
    private async Task<AzureBlobPage?> ListPageAsync(
        string container, string prefix, bool delimited, int maxResults, string? marker, CancellationToken ct)
    {
        var query = "restype=container&comp=list";
        if (prefix.Length > 0)
        {
            query += "&prefix=" + Uri.EscapeDataString(prefix);
        }

        if (delimited)
        {
            query += "&delimiter=%2F";
        }

        // Metadata tells a hierarchical account's directories (hdi_isfolder) from empty files.
        query += "&include=metadata&maxresults=" + maxResults.ToString(CultureInfo.InvariantCulture);
        if (marker is not null)
        {
            query += "&marker=" + Uri.EscapeDataString(marker);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, _endpoint.Container(container, query));
        using var response = await SendMetadataAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, container, ct);
        return AzureBlobResponse.ParseBlobList(await response.Content.ReadAsStringAsync(ct));
    }

    // ── Entries ──────────────────────────────────────────────────────────────

    public async Task<IFileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct = default)
    {
        var location = _endpoint.Locate(path);
        if (location.IsServiceRoot)
        {
            return new NetworkFileEntry(VfsPath.Root, "/", true, 0, DateTime.MinValue);
        }

        if (location.IsContainerRoot)
        {
            // Listed rather than asked for its properties: a container SAS may list a container but
            // not read its properties, and then the connection's own root would look missing.
            var probe = await ListPageAsync(location.Container!, string.Empty, delimited: false, 1, null, ct);
            var name = _endpoint.FixedContainer is null ? location.Container! : "/";
            return probe is null ? null : new NetworkFileEntry(path, name, true, 0, DateTime.MinValue);
        }

        var leaf = LeafName(location.Blob);
        var properties = await GetPropertiesAsync(location.Container!, location.Blob, leaf, ct);
        if (properties is not null)
        {
            return new NetworkFileEntry(path, leaf, properties.IsFolderMarker,
                properties.IsFolderMarker ? 0 : properties.Size, properties.LastModified);
        }

        // No blob of that name: a folder exists only as the common prefix of what is in it.
        return await HasContentAsync(location, ct)
            ? new NetworkFileEntry(path, leaf, true, 0, DateTime.MinValue)
            : null;
    }

    /// <summary>True when any blob lies under the folder <paramref name="location"/> (its own marker included).</summary>
    private async Task<bool> HasContentAsync(AzureBlobLocation location, CancellationToken ct)
    {
        string? marker = null;
        do
        {
            // A page may come back empty with a marker to go on from; only the end of the listing says "nothing".
            var page = await ListPageAsync(location.Container!, location.Prefix, delimited: false, 1, marker, ct);
            if (page is null)
            {
                return false;
            }

            if (page.Blobs.Count > 0 || page.Prefixes.Count > 0)
            {
                return true;
            }

            marker = page.NextMarker;
        }
        while (marker is not null);

        return false;
    }

    /// <summary>What Get Blob Properties says about one blob.</summary>
    private sealed record BlobProperties(
        long Size, DateTime LastModified, string? ETag, bool IsFolderMarker,
        string? CopyId, string? CopyStatus, string? CopyProgress, string? CopyStatusDescription);

    /// <summary>Get Blob Properties; null when there is no such blob (or no such container).</summary>
    private async Task<BlobProperties?> GetPropertiesAsync(string container, string blob, string subject, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, _endpoint.Blob(container, blob, null));
        using var response = await SendMetadataAsync(request, ct);

        // Only "not found" means "does not exist". A refused login is not an answer to "is it there?":
        // taken as one, a copy would overwrite blindly.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, subject, ct);
        return new BlobProperties(
            response.Content.Headers.ContentLength ?? 0,
            response.Content.Headers.LastModified?.LocalDateTime ?? DateTime.MinValue,
            response.Headers.ETag?.ToString(),
            string.Equals(Header(response, "x-ms-meta-hdi_isfolder"), "true", StringComparison.OrdinalIgnoreCase),
            Header(response, "x-ms-copy-id"),
            Header(response, "x-ms-copy-status"),
            Header(response, "x-ms-copy-progress"),
            Header(response, "x-ms-copy-status-description"));
    }

    // ── Reading ──────────────────────────────────────────────────────────────

    public async Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct = default)
    {
        var location = RequireBlob(path);
        var response = await GetBlobAsync(location, 0, null, ct);
        try
        {
            await EnsureSuccessAsync(response, LeafName(location.Blob), ct);
            return await response.Content.ReadAsStreamAsync(ct);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens <paramref name="path"/> at <paramref name="offset"/> on condition that the blob still has
    /// the ETag <paramref name="validator"/> (<c>If-Match</c>). A blob that changed (412) or has nothing
    /// past the offset (416) is read whole from byte 0 instead, and the returned offset says so.
    /// </summary>
    public async Task<(Stream Stream, long Offset, long Total, string? Validator)> OpenReadFromAsync(
        VfsPath path, long offset, string? validator, CancellationToken ct)
    {
        var location = RequireBlob(path);
        if (offset > 0)
        {
            var partial = await GetBlobAsync(location, offset, validator, ct);
            if (partial.StatusCode is not (HttpStatusCode.PreconditionFailed or HttpStatusCode.RequestedRangeNotSatisfiable))
            {
                return await ToRangeResultAsync(partial, offset, location, ct);
            }

            partial.Dispose();
        }

        var whole = await GetBlobAsync(location, 0, null, ct);
        return await ToRangeResultAsync(whole, 0, location, ct);
    }

    /// <summary>
    /// Get Blob, from <paramref name="offset"/> on. No timeout: the headers of a large blob come quickly,
    /// and its body is bounded by the caller's token, not by a clock.
    /// </summary>
    private async Task<HttpResponseMessage> GetBlobAsync(
        AzureBlobLocation location, long offset, string? ifMatch, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _endpoint.Blob(location.Container!, location.Blob, null));
        if (offset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
            if (!string.IsNullOrEmpty(ifMatch))
            {
                request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            }
        }

        return await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private async Task<(Stream Stream, long Offset, long Total, string? Validator)> ToRangeResultAsync(
        HttpResponseMessage response, long offset, AzureBlobLocation location, CancellationToken ct)
    {
        try
        {
            await EnsureSuccessAsync(response, LeafName(location.Blob), ct);
            var resumed = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            var length = response.Content.Headers.ContentLength ?? 0;
            var total = response.Content.Headers.ContentRange?.Length
                        ?? (length > 0 ? (resumed ? offset + length : length) : 0);
            var stream = await response.Content.ReadAsStreamAsync(ct);
            return (stream, resumed ? offset : 0, total, response.Headers.ETag?.ToString());
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    // ── Folders ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a folder; one that is already there is fine (a folder copied onto a folder merges).
    /// </summary>
    /// <remarks>
    /// Blob Storage has no folders of its own, so a marker blob stands for an empty one, and which
    /// marker depends on the account. A hierarchical-namespace (ADLS Gen2) account gets a zero-length
    /// blob <c>dir</c> with metadata <c>hdi_isfolder=true</c>: its blob endpoint makes that a real
    /// directory, and a name ending in "/" is not a valid path there. A flat account gets a
    /// zero-length <c>dir/</c>: the convention other blob tools read as a folder, where an
    /// <c>hdi_isfolder</c> blob would show up in them as an empty file beside it. The account is asked
    /// once (Get Account Information, <c>x-ms-is-hns-enabled</c>); when it cannot say, flat is assumed.
    /// At the root of an account without a fixed container, a folder is a new container.
    /// </remarks>
    public async Task CreateDirectoryAsync(VfsPath path, CancellationToken ct)
    {
        var location = _endpoint.Locate(path);
        if (location.IsServiceRoot || (location.IsContainerRoot && _endpoint.FixedContainer is not null))
        {
            return;
        }

        if (location.IsContainerRoot)
        {
            await CreateContainerAsync(location.Container!, ct);
        }
        else
        {
            await CreateFolderAsync(location, ct);
        }

        Changed?.Invoke(this, new VfsChangedEventArgs(path, WatcherChangeTypes.Created));
    }

    private async Task CreateContainerAsync(string name, CancellationToken ct)
    {
        if (!ContainerName().IsMatch(name))
        {
            throw new IOException(
                $"\"{name}\" cannot be a container: names are 3 to 63 lower-case letters, digits and single hyphens, starting and ending with a letter or digit.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, _endpoint.Container(name, "restype=container")) { Content = Empty() };
        using var response = await SendMetadataAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Conflict && ErrorCode(response) == "ContainerAlreadyExists")
        {
            return;
        }

        await EnsureSuccessAsync(response, name, ct);
    }

    private async Task CreateFolderAsync(AzureBlobLocation location, CancellationToken ct)
    {
        var container = location.Container!;
        var hierarchical = await IsHierarchicalAsync(container, ct);
        var created = await PutFolderMarkerAsync(container, hierarchical ? location.Blob : location.Prefix, hierarchical, ct);
        if (created || !hierarchical)
        {
            return;
        }

        // Something already has that name: the directory itself is fine, a file is not.
        var leaf = LeafName(location.Blob);
        if (await GetPropertiesAsync(container, location.Blob, leaf, ct) is { IsFolderMarker: false })
        {
            throw new IOException($"A file named \"{leaf}\" already exists.");
        }
    }

    /// <summary>Puts a zero-length marker unless something has its name. False when something did.</summary>
    private async Task<bool> PutFolderMarkerAsync(string container, string name, bool hdiFolder, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, _endpoint.Blob(container, name, null)) { Content = Empty() };
        request.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
        request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        if (hdiFolder)
        {
            request.Headers.TryAddWithoutValidation("x-ms-meta-hdi_isfolder", "true");
        }

        using var response = await SendMetadataAsync(request, ct);
        if (IsAlreadyExists(response))
        {
            return false;
        }

        await EnsureSuccessAsync(response, LeafName(name), ct);
        return true;
    }

    /// <summary>
    /// Asks the account once whether it has a hierarchical namespace. Asked at the container, so a
    /// container SAS may answer too. A refusal is not remembered: it says nothing about the account.
    /// </summary>
    private async Task<bool> IsHierarchicalAsync(string container, CancellationToken ct)
    {
        if (_hierarchical is { } known)
        {
            return known;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, _endpoint.Container(container, "restype=account&comp=properties"));
        using var response = await SendMetadataAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var hierarchical = string.Equals(Header(response, "x-ms-is-hns-enabled"), "true", StringComparison.OrdinalIgnoreCase);
        _hierarchical = hierarchical;
        return hierarchical;
    }

    // ── Deleting ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Deletes a blob, a folder or — at the root of an account without a fixed container — a container.
    /// A folder goes blob by blob (eight at a time); without <paramref name="recursive"/> only an empty one does.
    /// </summary>
    public async Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct)
    {
        var location = _endpoint.Locate(path);
        if (location.IsServiceRoot || (location.IsContainerRoot && _endpoint.FixedContainer is not null))
        {
            throw new IOException("The root of the connection cannot be deleted.");
        }

        if (location.IsContainerRoot)
        {
            await DeleteContainerAsync(location.Container!, recursive, ct);
        }
        else
        {
            await DeleteBlobOrFolderAsync(location, recursive, ct);
        }

        Changed?.Invoke(this, new VfsChangedEventArgs(path, WatcherChangeTypes.Deleted));
    }

    private async Task DeleteBlobOrFolderAsync(AzureBlobLocation location, bool recursive, CancellationToken ct)
    {
        var container = location.Container!;
        var leaf = LeafName(location.Blob);
        var properties = await GetPropertiesAsync(container, location.Blob, leaf, ct);

        // The app deletes files with recursive set too, so the blob itself decides what this is. On a
        // flat account a file "x" and a folder "x/" can both exist; deleting the file leaves the folder.
        if (properties is { IsFolderMarker: false })
        {
            await DeleteBlobAsync(container, location.Blob, snapshots: true, ignoreMissing: false, ct);
            return;
        }

        var directoryBlob = properties is not null;
        bool found;
        if (recursive)
        {
            found = await DeleteTreeAsync(container, location.Prefix, ct);
        }
        else
        {
            // Only the folder's own "dir/" marker may be in it.
            var page = await ListPageAsync(container, location.Prefix, delimited: true, 2, null, ct)
                       ?? throw ContainerMissing(container);
            if (page.Prefixes.Count > 0 || page.NextMarker is not null
                || page.Blobs.Any(b => !string.Equals(b.Name, location.Prefix, StringComparison.Ordinal)))
            {
                throw new IOException($"The folder \"{leaf}\" is not empty.");
            }

            found = page.Blobs.Count > 0
                    && await DeleteBlobAsync(container, location.Prefix, snapshots: false, ignoreMissing: true, ct);
        }

        if (directoryBlob)
        {
            await DeleteBlobAsync(container, location.Blob, snapshots: false, ignoreMissing: true, ct);
        }
        else if (!found)
        {
            throw NotFound(leaf);
        }
    }

    /// <summary>
    /// Deletes every blob under <paramref name="prefix"/>, page by page, then the directory blobs of a
    /// hierarchical account deepest first — a directory there only goes once it is empty. True when
    /// there was anything to delete.
    /// </summary>
    private async Task<bool> DeleteTreeAsync(string container, string prefix, CancellationToken ct)
    {
        var directories = new List<string>();
        var found = false;
        string? marker = null;
        do
        {
            ct.ThrowIfCancellationRequested();

            // Deleting behind the listing is safe: the marker names where to go on, not a position.
            var page = await ListPageAsync(container, prefix, delimited: false, ListPageSize, marker, ct)
                       ?? throw ContainerMissing(container);
            found |= page.Blobs.Count > 0;
            directories.AddRange(page.Blobs.Where(b => b.IsFolderMarker).Select(b => b.Name));
            await ForEachAsync(page.Blobs.Where(b => !b.IsFolderMarker), ct,
                (blob, token) => DeleteBlobAsync(container, blob.Name, snapshots: true, ignoreMissing: true, token));
            marker = page.NextMarker;
        }
        while (marker is not null);

        foreach (var level in directories.GroupBy(d => d.Count(c => c == '/')).OrderByDescending(g => g.Key))
        {
            await ForEachAsync(level, ct,
                (name, token) => DeleteBlobAsync(container, name, snapshots: false, ignoreMissing: true, token));
        }

        return found;
    }

    /// <summary>
    /// Delete Blob. A file takes its snapshots with it (without <c>x-ms-delete-snapshots</c> a blob that
    /// has any cannot be deleted); a directory blob has none. False when it was already gone.
    /// </summary>
    private async Task<bool> DeleteBlobAsync(
        string container, string name, bool snapshots, bool ignoreMissing, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, _endpoint.Blob(container, name, null));
        if (snapshots)
        {
            request.Headers.TryAddWithoutValidation("x-ms-delete-snapshots", "include");
        }

        using var response = await SendMetadataAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound && ignoreMissing)
        {
            return false;
        }

        await EnsureSuccessAsync(response, LeafName(name), ct);
        return true;
    }

    private async Task DeleteContainerAsync(string container, bool recursive, CancellationToken ct)
    {
        if (!recursive)
        {
            var page = await ListPageAsync(container, string.Empty, delimited: false, 1, null, ct)
                       ?? throw ContainerMissing(container);
            if (page.Blobs.Count > 0)
            {
                throw new IOException($"The container \"{container}\" is not empty.");
            }
        }

        // The service removes the container and everything in it in one request.
        using var request = new HttpRequestMessage(HttpMethod.Delete, _endpoint.Container(container, "restype=container"));
        using var response = await SendMetadataAsync(request, ct);
        await EnsureSuccessAsync(response, container, ct);
    }

    // ── Copy, move, rename ───────────────────────────────────────────────────

    /// <summary>
    /// Copies inside the account on the server (Copy Blob): no byte passes through this computer. A copy
    /// the service runs in the background is followed until it is done; cancelling aborts it.
    /// </summary>
    public async Task CopyAsync(VfsPath source, VfsPath destination, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        var from = RequireBlob(source);
        var to = RequireBlob(destination);
        var leaf = LeafName(from.Blob);
        var started = Stopwatch.StartNew();

        var properties = await GetPropertiesAsync(from.Container!, from.Blob, leaf, ct);
        if (properties is { IsFolderMarker: false })
        {
            await CopyBlobAsync(from, to, overwrite: true, properties.Size, progress, started, ct);
        }
        else if (!await CopyTreeAsync(from, to, directoryBlob: properties is not null, ct))
        {
            throw NotFound(leaf);
        }

        Changed?.Invoke(this, new VfsChangedEventArgs(destination, WatcherChangeTypes.Created));
    }

    /// <summary>Moves by copying and then deleting the source; an existing destination is replaced.</summary>
    public async Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct)
    {
        await RelocateAsync(source, destination, overwrite: true, ct);
        Changed?.Invoke(this, new VfsChangedEventArgs(source, WatcherChangeTypes.Deleted));
    }

    /// <summary>Renames by copying and then deleting; never replaces an existing entry.</summary>
    public async Task RenameAsync(VfsPath path, string newName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName.Contains('/') || newName.Contains('\\'))
        {
            throw new IOException($"\"{newName}\" is not a valid name.");
        }

        var slash = path.Value.LastIndexOf('/');
        var parent = slash <= 0 ? string.Empty : path.Value[..slash];
        await RelocateAsync(path, new VfsPath(parent + "/" + newName), overwrite: false, ct);
        Changed?.Invoke(this, new VfsChangedEventArgs(path, WatcherChangeTypes.Renamed));
    }

    /// <summary>
    /// Blob Storage has no rename: a file is copied and the original deleted, a folder blob by blob,
    /// and the source goes only once everything has arrived.
    /// </summary>
    private async Task RelocateAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken ct)
    {
        var from = _endpoint.Locate(source);
        var to = _endpoint.Locate(destination);
        if (from.IsServiceRoot || from.IsContainerRoot)
        {
            throw new NotSupportedException(
                "Azure Blob Storage cannot rename or move a container. Create a new container and move the files into it.");
        }

        if (to.IsServiceRoot || to.IsContainerRoot)
        {
            throw new IOException("A file or folder cannot become a container.");
        }

        if (from.Container == to.Container && from.Blob == to.Blob)
        {
            // Copying a blob onto itself and deleting "the source" would delete the only copy.
            return;
        }

        var leaf = LeafName(from.Blob);
        var properties = await GetPropertiesAsync(from.Container!, from.Blob, leaf, ct);
        if (!overwrite && await GetEntryAsync(destination, ct) is not null)
        {
            throw AlreadyExists(LeafName(to.Blob));
        }

        if (properties is { IsFolderMarker: false })
        {
            await CopyBlobAsync(from, to, overwrite, properties.Size, null, Stopwatch.StartNew(), ct);
            await DeleteBlobAsync(from.Container!, from.Blob, snapshots: true, ignoreMissing: true, ct);
            return;
        }

        var directoryBlob = properties is not null;
        if (!await CopyTreeAsync(from, to, directoryBlob, ct))
        {
            throw NotFound(leaf);
        }

        await DeleteTreeAsync(from.Container!, from.Prefix, ct);
        if (directoryBlob)
        {
            await DeleteBlobAsync(from.Container!, from.Blob, snapshots: false, ignoreMissing: true, ct);
        }
    }

    /// <summary>
    /// Copies every blob under the folder <paramref name="from"/> to the same names under
    /// <paramref name="to"/>, eight at a time. Directory blobs become directory markers there. True when
    /// the folder held anything (or is a directory blob).
    /// </summary>
    private async Task<bool> CopyTreeAsync(AzureBlobLocation from, AzureBlobLocation to, bool directoryBlob, CancellationToken ct)
    {
        // The listing would meet the copies it is making and copy them again, deeper each time.
        if (from.Container == to.Container && to.Prefix.StartsWith(from.Prefix, StringComparison.Ordinal))
        {
            throw new IOException($"\"{LeafName(from.Blob)}\" cannot be copied or moved into itself.");
        }

        if (directoryBlob)
        {
            await PutFolderMarkerAsync(to.Container!, to.Blob, hdiFolder: true, ct);
        }

        var found = directoryBlob;
        var clock = Stopwatch.StartNew();
        string? marker = null;
        do
        {
            ct.ThrowIfCancellationRequested();
            var page = await ListPageAsync(from.Container!, from.Prefix, delimited: false, ListPageSize, marker, ct)
                       ?? throw ContainerMissing(from.Container!);
            found |= page.Blobs.Count > 0;
            await ForEachAsync(page.Blobs, ct, async (blob, token) =>
            {
                var target = new AzureBlobLocation(to.Container, to.Prefix + blob.Name[from.Prefix.Length..]);
                if (blob.IsFolderMarker)
                {
                    await PutFolderMarkerAsync(to.Container!, target.Blob, hdiFolder: true, token);
                }
                else
                {
                    await CopyBlobAsync(new AzureBlobLocation(from.Container, blob.Name), target,
                        overwrite: true, blob.Size, null, clock, token);
                }
            });
            marker = page.NextMarker;
        }
        while (marker is not null);

        return found;
    }

    /// <summary>
    /// Copy Blob from <paramref name="from"/> to <paramref name="to"/> in this account. With a SAS token
    /// the source URL carries it too: the service reads the source with what that URL authorizes.
    /// </summary>
    private async Task CopyBlobAsync(
        AzureBlobLocation from, AzureBlobLocation to, bool overwrite, long size,
        IProgress<FileProgress>? progress, Stopwatch clock, CancellationToken ct)
    {
        var name = LeafName(from.Blob);
        using var request = new HttpRequestMessage(HttpMethod.Put, _endpoint.Blob(to.Container!, to.Blob, null)) { Content = Empty() };
        request.Headers.TryAddWithoutValidation("x-ms-copy-source", _endpoint.Blob(from.Container!, from.Blob, null).OriginalString);
        if (!overwrite)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        }

        using var response = await SendMetadataAsync(request, ct);
        if (!overwrite && IsAlreadyExists(response))
        {
            throw AlreadyExists(LeafName(to.Blob));
        }

        await EnsureSuccessAsync(response, name, ct);
        if (!string.Equals(Header(response, "x-ms-copy-status"), "success", StringComparison.OrdinalIgnoreCase))
        {
            await WaitForCopyAsync(to, Header(response, "x-ms-copy-id"), name, size, progress, clock, ct);
        }

        progress?.Report(new FileProgress(size, size, name, clock.Elapsed));
    }

    /// <summary>Follows a copy the service runs in the background, backing off up to five seconds between looks.</summary>
    private async Task WaitForCopyAsync(
        AzureBlobLocation to, string? copyId, string name, long size,
        IProgress<FileProgress>? progress, Stopwatch clock, CancellationToken ct)
    {
        var delay = CopyPollInterval;
        try
        {
            while (true)
            {
                await Task.Delay(delay, ct);
                var state = await GetPropertiesAsync(to.Container!, to.Blob, name, ct)
                            ?? throw new IOException($"The copy of \"{name}\" disappeared before it finished.");
                if (copyId is not null && state.CopyId is not null && state.CopyId != copyId)
                {
                    throw new IOException($"Another copy to \"{LeafName(to.Blob)}\" replaced this one.");
                }

                switch (state.CopyStatus?.ToLowerInvariant())
                {
                    case "success":
                        return;
                    case "pending":
                        if (progress is not null && TryParseCopyProgress(state.CopyProgress, out var copied, out var total))
                        {
                            progress.Report(new FileProgress(copied, total > 0 ? total : size, name, clock.Elapsed));
                        }

                        break;
                    default:
                        throw new IOException(
                            $"Azure could not copy \"{name}\": {state.CopyStatusDescription ?? state.CopyStatus ?? "the copy stopped"}.");
                }

                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxCopyPollInterval.Ticks));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && copyId is not null)
        {
            // Left alone, the service would finish the copy the user just cancelled.
            await TryAbortCopyAsync(to, copyId);
            throw;
        }
    }

    private async Task TryAbortCopyAsync(AzureBlobLocation to, string copyId)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put,
                _endpoint.Blob(to.Container!, to.Blob, "comp=copy&copyid=" + Uri.EscapeDataString(copyId))) { Content = Empty() };
            request.Headers.TryAddWithoutValidation("x-ms-copy-action", "abort");
            using var response = await SendMetadataAsync(request, CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or IOException)
        {
            // Best effort: the cancellation is what gets reported.
        }
    }

    /// <summary>Reads <c>x-ms-copy-progress</c>: "bytes copied/total bytes".</summary>
    private static bool TryParseCopyProgress(string? text, out long copied, out long total)
    {
        copied = total = 0;
        var slash = text?.IndexOf('/') ?? -1;
        return slash > 0
               && long.TryParse(text![..slash], NumberStyles.None, CultureInfo.InvariantCulture, out copied)
               && long.TryParse(text[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out total);
    }

    // ── Links ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A link anyone can open to read <paramref name="path"/> until <paramref name="expiry"/> from now:
    /// a service SAS (read only, this one blob) signed with the account key. Put it on the clipboard as
    /// <see cref="Uri.AbsoluteUri"/>; <see cref="Uri.ToString"/> would un-escape it.
    /// </summary>
    /// <exception cref="NotSupportedException">The connection uses a SAS token or no credentials: there is no key to sign with.</exception>
    public Uri CreateReadLink(VfsPath path, TimeSpan expiry)
    {
        if (_accountKey is null)
        {
            throw new NotSupportedException(UsesSasToken
                ? "A shareable link is signed with the account key, and this connection uses a SAS token."
                : "A shareable link is signed with the account key, and this connection has none.");
        }

        if (expiry <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(expiry), expiry, "A link must stay valid for some time.");
        }

        var location = _endpoint.Locate(path);
        if (location.IsServiceRoot || location.IsContainerRoot)
        {
            throw new ArgumentException("Only a file can be shared by a link.", nameof(path));
        }

        var query = AzureSharedKey.BlobReadSasQuery(
            _endpoint.Account, _accountKey, location.Container!, location.Blob, (Clock() + expiry).UtcDateTime, _endpoint.IsHttps);
        return _endpoint.Blob(location.Container!, location.Blob, query, includeSas: false);
    }

    public bool SupportsOperation(VfsOperation operation) => operation switch
    {
        // Everything the panel needs; the recycle bin is a local-disk concept.
        VfsOperation.Read or VfsOperation.Write or VfsOperation.Delete
            or VfsOperation.CreateDirectory or VfsOperation.Rename
            or VfsOperation.Move or VfsOperation.Copy => true,
        _ => false,
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    private static HttpClient CreateClient()
    {
        // No decompression: a blob stored with Content-Encoding: gzip is copied as the bytes it is.
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = DecompressionMethods.None,
        };

        // No overall timeout: HttpClient.Timeout limits the whole exchange and would cut off any
        // transfer longer than it. Metadata calls get their own.
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static byte[] DecodeAccountKey(string secret)
    {
        try
        {
            return Convert.FromBase64String(secret);
        }
        catch (FormatException)
        {
            throw new ArgumentException(
                "The account key is not valid: paste key1 or key2 from the storage account's Access keys, or a SAS token.");
        }
    }

    /// <summary>Stamps the request with the date and API version, signs it when there is a key, and sends it.</summary>
    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completion, CancellationToken ct)
    {
        request.Headers.TryAddWithoutValidation("x-ms-date", Clock().UtcDateTime.ToString("R", CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("x-ms-version", AzureSharedKey.ApiVersion);
        if (_accountKey is not null)
        {
            AzureSharedKey.Authorize(request, _endpoint.Account, _accountKey);
        }

        return _client.SendAsync(request, completion, ct);
    }

    /// <summary>Sends a short request under <see cref="MetadataTimeout"/>.</summary>
    private async Task<HttpResponseMessage> SendMetadataAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(MetadataTimeout);
        try
        {
            return await SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Azure Blob Storage did not answer within {MetadataTimeout.TotalSeconds:0} seconds.");
        }
    }

    /// <summary>Throws an <see cref="AzureStorageException"/> that says what went wrong, unless the request succeeded.</summary>
    private async Task EnsureSuccessAsync(HttpResponseMessage response, string? subject, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? body = null;
        try
        {
            // Empty for a HEAD: then the code comes from x-ms-error-code alone.
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // The status and the header still say enough.
        }

        var error = AzureBlobResponse.ParseError(body);
        var code = ErrorCode(response) ?? error.Code;
        throw new AzureStorageException(
            AzureStorageException.Describe(response.StatusCode, code, error.Message, error.AuthenticationDetail, subject, UsesSasToken),
            response.StatusCode, code);
    }

    private AzureStorageException NotFound(string subject) =>
        new(AzureStorageException.Describe(HttpStatusCode.NotFound, "BlobNotFound", null, null, subject, UsesSasToken),
            HttpStatusCode.NotFound, "BlobNotFound");

    private AzureStorageException ContainerMissing(string container) =>
        new(AzureStorageException.Describe(HttpStatusCode.NotFound, "ContainerNotFound", null, null, container, UsesSasToken),
            HttpStatusCode.NotFound, "ContainerNotFound");

    private static IOException AlreadyExists(string name) => new($"\"{name}\" already exists.");

    /// <summary>The answer to a conditional write (<c>If-None-Match: *</c>) whose target was there.</summary>
    private static bool IsAlreadyExists(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.PreconditionFailed
        || (response.StatusCode == HttpStatusCode.Conflict && ErrorCode(response) is "BlobAlreadyExists" or "PathAlreadyExists");

    private static string? ErrorCode(HttpResponseMessage response) => Header(response, "x-ms-error-code");

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static ByteArrayContent Empty() => new([]);

    /// <summary>The last segment of a blob name: what the panel shows.</summary>
    private static string LeafName(string blob)
    {
        var trimmed = blob.TrimEnd('/');
        return trimmed[(trimmed.LastIndexOf('/') + 1)..];
    }

    /// <summary>The location of a blob; the account root and a container's top level are not files.</summary>
    private AzureBlobLocation RequireBlob(VfsPath path)
    {
        var location = _endpoint.Locate(path);
        if (location.IsServiceRoot || location.IsContainerRoot)
        {
            throw new IOException($"\"{path.Value}\" is a container or the account root, not a file.");
        }

        return location;
    }

    private static Task ForEachAsync<T>(IEnumerable<T> items, CancellationToken ct, Func<T, CancellationToken, Task> body) =>
        Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
            async (item, token) => await body(item, token));

    /// <summary>3 to 63 lower-case letters, digits and single hyphens, starting and ending with a letter or digit.</summary>
    [GeneratedRegex("^(?=.{3,63}$)[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ContainerName();
}
