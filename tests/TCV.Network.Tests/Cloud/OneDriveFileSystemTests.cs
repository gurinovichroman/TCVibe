using System.Net;
using FluentAssertions;
using TCV.Core.Domain;
using TCV.Core.Interfaces;
using TCV.Network.Cloud;

namespace TCV.Network.Tests.Cloud;

/// <summary>
/// What <see cref="OneDriveFileSystem"/> asks Microsoft Graph, how it reads the answers, and the
/// request pipeline all three drives share (token refresh, throttling, timeouts).
/// </summary>
public sealed class OneDriveFileSystemTests
{
    private const string Graph = "https://graph.microsoft.com/v1.0";

    private static HttpResponseMessage NotFound() =>
        Kit.Json("""{"error":{"code":"itemNotFound","message":"The resource could not be found."}}""", HttpStatusCode.NotFound);

    private static HttpResponseMessage FileItem(string name, long size = 10, string cTag = "c1", string? downloadUrl = null) =>
        Kit.Json($$"""
            {"id":"id-{{name}}","name":"{{name}}","size":{{size}},"cTag":"{{cTag}}","eTag":"e1","file":{},
             "fileSystemInfo":{"lastModifiedDateTime":"2026-09-01T08:00:00Z"},"lastModifiedDateTime":"2026-09-03T08:00:00Z"
             {{(downloadUrl is null ? "" : $",\"@microsoft.graph.downloadUrl\":\"{downloadUrl}\"")}}
            }
            """);

    // ── Listing and stat ─────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_NextLink_FollowsEveryPage()
    {
        var http = new CloudHttp(r => r.Uri.AbsolutePath switch
        {
            "/v1.0/me/drive/root:/My%20Docs:/children" => Kit.Json($$$"""
                {"value":[
                  {"name":"sub","folder":{"childCount":2},"size":999,"lastModifiedDateTime":"2026-09-01T08:00:00Z"},
                  {"name":"a b.txt","size":5,"file":{},"fileSystemInfo":{"lastModifiedDateTime":"2026-09-01T08:00:00Z"}}
                ],"@odata.nextLink":"{{{Graph}}}/me/drive/items/X/children?$skiptoken=abc"}
                """),
            "/v1.0/me/drive/items/X/children" => Kit.Json("""{"value":[{"name":"c.txt","size":7,"file":{}}]}"""),
            _ => Kit.Unexpected(r),
        });
        using var fs = Kit.OneDrive(http);

        var entries = await fs.ListAsync(new VfsPath("/My Docs"));

        entries.Select(e => (e.Path.Value, e.IsDirectory, e.Size)).Should().Equal(
            ("/My Docs/sub", true, 0L), ("/My Docs/a b.txt", false, 5L), ("/My Docs/c.txt", false, 7L));
        http.Api[0].Query["$top"].Should().Be("1000");
        http.Api[1].Query["$skiptoken"].Should().Be("abc", "the next page's address is used as Graph gave it");
    }

    [Fact]
    public async Task GetEntryAsync_ItemNotFound_ReturnsNull()
    {
        using var fs = Kit.OneDrive(new CloudHttp(_ => NotFound()));

        (await fs.GetEntryAsync(new VfsPath("/nope.txt"))).Should().BeNull();
    }

    [Fact]
    public async Task GetEntryAsync_AccessDenied_ThrowsWithTheProvidersWords()
    {
        using var fs = Kit.OneDrive(new CloudHttp(_ => Kit.Json(
            """{"error":{"code":"accessDenied","message":"Access denied."}}""", HttpStatusCode.Forbidden)));

        var act = () => fs.GetEntryAsync(new VfsPath("/x.txt"));

        (await act.Should().ThrowAsync<CloudDriveException>())
            .Which.Message.Should().Be("OneDrive: Access denied. (HTTP 403, accessDenied)");
    }

    // ── Downloads ────────────────────────────────────────────────────────────

