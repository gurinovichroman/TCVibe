using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using TCV.Core.Domain;
using TCV.Network.Cloud;

namespace TCV.Network.Tests.Cloud;

/// <summary>What <see cref="DropboxFileSystem"/> asks the Dropbox API v2 and how it reads the answers.</summary>
public sealed class DropboxFileSystemTests
{
    private static string Endpoint(Recorded r) => r.Uri.AbsolutePath["/2/".Length..];

    private static JsonElement Arg(Recorded r) => JsonDocument.Parse(r.Header("Dropbox-API-Arg")!).RootElement.Clone();

    private static HttpResponseMessage Conflict(string summary) =>
        Kit.Json($$$"""{"error_summary":"{{{summary}}}","error":{".tag":"path"}}""", HttpStatusCode.Conflict);

    private static HttpResponseMessage File(string name, long size = 10, string rev = "r1") =>
        Kit.Json($$"""{".tag":"file","name":"{{name}}","size":{{size}},"rev":"{{rev}}","client_modified":"2026-09-01T08:00:00Z","server_modified":"2026-09-02T08:00:00Z"}""");

    private static HttpResponseMessage Download(byte[] data, string rev, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = Kit.Bytes(data, status);
        response.Headers.TryAddWithoutValidation("Dropbox-API-Result", $$"""{"name":"big.bin","rev":"{{rev}}","size":10}""");
        return response;
    }

    // ── The argument header ──────────────────────────────────────────────────

    [Fact]
    public void Serialize_NonAsciiName_IsEscapedToPlainAscii()
    {
        var header = DropboxApiArg.Serialize(new JsonObject { ["path"] = "/Отчёт 😀.txt" });

        header.Should().MatchRegex("^[\\x20-\\x7E]+$", "an HTTP header carries ASCII only");
        header.Should().Contain("\\u041E").And.Contain("\\uD83D\\uDE00");
        JsonDocument.Parse(header).RootElement.GetProperty("path").GetString().Should().Be("/Отчёт 😀.txt");
    }

