using System.Net;
using System.Text;
using FluentAssertions;
using TCV.Core.Domain;
using TCV.Network.Cloud;

namespace TCV.Network.Tests.Cloud;

/// <summary>What <see cref="GoogleDriveFileSystem"/> asks the Drive API v3 and how it reads the answers.</summary>
public sealed class GoogleDriveFileSystemTests
{
    private const string Files = "https://www.googleapis.com/drive/v3/files";
    private const string Folder = GoogleDriveFileSystem.FolderMime;

    private static string Item(string id, string name, string mime = "text/plain", long? size = 10,
        string created = "2026-01-01T00:00:00Z", string? revision = "rev1") =>
        $$$"""{"id":"{{{id}}}","name":"{{{name}}}","mimeType":"{{{mime}}}"{{{(size is null ? "" : $",\"size\":\"{size}\"")}}},"modifiedTime":"2026-09-02T10:00:00.000Z","createdTime":"{{{created}}}"{{{(revision is null ? "" : $",\"headRevisionId\":\"{revision}\"")}}}}""";

    private static HttpResponseMessage Found(params string[] items) => Kit.Json($$"""{"files":[{{string.Join(',', items)}}]}""");

    private static bool IsQuery(Recorded r) => r.Method == HttpMethod.Get && r.Uri.GetLeftPart(UriPartial.Path) == Files;