    [Fact]
    public async Task OpenReadAsync_RedirectToTheContent_IsFollowedWithoutTheToken()
    {
        var http = new CloudHttp(r =>
        {
            if (r.Uri.Host == "public.example")
            {
                return Kit.Bytes("bytes"u8.ToArray());
            }

            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("https://public.example/dl/f.bin?tempauth=x");
            return redirect;
        });
        using var fs = Kit.OneDrive(http);

        var bytes = await Kit.ReadAllAsync(await fs.OpenReadAsync(new VfsPath("/f.bin")));

        bytes.Should().Equal("bytes"u8.ToArray());
        http.Api[0].Uri.AbsolutePath.Should().Be("/v1.0/me/drive/root:/f.bin:/content");
        http.Api[0].Bearer.Should().NotBeNull();
        http.Api[1].Bearer.Should().BeNull("a pre-authenticated address must never receive the bearer token");
    }

    [Fact]
    public async Task OpenReadFromAsync_SameCTag_ResumesFromTheDownloadAddress()
    {
        var http = new CloudHttp(r => r.Uri.Host == "public.example"
            ? Kit.Bytes("456789"u8.ToArray(), HttpStatusCode.PartialContent)
            : FileItem("f.bin", cTag: "c1", downloadUrl: "https://public.example/dl"));
        using var fs = Kit.OneDrive(http);

        var (stream, offset, total, validator) = await fs.OpenReadFromAsync(new VfsPath("/f.bin"), 4, "c1", CancellationToken.None);
        await stream.DisposeAsync();

        (offset, total, validator).Should().Be((4L, 10L, "c1"));
        http.Api[1].Header("Range").Should().Be("bytes=4-");
        http.Api[1].Bearer.Should().BeNull();
    }

    // ── Uploads ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task CommitAsync_SmallFileThatAppearedMeanwhile_IsRefusedByTheServer()
    {
        var http = new CloudHttp(r => r.Method == HttpMethod.Put
            ? Kit.Json("""{"error":{"code":"nameAlreadyExists","message":"Name already exists"}}""", HttpStatusCode.Conflict)
            : NotFound());
        using var fs = Kit.OneDrive(http);

        var act = () => Kit.UploadAsync(fs, "/a.txt", "x"u8.ToArray());

        await act.Should().ThrowAsync<IOException>().WithMessage("\"a.txt\" already exists.");
        var put = http.Api.Single(r => r.Method == HttpMethod.Put);
        put.Uri.AbsolutePath.Should().Be("/v1.0/me/drive/root:/a.txt:/content");
        put.Query["@microsoft.graph.conflictBehavior"].Should().Be("fail");
    }

    [Fact]
    public async Task OpenWriteAsync_LargeFile_SendsFragmentsToTheSessionWithoutTheToken()
    {
        var http = new CloudHttp(r =>
        {
            if (r.Method == HttpMethod.Get)
            {
                return NotFound();
            }

            if (r.Method == HttpMethod.Post)
            {
                return Kit.Json("""{"uploadUrl":"https://up.example/session/1","expirationDateTime":"2026-10-14T00:00:00Z"}""");
            }

            return r.Header("Content-Range") switch
            {
                "bytes 0-3/10" => Kit.Json("""{"nextExpectedRanges":["4-"]}""", HttpStatusCode.Accepted),
                "bytes 4-7/10" => Kit.Json("""{"nextExpectedRanges":["8-9"]}""", HttpStatusCode.Accepted),
                "bytes 8-9/10" => Kit.Json("""{"id":"new","name":"big.bin"}""", HttpStatusCode.Created),
                _ => Kit.Unexpected(r),
            };
        });
        using var fs = new OneDriveFileSystem(Kit.Connection(NetworkProtocol.OneDrive), new HttpClient(http))
        {
            SimpleUploadLimit = 4,
            FragmentSize = 4,
        };

        await Kit.UploadAsync(fs, "/big.bin", "0123456789"u8.ToArray());

        var session = http.Api.Single(r => r.Method == HttpMethod.Post);
        session.Uri.AbsolutePath.Should().Be("/v1.0/me/drive/root:/big.bin:/createUploadSession");
        session.Json.GetProperty("item").GetProperty("@microsoft.graph.conflictBehavior").GetString().Should().Be("fail");
        var fragments = http.Api.Where(r => r.Method == HttpMethod.Put).ToList();
        fragments.Select(r => r.Text).Should().Equal("0123", "4567", "89");
        fragments.Should().OnlyContain(r => r.Bearer == null, "the session address is pre-authenticated");
        http.Api.Should().NotContain(r => r.Method == HttpMethod.Delete, "a finished session is not cancelled");
    }