    // ── Listing ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_HasMore_ContinuesWithTheCursorAndSkipsDeletedEntries()
    {
        var http = new CloudHttp(r => Endpoint(r) switch
        {
            "files/list_folder" => Kit.Json("""
                {"entries":[
                  {".tag":"folder","name":"src","path_display":"/src","id":"id:1"},
                  {".tag":"file","name":"a.txt","size":5,"client_modified":"2026-09-01T08:00:00Z","server_modified":"2026-09-02T08:00:00Z","rev":"1"},
                  {".tag":"deleted","name":"gone.txt"}
                ],"cursor":"c1","has_more":true}
                """),
            "files/list_folder/continue" => Kit.Json("""{"entries":[{".tag":"file","name":"b.txt","size":7,"rev":"2"}],"cursor":"c2","has_more":false}"""),
            _ => Kit.Unexpected(r),
        });
        using var fs = Kit.Dropbox(http);

        var entries = await fs.ListAsync(VfsPath.Root);

        entries.Select(e => (e.Path.Value, e.IsDirectory, e.Size)).Should().Equal(
            ("/src", true, 0L), ("/a.txt", false, 5L), ("/b.txt", false, 7L));
        entries[1].LastModified.Should().Be(new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero).LocalDateTime,
            "the file's own time (client_modified) is shown, not the upload's");
        http.Api[0].Json.GetProperty("path").GetString().Should().Be(string.Empty, "Dropbox's root is the empty path");
        http.Api[1].Json.GetProperty("cursor").GetString().Should().Be("c1");
    }

    [Fact]
    public async Task GetEntryAsync_NotFound_ReturnsNull()
    {
        using var fs = Kit.Dropbox(new CloudHttp(_ => Conflict("path/not_found/..")));

        (await fs.GetEntryAsync(new VfsPath("/nope.txt"))).Should().BeNull();
    }

    [Fact]
    public async Task GetEntryAsync_OtherRefusal_ThrowsInsteadOfReportingMissing()
    {
        using var fs = Kit.Dropbox(new CloudHttp(_ => Conflict("path/restricted_content/.")));

        var act = () => fs.GetEntryAsync(new VfsPath("/x.txt"));

        (await act.Should().ThrowAsync<CloudDriveException>()).Which.ErrorCode.Should().Be("path/restricted_content");
    }

    // ── Downloads ────────────────────────────────────────────────────────────

    [Fact]
    public async Task OpenReadFromAsync_SameRevision_ResumesAtTheOffset()
    {
        var http = new CloudHttp(r => Download("456789"u8.ToArray(), "r1", HttpStatusCode.PartialContent));
        using var fs = Kit.Dropbox(http);

        var (stream, offset, total, validator) = await fs.OpenReadFromAsync(new VfsPath("/big.bin"), 4, "r1", CancellationToken.None);
        var rest = await Kit.ReadAllAsync(stream);

        (offset, total, validator).Should().Be((4L, 10L, "r1"));
        rest.Should().Equal("456789"u8.ToArray());
        var request = http.Api.Single();
        request.Uri.Should().Be(new Uri("https://content.dropboxapi.com/2/files/download"));
        request.Header("Range").Should().Be("bytes=4-");
        Arg(request).GetProperty("path").GetString().Should().Be("/big.bin");
    }

    [Fact]
    public async Task OpenReadFromAsync_RevisionChanged_StartsOverFromTheFirstByte()
    {
        var http = new CloudHttp(r => r.Header("Range") is null
            ? Download("0123456789"u8.ToArray(), "r2")
            : Download("456789"u8.ToArray(), "r2", HttpStatusCode.PartialContent));
        using var fs = Kit.Dropbox(http);

        var (stream, offset, _, validator) = await fs.OpenReadFromAsync(new VfsPath("/big.bin"), 4, "r1", CancellationToken.None);
        var all = await Kit.ReadAllAsync(stream);

        offset.Should().Be(0, "the rest of a new revision would not fit the start of the old one");
        validator.Should().Be("r2");
        all.Should().Equal("0123456789"u8.ToArray());
    }

    // ── Uploads ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task OpenWriteAsync_SmallFile_UploadsInAddModeWithAnAsciiArgument()
    {
        var http = new CloudHttp(r => Endpoint(r) switch
        {
            "files/get_metadata" => Conflict("path/not_found/."),
            "files/upload" => File("Новый.txt"),
            _ => Kit.Unexpected(r),
        });
        using var fs = Kit.Dropbox(http);

        await Kit.UploadAsync(fs, "/Новый.txt", "data"u8.ToArray());

        var upload = http.Api.Single(r => Endpoint(r) == "files/upload");
        upload.Header("Dropbox-API-Arg").Should().MatchRegex("^[\\x20-\\x7E]+$");
        var arg = Arg(upload);
        arg.GetProperty("path").GetString().Should().Be("/Новый.txt");
        arg.GetProperty("mode").GetString().Should().Be("add", "without overwrite Dropbox itself must refuse to replace");
        arg.GetProperty("autorename").GetBoolean().Should().BeFalse();
        upload.Header("Content-Type").Should().Be("application/octet-stream");
        upload.Text.Should().Be("data");
    }

    [Fact]
    public async Task OpenWriteAsync_LargeFile_GoesThroughAnUploadSession()
    {
        var http = new CloudHttp(r => Endpoint(r) switch
        {
            "files/get_metadata" => Conflict("path/not_found/."),
            "files/upload_session/start" => Kit.Json("""{"session_id":"s1"}"""),
            "files/upload_session/append_v2" => Kit.Json("null"),
            "files/upload_session/finish" => File("big.bin"),
            _ => Kit.Unexpected(r),
        });
        using var fs = new DropboxFileSystem(Kit.Connection(NetworkProtocol.Dropbox), new HttpClient(http))
        {
            SimpleUploadLimit = 4,
            ChunkSize = 4,
        };

        await Kit.UploadAsync(fs, "/big.bin", "0123456789"u8.ToArray(), overwrite: true);

        var session = http.Api.Where(r => Endpoint(r).StartsWith("files/upload_session", StringComparison.Ordinal)).ToList();
        session.Select(Endpoint).Should().Equal(
            "files/upload_session/start", "files/upload_session/append_v2", "files/upload_session/finish");
        session.Select(r => r.Text).Should().Equal("0123", "4567", "89");
        Arg(session[1]).GetProperty("cursor").GetProperty("offset").GetInt64().Should().Be(4);
        var finish = Arg(session[2]);
        finish.GetProperty("cursor").GetProperty("session_id").GetString().Should().Be("s1");
        finish.GetProperty("cursor").GetProperty("offset").GetInt64().Should().Be(8);
        finish.GetProperty("commit").GetProperty("mode").GetString().Should().Be("overwrite");
    }

    [Fact]
    public async Task CommitAsync_FileAppearedMeanwhile_ThrowsAlreadyExists()
    {
        var http = new CloudHttp(r => Endpoint(r) switch
        {
            "files/get_metadata" => Conflict("path/not_found/."),
            "files/upload" => Conflict("path/conflict/file/.."),
            _ => Kit.Unexpected(r),
        });
        using var fs = Kit.Dropbox(http);

        var act = () => Kit.UploadAsync(fs, "/a.txt", "x"u8.ToArray());

        await act.Should().ThrowAsync<IOException>().WithMessage("\"a.txt\" already exists.");
    }

    [Fact]
    public async Task OpenWriteAsync_ExistsWithoutOverwrite_Refuses()
    {
        var http = new CloudHttp(r => Endpoint(r) == "files/get_metadata" ? File("a.txt") : Kit.Unexpected(r));
        using var fs = Kit.Dropbox(http);

        var act = () => fs.OpenWriteAsync(new VfsPath("/a.txt"));

        await act.Should().ThrowAsync<IOException>().WithMessage("\"a.txt\" already exists.");
    }

    // ── Changes ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task MoveAsync_FileInTheWay_GoesToDeletedFilesBeforeTheMoveIsRepeated()
    {
        var moves = 0;
        var http = new CloudHttp(r => Endpoint(r) switch
        {
            "files/move_v2" => Interlocked.Increment(ref moves) == 1 ? Conflict("to/conflict/file/..") : Kit.Json("""{"metadata":{}}"""),
            "files/get_metadata" => File("a.txt"),
            "files/delete_v2" => Kit.Json("""{"metadata":{}}"""),
            _ => Kit.Unexpected(r),
        });
        using var fs = Kit.Dropbox(http);

        await fs.MoveAsync(new VfsPath("/a.txt"), new VfsPath("/dst/a.txt"), CancellationToken.None);

        http.Api.Select(Endpoint).Should().Equal("files/move_v2", "files/get_metadata", "files/delete_v2", "files/move_v2");
        http.Api[2].Json.GetProperty("path").GetString().Should().Be("/dst/a.txt");
    }

    [Fact]
    public async Task MoveAsync_OnlyTheCaseDiffers_NeverDeletesTheSource()
    {
        var http = new CloudHttp(r => Endpoint(r) == "files/move_v2" ? Conflict("to/conflict/file/..") : Kit.Unexpected(r));
        using var fs = Kit.Dropbox(http);

        var act = () => fs.MoveAsync(new VfsPath("/readme.md"), new VfsPath("/README.md"), CancellationToken.None);

        await act.Should().ThrowAsync<IOException>().WithMessage("*already exists*");
        http.Api.Should().NotContain(r => Endpoint(r) == "files/delete_v2", "Dropbox paths ignore case: the file in the way is the source");
    }

    [Fact]
    public async Task DeleteAsync_Folder_GoesToDeletedFilesInOneCall()
    {
        var http = new CloudHttp(r => Endpoint(r) == "files/delete_v2" ? Kit.Json("""{"metadata":{}}""") : Kit.Unexpected(r));
        using var fs = Kit.Dropbox(http);

        await fs.DeleteAsync(new VfsPath("/old"), recursive: true, CancellationToken.None);

        http.Api.Should().ContainSingle().Which.Json.GetProperty("path").GetString().Should().Be("/old");
    }

    [Fact]
    public async Task CreateDirectoryAsync_FolderAlreadyThere_IsLeftAsItIs()
    {
        using var fs = Kit.Dropbox(new CloudHttp(_ => Conflict("path/conflict/folder/..")));

        var act = () => fs.CreateDirectoryAsync(new VfsPath("/src"), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