    // ── Listing and paths ────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_TwoPages_ReturnsEveryShownItemWithItsPath()
    {
        var http = new CloudHttp(r => r.Query["pageToken"] switch
        {
            null => Kit.Json($$"""{"files":[{{Item("f1", "src", Folder, size: null)}},{{Item("d1", "notes.txt", size: 12)}}],"nextPageToken":"page-2"}"""),
            "page-2" => Found(
                Item("g1", "Plan", "application/vnd.google-apps.document", size: null),
                Item("x1", "Survey", "application/vnd.google-apps.form", size: null)),
            _ => Kit.Unexpected(r),
        });
        using var fs = Kit.Google(http);

        var entries = await fs.ListAsync(VfsPath.Root);

        entries.Select(e => (e.Path.Value, e.Name, e.IsDirectory, e.Size)).Should().BeEquivalentTo(new[]
        {
            ("/src", "src", true, 0L),
            ("/notes.txt", "notes.txt", false, 12L),
            ("/Plan.docx", "Plan.docx", false, 0L),
        }, "a Google document is listed as its export, and a form, which has none, is left out");
        entries.Single(e => e.Name == "notes.txt").LastModified
            .Should().Be(new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero).LocalDateTime);
        http.Api.Should().HaveCount(2);
        http.Api[0].Query["q"].Should().Be("'root' in parents and trashed = false");
        http.Api[0].Query["pageSize"].Should().Be("1000");
        http.Api[1].Query["pageToken"].Should().Be("page-2");
    }

    [Fact]
    public async Task ListAsync_SameNameTwice_ShowsTheOlderItemOnly()
    {
        var http = new CloudHttp(_ => Found(
            Item("new", "a.txt", size: 2, created: "2026-05-01T00:00:00Z"),
            Item("old", "a.txt", size: 1, created: "2026-01-01T00:00:00Z")));
        using var fs = Kit.Google(http);

        var entries = await fs.ListAsync(VfsPath.Root);

        entries.Should().ContainSingle().Which.Size.Should().Be(1, "the oldest of the same-named items is the one a path names");
    }

    [Fact]
    public async Task GetEntryAsync_NestedPath_ResolvesNameByNameAndRemembersTheFolders()
    {
        var http = new CloudHttp(r =>
        {
            var q = r.Query["q"]!;
            return q switch
            {
                _ when q.StartsWith("'root' in parents", StringComparison.Ordinal) && q.Contains("name = 'a'") => Found(Item("idA", "a", Folder, null)),
                _ when q.StartsWith("'idA' in parents", StringComparison.Ordinal) && q.Contains("name = 'b'") => Found(Item("idB", "b", Folder, null)),
                _ when q.StartsWith("'idB' in parents", StringComparison.Ordinal) && q.Contains("name = 'c.txt'") => Found(Item("idC", "c.txt", size: 7)),
                _ when q.StartsWith("'idB' in parents", StringComparison.Ordinal) => Found(),
                _ => Kit.Unexpected(r),
            };
        });
        using var fs = Kit.Google(http);

        var found = await fs.GetEntryAsync(new VfsPath("/a/b/c.txt"));
        var missing = await fs.GetEntryAsync(new VfsPath("/a/b/d.txt"));

        found.Should().NotBeNull();
        found!.Path.Value.Should().Be("/a/b/c.txt");
        found.Size.Should().Be(7);
        missing.Should().BeNull();
        http.Api.Should().HaveCount(4, "the folders of the second path are known by then: only its last name is looked up");
    }

    [Fact]
    public async Task GetEntryAsync_ServerError_ThrowsInsteadOfReportingMissing()
    {
        var http = new CloudHttp(_ => Kit.Json(
            """{"error":{"code":500,"message":"Backend Error","errors":[{"reason":"backendError"}]}}""", HttpStatusCode.InternalServerError));
        using var fs = Kit.Google(http);

        var act = () => fs.GetEntryAsync(new VfsPath("/a.txt"));

        (await act.Should().ThrowAsync<CloudDriveException>())
            .Which.Message.Should().Be("Google Drive: Backend Error. (HTTP 500, backendError)");
    }

    [Fact]
    public void Quote_ApostropheAndBackslash_AreEscapedForTheQuery()
    {
        GoogleDriveFileSystem.Quote(@"O'Brien\notes").Should().Be(@"'O\'Brien\\notes'");
    }

    [Fact]
    public async Task OpenReadAsync_GoogleDocument_ReadsItsOfficeExport()
    {
        var http = new CloudHttp(r => IsQuery(r)
            ? Found(Item("g1", "Plan", "application/vnd.google-apps.document", size: null))
            : r.Uri.AbsolutePath.EndsWith("/files/g1/export", StringComparison.Ordinal)
                ? Kit.Bytes("docx"u8.ToArray())
                : Kit.Unexpected(r));
        using var fs = Kit.Google(http);

        var bytes = await Kit.ReadAllAsync(await fs.OpenReadAsync(new VfsPath("/Plan.docx")));

        bytes.Should().Equal("docx"u8.ToArray());
        http.Api[0].Query["q"].Should().Contain("(name = 'Plan' and mimeType = 'application/vnd.google-apps.document')");
        http.Api[1].Query["mimeType"].Should().Be("application/vnd.openxmlformats-officedocument.wordprocessingml.document");
    }

    // ── Ranged reads ─────────────────────────────────────────────────────────

    [Fact]
    public async Task OpenReadFromAsync_SameRevision_AsksForTheRestOnly()
    {
        var http = new CloudHttp(r => IsQuery(r)
            ? Found(Item("id1", "big.bin", size: 10, revision: "rev1"))
            : Kit.Bytes("456789"u8.ToArray(), HttpStatusCode.PartialContent));
        using var fs = Kit.Google(http);

        var (stream, offset, total, validator) = await fs.OpenReadFromAsync(new VfsPath("/big.bin"), 4, "rev1", CancellationToken.None);
        await stream.DisposeAsync();

        (offset, total, validator).Should().Be((4L, 10L, "rev1"));
        var download = http.Api[1];
        download.Uri.AbsolutePath.Should().Be("/drive/v3/files/id1");
        download.Query["alt"].Should().Be("media");
        download.Header("Range").Should().Be("bytes=4-");
    }

    [Fact]
    public async Task OpenReadFromAsync_RevisionChanged_StartsFromTheFirstByte()
    {
        var http = new CloudHttp(r => IsQuery(r)
            ? Found(Item("id1", "big.bin", size: 10, revision: "rev2"))
            : Kit.Bytes(new byte[10]));
        using var fs = Kit.Google(http);

        var (stream, offset, _, validator) = await fs.OpenReadFromAsync(new VfsPath("/big.bin"), 4, "rev1", CancellationToken.None);
        await stream.DisposeAsync();

        offset.Should().Be(0);
        validator.Should().Be("rev2");
        http.Api[1].Header("Range").Should().BeNull("the part already here belongs to another version");
    }

    // ── Uploads ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task OpenWriteAsync_SmallNewFile_SendsMetadataAndBytesInOneMultipartRequest()
    {
        var http = new CloudHttp(r => IsQuery(r) ? Found() : Kit.Json("""{"id":"new"}"""));
        using var fs = Kit.Google(http);

        await Kit.UploadAsync(fs, "/hello.txt", "hello"u8.ToArray());

        var upload = http.Api.Should().ContainSingle(r => r.Method == HttpMethod.Post).Subject;
        upload.Uri.GetLeftPart(UriPartial.Path).Should().Be("https://www.googleapis.com/upload/drive/v3/files");
        upload.Query["uploadType"].Should().Be("multipart");
        upload.Header("Content-Type").Should().StartWith("multipart/related");
        upload.Text.Should().Contain("""{"name":"hello.txt","parents":["root"]}""").And.Contain("hello");
    }

    [Fact]
    public async Task OpenWriteAsync_LargeFile_SendsChunksAndResumesWhereDriveStopped()
    {
        const string session = "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&upload_id=xyz";
        var http = new CloudHttp(r =>
        {
            if (IsQuery(r))
            {
                return Found();
            }

            if (r.Method == HttpMethod.Post)
            {
                var opened = Kit.Json("{}");
                opened.Headers.Location = new Uri(session);
                return opened;
            }

            return r.Header("Content-Range") switch
            {
                "bytes 0-3/10" => Incomplete("bytes=0-3"),
                "bytes 4-7/10" => Incomplete("bytes=0-5"), // only half of this chunk arrived
                "bytes 6-9/10" => Kit.Json("""{"id":"new"}"""),
                _ => Kit.Unexpected(r),
            };
        });
        using var fs = new GoogleDriveFileSystem(Kit.Connection(NetworkProtocol.GoogleDrive), new HttpClient(http))
        {
            MultipartLimit = 4,
            ChunkSize = 4,
        };

        await Kit.UploadAsync(fs, "/big.bin", "0123456789"u8.ToArray());

        var open = http.Api.Single(r => r.Method == HttpMethod.Post);
        open.Query["uploadType"].Should().Be("resumable");
        open.Header("X-Upload-Content-Length").Should().Be("10");
        http.Api.Where(r => r.Method == HttpMethod.Put).Select(r => r.Text)
            .Should().Equal(["0123", "4567", "6789"], "the third chunk starts where Drive said it stopped");
    }

    [Fact]
    public async Task OpenWriteAsync_FileExistsWithoutOverwrite_RefusesBeforeCollectingTheBody()
    {
        var http = new CloudHttp(r => IsQuery(r) ? Found(Item("id1", "a.txt")) : Kit.Unexpected(r));
        using var fs = Kit.Google(http);

        var act = () => fs.OpenWriteAsync(new VfsPath("/a.txt"), overwrite: false);

        await act.Should().ThrowAsync<IOException>().WithMessage("\"a.txt\" already exists.");
        http.Api.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task OpenWriteAsync_FileExistsWithOverwrite_ReplacesTheBytesOfThatFile()
    {
        var http = new CloudHttp(r => IsQuery(r) ? Found(Item("id1", "a.txt")) : Kit.Json("""{"id":"id1"}"""));
        using var fs = Kit.Google(http);

        await Kit.UploadAsync(fs, "/a.txt", "new"u8.ToArray(), overwrite: true);

        var update = http.Api.Single(r => r.Method != HttpMethod.Get);
        update.Method.Should().Be(HttpMethod.Patch, "updating keeps the file's id, sharing and history instead of making a twin");
        update.Uri.AbsolutePath.Should().Be("/upload/drive/v3/files/id1");
        update.Query["uploadType"].Should().Be("media");
        update.Text.Should().Be("new");
    }

    [Fact]
    public async Task OpenWriteAsync_OverAGoogleDocument_IsRefused()
    {
        var http = new CloudHttp(r => IsQuery(r)
            ? Found(Item("g1", "Plan", "application/vnd.google-apps.document", size: null))
            : Kit.Unexpected(r));
        using var fs = Kit.Google(http);

        var act = () => fs.OpenWriteAsync(new VfsPath("/Plan.docx"), overwrite: true);

        await act.Should().ThrowAsync<IOException>().WithMessage("*Google document*cannot be overwritten*");
    }

    // ── Changes ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_File_MovesItToTheTrash()
    {
        var http = new CloudHttp(r => IsQuery(r) ? Found(Item("id1", "a.txt")) : Kit.Json("""{"id":"id1"}"""));
        using var fs = Kit.Google(http);
        var changes = new List<VfsChangedEventArgs>();
        fs.Changed += (_, e) => changes.Add(e);

        await fs.DeleteAsync(new VfsPath("/a.txt"), recursive: true, CancellationToken.None);

        var trash = http.Api.Single(r => r.Method != HttpMethod.Get);
        trash.Method.Should().Be(HttpMethod.Patch, "a delete goes to the trash, never straight to oblivion");
        trash.Uri.AbsolutePath.Should().Be("/drive/v3/files/id1");
        trash.Json.GetProperty("trashed").GetBoolean().Should().BeTrue();
        changes.Should().ContainSingle(e => e.ChangeType == WatcherChangeTypes.Deleted && e.Path.Value == "/a.txt");
    }

    [Fact]
    public async Task MoveAsync_ToAnotherFolder_SwapsTheParentsAndTrashesTheFileItReplaces()
    {
        var http = new CloudHttp(r =>
        {
            var q = r.Query["q"] ?? string.Empty;
            return r.Method == HttpMethod.Get
                ? q switch
                {
                    _ when q.Contains("name = 'a.txt'") && q.StartsWith("'root'", StringComparison.Ordinal) => Found(Item("src", "a.txt")),
                    _ when q.Contains("name = 'dst'") => Found(Item("idDst", "dst", Folder, null)),
                    _ when q.Contains("name = 'b.txt'") => Found(Item("old", "b.txt")),
                    _ => Found(),
                }
                : Kit.Json("""{"id":"x"}""");
        });
        using var fs = Kit.Google(http);

        await fs.MoveAsync(new VfsPath("/a.txt"), new VfsPath("/dst/b.txt"), CancellationToken.None);

        var patches = http.Api.Where(r => r.Method == HttpMethod.Patch).ToList();
        patches.Should().HaveCount(2);
        patches[0].Uri.AbsolutePath.Should().Be("/drive/v3/files/src");
        patches[0].Query["addParents"].Should().Be("idDst");
        patches[0].Query["removeParents"].Should().Be("root");
        patches[0].Json.GetProperty("name").GetString().Should().Be("b.txt");
        patches[1].Uri.AbsolutePath.Should().Be("/drive/v3/files/old", "the replaced file goes to the trash only once the move has landed");
        patches[1].Json.GetProperty("trashed").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task CreateDirectoryAsync_FolderAlreadyThere_MakesNoTwin()
    {
        var http = new CloudHttp(r => IsQuery(r) ? Found(Item("idA", "a", Folder, null)) : Kit.Unexpected(r));
        using var fs = Kit.Google(http);

        await fs.CreateDirectoryAsync(new VfsPath("/a"), CancellationToken.None);

        http.Api.Should().OnlyContain(r => r.Method == HttpMethod.Get, "Drive would happily keep two folders of one name");
    }

    [Fact]
    public async Task Request_RateLimitedWith403_IsSentAgain()
    {
        var calls = 0;
        var http = new CloudHttp(_ => Interlocked.Increment(ref calls) == 1
            ? Kit.Json("""{"error":{"code":403,"message":"Rate Limit Exceeded","errors":[{"reason":"userRateLimitExceeded"}]}}""", HttpStatusCode.Forbidden)
            : Found());
        using var fs = Kit.Google(http);

        var entries = await fs.ListAsync(VfsPath.Root);

        entries.Should().BeEmpty();
        http.Api.Should().HaveCount(2);
    }

    private static HttpResponseMessage Incomplete(string range)
    {
        var response = new HttpResponseMessage((HttpStatusCode)308) { Content = new StringContent(string.Empty, Encoding.UTF8) };
        response.Headers.TryAddWithoutValidation("Range", range);
        return response;
    }
}