    [Fact]
    public async Task DisposeAsync_AbortedUpload_SendsNothingAndLeavesNoTemporaryFile()
    {
        var http = new CloudHttp(_ => NotFound());
        using var fs = Kit.OneDrive(http);

        var output = await fs.OpenWriteAsync(new VfsPath("/half.bin"), overwrite: true);
        await output.WriteAsync(new byte[] { 1, 2, 3 });
        var buffer = ((CloudUploadStream)output).BufferPath;
        ((IAbortableWriteStream)output).Abort();
        await output.DisposeAsync();

        http.Api.Should().OnlyContain(r => r.Method == HttpMethod.Get, "a half-written body must not be published as the file");
        File.Exists(buffer).Should().BeFalse();
    }

    // ── Changes ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_File_SendsOneDeleteToTheRecycleBin()
    {
        var http = new CloudHttp(r => r.Method == HttpMethod.Delete ? Kit.Status(HttpStatusCode.NoContent) : Kit.Unexpected(r));
        using var fs = Kit.OneDrive(http);

        await fs.DeleteAsync(new VfsPath("/a b.txt"), recursive: true, CancellationToken.None);

        http.Api.Should().ContainSingle().Which.Uri.AbsolutePath.Should().Be("/v1.0/me/drive/root:/a%20b.txt:");
    }

    [Fact]
    public async Task CopyAsync_ServerSideCopy_FollowsTheMonitorUntilItIsDone()
    {
        var polls = 0;
        var http = new CloudHttp(r =>
        {
            switch (r.Uri.AbsolutePath)
            {
                case "/v1.0/me/drive/root:/a.txt:" when r.Method == HttpMethod.Get:
                    return FileItem("a.txt", size: 3);
                case "/v1.0/me/drive/root" when r.Method == HttpMethod.Get:
                    return Kit.Json("""{"id":"root-id","name":"root","folder":{"childCount":1},"parentReference":{"driveId":"drive-1"}}""");
                case "/v1.0/me/drive/root:/b.txt:":
                    return NotFound();
                case "/v1.0/me/drive/root:/a.txt:/copy":
                    var started = Kit.Status(HttpStatusCode.Accepted);
                    started.Headers.Location = new Uri("https://monitor.example/m1");
                    return started;
                case "/m1":
                    return Interlocked.Increment(ref polls) == 1
                        ? Kit.Json("""{"status":"inProgress","percentageComplete":50}""", HttpStatusCode.Accepted)
                        : Kit.Json("""{"status":"completed","resourceId":"new"}""");
                default:
                    return Kit.Unexpected(r);
            }
        });
        using var fs = Kit.OneDrive(http);
        var reports = new List<FileProgress>();

        await fs.CopyAsync(new VfsPath("/a.txt"), new VfsPath("/b.txt"), new InlineProgress<FileProgress>(reports.Add), CancellationToken.None);

        var copy = http.Api.Single(r => r.Method == HttpMethod.Post);
        copy.Json.GetProperty("name").GetString().Should().Be("b.txt");
        copy.Json.GetProperty("parentReference").GetProperty("id").GetString().Should().Be("root-id");
        copy.Json.GetProperty("parentReference").GetProperty("driveId").GetString().Should().Be("drive-1");
        http.Api.Where(r => r.Uri.Host == "monitor.example").Should().HaveCount(2).And.OnlyContain(r => r.Bearer == null);
        reports.Last().BytesCopied.Should().Be(3);
    }

    [Fact]
    public async Task MoveAsync_OnlyTheCaseDiffers_NeverDeletesTheSource()
    {
        var http = new CloudHttp(r => r.Method == HttpMethod.Patch
            ? Kit.Json("""{"error":{"code":"nameAlreadyExists","message":"Name already exists"}}""", HttpStatusCode.Conflict)
            : Kit.Unexpected(r));
        using var fs = Kit.OneDrive(http);

        var act = () => fs.MoveAsync(new VfsPath("/readme.md"), new VfsPath("/README.md"), CancellationToken.None);

        await act.Should().ThrowAsync<IOException>().WithMessage("*already exists*");
        http.Api.Should().NotContain(r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task CreateDirectoryAsync_FolderAlreadyThere_IsLeftAsItIs()
    {
        var http = new CloudHttp(r => r.Method == HttpMethod.Post
            ? Kit.Json("""{"error":{"code":"nameAlreadyExists","message":"Name already exists"}}""", HttpStatusCode.Conflict)
            : Kit.Json("""{"id":"f","name":"src","folder":{"childCount":0}}"""));
        using var fs = Kit.OneDrive(http);

        await fs.CreateDirectoryAsync(new VfsPath("/src"), CancellationToken.None);

        var create = http.Api[0];
        create.Uri.AbsolutePath.Should().Be("/v1.0/me/drive/root/children");
        create.Json.GetProperty("@microsoft.graph.conflictBehavior").GetString().Should().Be("fail");
    }

    // ── The shared request pipeline ──────────────────────────────────────────

    [Fact]
    public async Task Request_Unauthorized_RefreshesTheTokenOnceAndSendsAgain()
    {
        var calls = 0;
        var http = new CloudHttp(_ => Interlocked.Increment(ref calls) == 1 ? Kit.Status(HttpStatusCode.Unauthorized) : FileItem("a.txt"));
        using var fs = Kit.OneDrive(http);

        var entry = await fs.GetEntryAsync(new VfsPath("/a.txt"));

        entry.Should().NotBeNull();
        http.TokenRequests.Should().HaveCount(2);
        http.Api.Select(r => r.Bearer).Should().Equal("access-1", "access-2");
    }

    [Fact]
    public async Task Request_UnauthorizedEvenAfterRefresh_RequiresANewSignIn()
    {
        using var fs = Kit.OneDrive(new CloudHttp(_ => Kit.Status(HttpStatusCode.Unauthorized)));

        var act = () => fs.GetEntryAsync(new VfsPath("/a.txt"));

        await act.Should().ThrowAsync<SignInRequiredException>();
        fs.NeedsSignIn.Should().BeTrue("the grant itself is gone, and only the browser can renew it");
    }

    [Fact]
    public async Task Request_TooManyRequests_WaitsAsToldAndSendsAgain()
    {
        var calls = 0;
        var http = new CloudHttp(_ =>
        {
            if (Interlocked.Increment(ref calls) > 1)
            {
                return FileItem("a.txt");
            }

            var throttled = Kit.Json("""{"error":{"code":"activityLimitReached","message":"Slow down"}}""", HttpStatusCode.TooManyRequests);
            throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return throttled;
        });
        using var fs = Kit.OneDrive(http);

        var entry = await fs.GetEntryAsync(new VfsPath("/a.txt"));

        entry.Should().NotBeNull();
        http.Api.Should().HaveCount(2);
    }

    [Fact]
    public async Task Request_ThrottledForTooLong_SaysWhenToTryAgain()
    {
        var http = new CloudHttp(_ =>
        {
            var throttled = Kit.Json("""{"error":{"code":"activityLimitReached","message":"Slow down"}}""", HttpStatusCode.TooManyRequests);
            throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(1));
            return throttled;
        });
        using var fs = Kit.OneDrive(http);

        var act = () => fs.GetEntryAsync(new VfsPath("/a.txt"));

        await act.Should().ThrowAsync<CloudDriveException>().WithMessage("OneDrive is limiting requests right now. Try again in 3600 seconds.");
        http.Api.Should().ContainSingle("a wait longer than the limit is reported, not sat out");
    }

    [Fact]
    public async Task Request_ServerSilent_TimesOutAfterTheMetadataTimeout()
    {
        var http = new CloudHttp(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Kit.Status(HttpStatusCode.OK);
        });
        using var fs = new OneDriveFileSystem(Kit.Connection(NetworkProtocol.OneDrive), new HttpClient(http))
        {
            MetadataTimeout = TimeSpan.FromMilliseconds(200),
        };

        var act = () => fs.ListAsync(VfsPath.Root);

        await act.Should().ThrowAsync<TimeoutException>().WithMessage("OneDrive did not answer within*");
    }

    [Fact]
    public void DisplayName_BeforeAnySignIn_IsTheUserNameOrTheProvider()
    {
        var known = Kit.Connection(NetworkProtocol.OneDrive);
        known.Username = "me@example.com";

        using var named = Kit.OneDrive(new CloudHttp(Kit.Unexpected), known);
        using var anonymous = Kit.OneDrive(new CloudHttp(Kit.Unexpected));

        named.DisplayName.Should().Be("me@example.com");
        anonymous.DisplayName.Should().Be("OneDrive");
        anonymous.Scheme.Should().Be("onedrive");
    }
}
