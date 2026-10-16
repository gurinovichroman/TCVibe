using System.Net;
using System.Text;
using FluentAssertions;
using TCV.Core.Domain;
using TCV.Core.Interfaces;
using TCV.Network;
using TCV.Network.Cloud;

namespace TCV.Network.Tests;

/// <summary>What <see cref="AzureBlobFileSystem"/> puts on the wire and how it reacts to the answers.</summary>
public sealed class AzureBlobFileSystemTests
{
    private const string Date = "Tue, 13 Oct 2026 08:00:00 GMT";
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);

    private static AzureBlobFileSystem Create(
        FakeBlobService fake, string? container = "c", string password = AzureSharedKeyTests.Key,
        string account = "myaccount", string host = "", int port = 443, bool useHttp = false, TimeSpan? timeout = null)
    {
        var connection = new ConnectionInfo
        {
            Protocol = NetworkProtocol.AzureBlob,
            Username = account,
            Password = password,
            Host = host,
            Port = port,
            UseHttp = useHttp,
            Bucket = container,
        };
        return new AzureBlobFileSystem(connection, new HttpClient(fake))
        {
            MetadataTimeout = timeout ?? TimeSpan.FromSeconds(30),
            CopyPollInterval = TimeSpan.FromMilliseconds(1),
            Clock = () => Now,
        };
    }

    private static async Task WriteAsync(IVirtualFileSystem fs, string path, string text, bool overwrite = true)
    {
        await using var output = await fs.OpenWriteAsync(new VfsPath(path), overwrite);
        await output.WriteAsync(Encoding.UTF8.GetBytes(text));
    }

    private static async Task<string> ReadAllAsync(Stream stream)
    {
        await using (stream)
        {
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync();
        }
    }

    // ── Listing ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_Folder_ListsOneLevelWithADelimitedPrefixQuery()
    {
        var fake = new FakeBlobService().With("c", "docs/a.txt", "abc").With("c", "docs/sub/b.txt").With("c", "top.txt");
        using var fs = Create(fake);

        var entries = await fs.ListAsync(new VfsPath("/docs"));

        entries.Select(e => (e.Path.Value, e.IsDirectory, e.Size)).Should().BeEquivalentTo(new[]
        {
            ("/docs/a.txt", false, 3L), ("/docs/sub", true, 0L),
        });
        var call = fake.Calls.Single();
        call.Path.Should().Be("/c");
        call.Q("prefix").Should().Be("docs/");
        call.Q("delimiter").Should().Be("/");
        call.Q("include").Should().Be("metadata");
        call.Q("maxresults").Should().Be("5000");
    }

    [Fact]
    public async Task ListAsync_ManyPages_FollowsNextMarkerToTheEnd()
    {
        var fake = new FakeBlobService { PageLimit = 2 };
        for (var i = 0; i < 5; i++)
        {
            fake.With("c", $"f{i}.txt");
        }

        using var fs = Create(fake);

        var entries = await fs.ListAsync(VfsPath.Root);

        entries.Select(e => e.Name).Should().Equal("f0.txt", "f1.txt", "f2.txt", "f3.txt", "f4.txt");
        fake.Calls.Select(c => c.Q("marker")).Should().Equal(null, "f2.txt", "f4.txt");
    }

    [Fact]
    public async Task ListAsync_HierarchicalAccount_ShowsADirectoryOnceThoughItIsBlobAndPrefix()
    {
        var fake = new FakeBlobService { Hierarchical = true }.With("c", "d/x.txt").WithDirectory("c", "empty");
        using var fs = Create(fake);

        var entries = await fs.ListAsync(VfsPath.Root);

        entries.Select(e => (e.Name, e.IsDirectory)).Should().BeEquivalentTo(new[] { ("d", true), ("empty", true) });
    }

    [Fact]
    public async Task ListAsync_AccountRootWithoutContainer_ListsContainersAsFolders()
    {
        var fake = new FakeBlobService().With("logs", "a").With("photos", "b");
        using var fs = Create(fake, container: null);

        var entries = await fs.ListAsync(VfsPath.Root);

        entries.Select(e => (e.Path.Value, e.IsDirectory)).Should().Equal(("/logs", true), ("/photos", true));
        fake.Calls.Single().Path.Should().Be("/");
        fake.Calls.Single().Q("comp").Should().Be("list");
        (await fs.ListAsync(new VfsPath("/photos"))).Single().Path.Value.Should().Be("/photos/b");
    }

    [Fact]
    public async Task ListAsync_MissingContainer_SaysTheContainerDoesNotExist()
    {
        using var fs = Create(new FakeBlobService(), container: "nope");

        var act = () => fs.ListAsync(VfsPath.Root);

        (await act.Should().ThrowAsync<AzureStorageException>()).Which.ErrorCode.Should().Be("ContainerNotFound");
    }

    [Fact]
    public async Task ListAsync_ServerThatNeverAnswers_TimesOutWithAClearMessage()
    {
        var fake = new FakeBlobService { Intercept = _ => throw new InvalidOperationException("unused") };
        var connection = new ConnectionInfo { Protocol = NetworkProtocol.AzureBlob, Username = "a", Password = AzureSharedKeyTests.Key, Bucket = "c" };
        using var fs = new AzureBlobFileSystem(connection, new HttpClient(new HangingHandler())) { MetadataTimeout = TimeSpan.FromMilliseconds(150) };
        _ = fake;

        var act = () => fs.ListAsync(VfsPath.Root);

        await act.Should().ThrowAsync<TimeoutException>().WithMessage("*did not answer*");
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        }
    }

    // ── Entries ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetEntryAsync_NotFound_IsNullButARefusedLoginIsNotAnAnswer()
    {
        using var missing = Create(new FakeBlobService().With("c", "other"));
        var refused = new FakeBlobService
        {
            Intercept = _ => FakeBlobService.Error(HttpStatusCode.Forbidden, "AuthenticationFailed", head: true),
        };
        using var denied = Create(refused);

        (await missing.GetEntryAsync(new VfsPath("/x"))).Should().BeNull();
        var act = () => denied.GetEntryAsync(new VfsPath("/x"));
        (await act.Should().ThrowAsync<AzureStorageException>())
            .WithMessage("*account key*")
            .Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetEntryAsync_VirtualFolder_IsFoundThroughTheListing()
    {
        var fake = new FakeBlobService().With("c", "dir/x.txt");
        using var fs = Create(fake);

        var entry = await fs.GetEntryAsync(new VfsPath("/dir"));

        entry!.IsDirectory.Should().BeTrue();
        entry.Name.Should().Be("dir");
        fake.Calls.Select(c => c.Method).Should().Equal("HEAD", "GET");
        fake.Calls[1].Q("prefix").Should().Be("dir/");
        fake.Calls[1].Q("maxresults").Should().Be("1");
    }

    [Fact]
    public async Task GetEntryAsync_HierarchicalDirectoryBlob_IsAFolderFromItsProperties()
    {
        var fake = new FakeBlobService { Hierarchical = true }.WithDirectory("c", "d");
        using var fs = Create(fake);

        var entry = await fs.GetEntryAsync(new VfsPath("/d"));

        entry!.IsDirectory.Should().BeTrue();
        fake.Calls.Should().ContainSingle(c => c.Method == "HEAD");
    }

    [Fact]
    public async Task GetEntryAsync_File_HasSizeAndLocalTime()
    {
        using var fs = Create(new FakeBlobService().With("c", "a.txt", "hello"));

        var entry = await fs.GetEntryAsync(new VfsPath("/a.txt"));

        entry!.IsDirectory.Should().BeFalse();
        entry.Size.Should().Be(5);
        entry.LastModified.Should().Be(FakeBlobService.Modified.LocalDateTime);
    }

    // ── Credentials and addressing ───────────────────────────────────────────

    [Fact]
    public async Task SharedKey_Request_IsStampedAndSignedOverWhatIsSent()
    {
        var fake = new FakeBlobService().With("c", "docs/a.txt");
        using var fs = Create(fake);

        await fs.ListAsync(new VfsPath("/docs"));

        var call = fake.Calls.Single();
        call.Header("x-ms-date").Should().Be(Date);
        call.Header("x-ms-version").Should().Be("2023-11-03");
        var expected = "GET" + new string('\n', 12) + "x-ms-date:" + Date + "\nx-ms-version:2023-11-03\n" +
                       "/myaccount/c\ncomp:list\ndelimiter:/\ninclude:metadata\nmaxresults:5000\nprefix:docs/\nrestype:container";
        call.Header("Authorization").Should().Be(
            "SharedKey myaccount:" + AzureSharedKey.ComputeSignature(expected, Convert.FromBase64String(AzureSharedKeyTests.Key)));
    }

    [Fact]
    public async Task SasToken_EveryRequest_CarriesTheTokenAndNoAuthorization()
    {
        var fake = new FakeBlobService().With("c", "a.txt", "hi");
        using var fs = Create(fake, password: "?sv=2023-11-03&sp=rl&sig=abc%2Bdef");

        await fs.ListAsync(VfsPath.Root);
        await ReadAllAsync(await fs.OpenReadAsync(new VfsPath("/a.txt")));

        fake.Calls.Should().HaveCount(2).And.OnlyContain(c => c.Q("sig") == "abc+def" && c.Q("sp") == "rl");
        fake.Calls.Should().OnlyContain(c => c.Header("Authorization") == null);
        fs.UsesSasToken.Should().BeTrue();
        fs.CanCreateReadLink.Should().BeFalse();
    }

    [Fact]
    public async Task Emulator_PathStyle_PutsTheAccountBeforeTheContainer()
    {
        var fake = new FakeBlobService(accountInPath: "devstoreaccount1").With("c", "a.txt", "hello");
        using var fs = Create(fake, account: "devstoreaccount1", host: "127.0.0.1", port: 10000, useHttp: true);

        (await ReadAllAsync(await fs.OpenReadAsync(new VfsPath("/a.txt")))).Should().Be("hello");

        fake.Calls.Single().Path.Should().Be("/devstoreaccount1/c/a.txt");
    }

    [Fact]
    public async Task OpenWriteAsync_AwkwardName_ArrivesAsItselfAndListsBack()
    {
        var fake = new FakeBlobService().WithContainer("c");
        using var fs = Create(fake);

        await WriteAsync(fs, "/my dir/résumé+1 100%.txt", "x");

        fake.Calls.Single().Path.Should().Be("/c/my%20dir/r%C3%A9sum%C3%A9%2B1%20100%25.txt");
        fake.Container("c").Keys.Should().Equal("my dir/résumé+1 100%.txt");
        (await fs.ListAsync(new VfsPath("/my dir"))).Single().Name.Should().Be("résumé+1 100%.txt");
    }

    [Fact]
    public void Constructor_KeyThatIsNotBase64_SaysWhatToPaste()
    {
        var act = () => Create(new FakeBlobService(), password: "not a key!");

        act.Should().Throw<ArgumentException>().WithMessage("*account key*");
    }

    [Fact]
    public void DisplayName_WithAndWithoutContainer_IsAccountAndContainer()
    {
        Create(new FakeBlobService()).DisplayName.Should().Be("myaccount/c");
        Create(new FakeBlobService(), container: null).DisplayName.Should().Be("myaccount");
    }

    // ── Uploads ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task OpenWriteAsync_SmallFile_IsOnePutBlob()
    {
        var fake = new FakeBlobService().WithContainer("c");
        using var fs = Create(fake);

        await WriteAsync(fs, "/a.txt", "hello");

        var put = fake.Calls.Should().ContainSingle().Subject;
        put.Method.Should().Be("PUT");
        put.Header("x-ms-blob-type").Should().Be("BlockBlob");
        put.Header("If-None-Match").Should().BeNull("an overwrite is allowed to replace");
        fake.Text("c", "a.txt").Should().Be("hello");
    }

    [Fact]
    public async Task OpenWriteAsync_LargeFile_GoesInEqualLengthBlocksThenOneList()
    {
        var fake = new FakeBlobService().WithContainer("c");
        using var fs = new AzureBlobFileSystem(
            new ConnectionInfo { Protocol = NetworkProtocol.AzureBlob, Username = "myaccount", Password = AzureSharedKeyTests.Key, Bucket = "c" },
            new HttpClient(fake))
        {
            SingleUploadLimit = 10,
            BlockSize = 4,
        };
        var reported = new List<long>();

        var output = await fs.OpenWriteAsync(new VfsPath("/big.bin"), overwrite: true);
        await output.WriteAsync(Encoding.UTF8.GetBytes("hello world"));
        await ((IDeferredUploadStream)output).CommitAsync(new SyncProgress(reported.Add), CancellationToken.None);
        await output.DisposeAsync();

        var blocks = fake.Calls.Where(c => c.Q("comp") == "block").ToList();
        blocks.Select(b => b.Body.Length).Should().Equal(4, 4, 3);
        blocks.Select(b => b.Q("blockid")!.Length).Distinct().Should().ContainSingle("all block ids of a blob have one length");
        var list = fake.Calls.Single(c => c.Q("comp") == "blocklist");
        Encoding.UTF8.GetString(list.Body).Should().Contain(string.Concat(blocks.Select(b => $"<Latest>{b.Q("blockid")}</Latest>")));
        fake.Text("c", "big.bin").Should().Be("hello world");
        reported.Should().BeInAscendingOrder().And.EndWith(11);
    }

    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    [Fact]
    public async Task OpenWriteAsync_WithoutOverwriteOnAnExistingBlob_RefusesBeforeCollectingAnything()
    {
        var fake = new FakeBlobService().With("c", "a.txt", "old");
        using var fs = Create(fake);

        var act = () => fs.OpenWriteAsync(new VfsPath("/a.txt"), overwrite: false);

        await act.Should().ThrowAsync<IOException>().WithMessage("*already exists*");
        fake.Calls.Should().ContainSingle(c => c.Method == "HEAD");
        fake.Text("c", "a.txt").Should().Be("old");
    }

    [Fact]
    public async Task OpenWriteAsync_WithoutOverwrite_BlobAppearingMeanwhileIsRefusedByTheService()
    {
        var fake = new FakeBlobService().WithContainer("c");
        fake.Intercept = request => request.Method == HttpMethod.Put
            ? FakeBlobService.Error(HttpStatusCode.Conflict, "BlobAlreadyExists")
            : null;
        using var fs = Create(fake);

        var act = () => WriteAsync(fs, "/a.txt", "new", overwrite: false);

        await act.Should().ThrowAsync<IOException>().WithMessage("*already exists*");
        fake.Calls.Single(c => c.Method == "PUT").Header("If-None-Match").Should().Be("*");
    }

    [Fact]
    public async Task OpenWriteAsync_Aborted_NeverSendsThePartialBody()
    {
        var fake = new FakeBlobService();
        using var fs = Create(fake);

        var output = (CloudUploadStream)await fs.OpenWriteAsync(new VfsPath("/half.bin"), overwrite: true);
        await output.WriteAsync(new byte[] { 1, 2, 3 });
        var temporary = output.BufferPath;
        output.Abort();
        await output.DisposeAsync();

        fake.Calls.Should().BeEmpty();
        File.Exists(temporary).Should().BeFalse("the collected body goes with the stream");
    }

    // ── Ranged reads ─────────────────────────────────────────────────────────

    [Fact]
    public async Task OpenReadFromAsync_SameETag_ResumesAtTheOffsetWithIfMatch()
    {
        var fake = new FakeBlobService().With("c", "big.bin", new string('a', 100) + new string('b', 100));
        using var fs = Create(fake);
        var (first, _, _, etag) = await fs.OpenReadFromAsync(new VfsPath("/big.bin"), 0, null, CancellationToken.None);
        await first.DisposeAsync();

        var (stream, offset, total, validator) = await fs.OpenReadFromAsync(new VfsPath("/big.bin"), 100, etag, CancellationToken.None);

        (await ReadAllAsync(stream)).Should().Be(new string('b', 100));
        offset.Should().Be(100);
        total.Should().Be(200);
        validator.Should().Be(etag);
        var resumed = fake.Calls.Last();
        resumed.Header("Range").Should().Be("bytes=100-");
        resumed.Header("If-Match").Should().Be(etag);
    }

    [Fact]
    public async Task OpenReadFromAsync_BlobChanged_StartsOverFromTheFirstByte()
    {
        var fake = new FakeBlobService().With("c", "big.bin", new string('n', 200));
        using var fs = Create(fake);

        var (stream, offset, total, _) = await fs.OpenReadFromAsync(new VfsPath("/big.bin"), 100, "\"0xOLD\"", CancellationToken.None);

        (await ReadAllAsync(stream)).Should().HaveLength(200);
        offset.Should().Be(0, "the part already downloaded belongs to another version of the blob");
        total.Should().Be(200);
        fake.Calls.Should().HaveCount(2);
        fake.Calls[1].Header("Range").Should().BeNull();
        fake.Calls[1].Header("If-Match").Should().BeNull();
    }

    // ── Deleting ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_FolderRecursive_DeletesEveryBlobUnderItAndNothingElse()
    {
        var fake = new FakeBlobService().With("c", "dir/", "").With("c", "dir/a").With("c", "dir/sub/b").With("c", "dirt");
        using var fs = Create(fake);
        var changes = new List<VfsChangedEventArgs>();
        fs.Changed += (_, e) => changes.Add(e);

        await fs.DeleteAsync(new VfsPath("/dir"), recursive: true, CancellationToken.None);

        fake.Container("c").Keys.Should().Equal("dirt");
        fake.Calls.Where(c => c.Method == "DELETE" && c.Path != "/c/dir/")
            .Should().OnlyContain(c => c.Header("x-ms-delete-snapshots") == "include");
        changes.Should().ContainSingle(e => e.Path.Value == "/dir" && e.ChangeType == WatcherChangeTypes.Deleted);
    }

    [Fact]
    public async Task DeleteAsync_HierarchicalFolder_DeletesDirectoriesDeepestFirst()
    {
        var fake = new FakeBlobService { Hierarchical = true }.With("c", "d/e/f.txt").WithDirectory("c", "d/empty").With("c", "keep");
        using var fs = Create(fake);

        await fs.DeleteAsync(new VfsPath("/d"), recursive: true, CancellationToken.None);

        fake.Container("c").Keys.Should().Equal("keep");
    }

    [Fact]
    public async Task DeleteAsync_NonRecursiveFolderWithContent_RefusesAndDeletesNothing()
    {
        var fake = new FakeBlobService().With("c", "dir/a");
        using var fs = Create(fake);

        var act = () => fs.DeleteAsync(new VfsPath("/dir"), recursive: false, CancellationToken.None);

        await act.Should().ThrowAsync<IOException>().WithMessage("*not empty*");
        fake.Calls.Should().NotContain(c => c.Method == "DELETE");
    }

    [Fact]
    public async Task DeleteAsync_NonRecursiveEmptyFolder_RemovesItsMarker()
    {
        var fake = new FakeBlobService().With("c", "dir/", "");
        using var fs = Create(fake);

        await fs.DeleteAsync(new VfsPath("/dir"), recursive: false, CancellationToken.None);

        fake.Container("c").Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteAsync_FileBesideAFolderOfTheSameName_DeletesOnlyTheFile()
    {
        var fake = new FakeBlobService().With("c", "x").With("c", "x/y");
        using var fs = Create(fake);

        await fs.DeleteAsync(new VfsPath("/x"), recursive: true, CancellationToken.None);

        fake.Container("c").Keys.Should().Equal("x/y");
    }

    [Fact]
    public async Task DeleteAsync_Missing_SaysItDoesNotExist()
    {
        using var fs = Create(new FakeBlobService().With("c", "other"));

        var act = () => fs.DeleteAsync(new VfsPath("/ghost"), recursive: true, CancellationToken.None);

        await act.Should().ThrowAsync<AzureStorageException>().WithMessage("*\"ghost\" does not exist*");
    }

    // ── Copy, move, rename ───────────────────────────────────────────────────

    [Fact]
    public async Task CopyAsync_PendingServerCopy_IsFollowedUntilDone()
    {
        var fake = new FakeBlobService { CopyLooks = 2 }.With("c", "a.bin", "0123456789");
        using var fs = Create(fake);
        var reports = new List<FileProgress>();

        await fs.CopyAsync(new VfsPath("/a.bin"), new VfsPath("/copies/b.bin"), new SyncFileProgress(reports.Add), CancellationToken.None);

        fake.Text("c", "copies/b.bin").Should().Be("0123456789");
        fake.Calls.Single(c => c.Header("x-ms-copy-source") is not null)
            .Header("x-ms-copy-source").Should().Be("https://myaccount.blob.core.windows.net/c/a.bin");
        fake.Calls.Count(c => c.Method == "HEAD" && c.Path == "/c/copies/b.bin").Should().Be(2);
        reports.Should().NotBeEmpty();
        reports[^1].BytesCopied.Should().Be(10);
        reports[^1].TotalBytes.Should().Be(10);
    }

    private sealed class SyncFileProgress(Action<FileProgress> report) : IProgress<FileProgress>
    {
        public void Report(FileProgress value) => report(value);
    }

    [Fact]
    public async Task CopyAsync_WithSasToken_TheSourceUrlCarriesTheTokenToo()
    {
        var fake = new FakeBlobService().With("c", "a b.txt");
        using var fs = Create(fake, password: "sv=2023-11-03&sp=rwdl&sig=s%2Fg");

        await fs.CopyAsync(new VfsPath("/a b.txt"), new VfsPath("/b.txt"), null, CancellationToken.None);

        fake.Calls.Single(c => c.Header("x-ms-copy-source") is not null).Header("x-ms-copy-source")
            .Should().Be("https://myaccount.blob.core.windows.net/c/a%20b.txt?sv=2023-11-03&sp=rwdl&sig=s%2Fg");
    }

    [Fact]
    public async Task CopyAsync_CancelledWhileTheServerCopies_AbortsTheCopy()
    {
        var fake = new FakeBlobService { CopyLooks = 1000 }.With("c", "a.bin", "data");
        using var cts = new CancellationTokenSource();
        var looks = 0;
        fake.Intercept = request =>
        {
            if (request.Method == HttpMethod.Head && request.RequestUri!.AbsolutePath == "/c/b.bin" && ++looks == 2)
            {
                cts.Cancel();
            }

            return null;
        };
        using var fs = Create(fake);

        var act = () => fs.CopyAsync(new VfsPath("/a.bin"), new VfsPath("/b.bin"), null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        var copyId = fake.Container("c")["b.bin"].CopyId;
        var abort = fake.Calls.Single(c => c.Q("comp") == "copy");
        abort.Q("copyid").Should().Be(copyId);
        abort.Header("x-ms-copy-action").Should().Be("abort");
        fake.Container("c")["b.bin"].CopyStatus.Should().Be("aborted");
    }

    [Fact]
    public async Task RenameAsync_File_CopiesWithoutReplacingThenDeletesTheOriginal()
    {
        var fake = new FakeBlobService().With("c", "dir/a.txt", "text");
        using var fs = Create(fake);

        await fs.RenameAsync(new VfsPath("/dir/a.txt"), "b.txt", CancellationToken.None);

        fake.Container("c").Keys.Should().Equal("dir/b.txt");
        fake.Text("c", "dir/b.txt").Should().Be("text");
        fake.Calls.Single(c => c.Header("x-ms-copy-source") is not null).Header("If-None-Match").Should().Be("*");
    }

    [Fact]
    public async Task RenameAsync_TargetExists_RefusesWithoutCopying()
    {
        var fake = new FakeBlobService().With("c", "a.txt", "a").With("c", "b.txt", "b");
        using var fs = Create(fake);

        var act = () => fs.RenameAsync(new VfsPath("/a.txt"), "b.txt", CancellationToken.None);

        await act.Should().ThrowAsync<IOException>().WithMessage("*already exists*");
        fake.Calls.Should().NotContain(c => c.Method == "PUT" || c.Method == "DELETE");
        fake.Text("c", "b.txt").Should().Be("b");
    }

    [Fact]
    public async Task RenameAsync_OnlyTheCaseChanges_IsARealRename()
    {
        var fake = new FakeBlobService().With("c", "readme", "r");
        using var fs = Create(fake);

        await fs.RenameAsync(new VfsPath("/readme"), "README", CancellationToken.None);

        fake.Container("c").Keys.Should().Equal(new[] { "README" }, "blob names are case-sensitive");
    }

    [Fact]
    public async Task MoveAsync_Folder_CopiesEveryBlobThenDeletesTheSource()
    {
        var fake = new FakeBlobService().With("c", "src/", "").With("c", "src/a", "1").With("c", "src/sub/b", "2").With("c", "srcx", "3");
        using var fs = Create(fake);

        await fs.MoveAsync(new VfsPath("/src"), new VfsPath("/dst/moved"), CancellationToken.None);

        fake.Container("c").Keys.Should().BeEquivalentTo("dst/moved/", "dst/moved/a", "dst/moved/sub/b", "srcx");
        fake.Text("c", "dst/moved/sub/b").Should().Be("2");
    }

    [Fact]
    public async Task MoveAsync_HierarchicalFolderWithAnEmptyDirectory_KeepsTheEmptyDirectory()
    {
        var fake = new FakeBlobService { Hierarchical = true }.With("c", "src/a").WithDirectory("c", "src/empty");
        using var fs = Create(fake);

        await fs.MoveAsync(new VfsPath("/src"), new VfsPath("/dst"), CancellationToken.None);

        fake.Container("c").Keys.Should().BeEquivalentTo("dst", "dst/a", "dst/empty");
        fake.Container("c")["dst/empty"].IsDirectory.Should().BeTrue();
    }

    [Fact]
    public async Task MoveAsync_FolderIntoItself_Refuses()
    {
        var fake = new FakeBlobService().With("c", "a/x");
        using var fs = Create(fake);

        var act = () => fs.MoveAsync(new VfsPath("/a"), new VfsPath("/a/b"), CancellationToken.None);

        await act.Should().ThrowAsync<IOException>().WithMessage("*into itself*");
        fake.Container("c").Keys.Should().Equal("a/x");
    }

    [Fact]
    public async Task RenameAsync_Container_IsNotSupported()
    {
        using var fs = Create(new FakeBlobService().With("logs", "a"), container: null);

        var act = () => fs.RenameAsync(new VfsPath("/logs"), "old-logs", CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>().WithMessage("*cannot rename or move a container*");
    }

    // ── Folders ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateDirectoryAsync_FlatAccount_PutsAZeroLengthSlashMarker()
    {
        var fake = new FakeBlobService();
        fake.Container("c");
        using var fs = Create(fake);

        await fs.CreateDirectoryAsync(new VfsPath("/new"), CancellationToken.None);
        await fs.CreateDirectoryAsync(new VfsPath("/new"), CancellationToken.None);

        fake.Container("c").Keys.Should().Equal("new/");
        fake.Container("c")["new/"].Data.Should().BeEmpty();
        fake.Calls.Count(c => c.Q("restype") == "account").Should().Be(1, "the account is asked once");
        (await fs.ListAsync(VfsPath.Root)).Single().Should().Match<IFileEntry>(e => e.Name == "new" && e.IsDirectory);
        (await fs.ListAsync(new VfsPath("/new"))).Should().BeEmpty("the marker is the folder, not a file in it");
    }

    [Fact]
    public async Task CreateDirectoryAsync_HierarchicalAccount_PutsAnHdiFolderBlob()
    {
        var fake = new FakeBlobService { Hierarchical = true };
        fake.Container("c");
        using var fs = Create(fake);

        await fs.CreateDirectoryAsync(new VfsPath("/new"), CancellationToken.None);
        await fs.CreateDirectoryAsync(new VfsPath("/new"), CancellationToken.None);

        fake.Container("c").Keys.Should().Equal("new");
        fake.Container("c")["new"].IsDirectory.Should().BeTrue();
    }

    [Fact]
    public async Task CreateDirectoryAsync_HierarchicalNameTakenByAFile_Refuses()
    {
        var fake = new FakeBlobService { Hierarchical = true }.With("c", "taken");
        using var fs = Create(fake);

        var act = () => fs.CreateDirectoryAsync(new VfsPath("/taken"), CancellationToken.None);

        await act.Should().ThrowAsync<IOException>().WithMessage("*file named \"taken\" already exists*");
    }

    [Fact]
    public async Task CreateDirectoryAsync_AccountRoot_CreatesAContainerAndChecksItsName()
    {
        var fake = new FakeBlobService();
        using var fs = Create(fake, container: null);

        await fs.CreateDirectoryAsync(new VfsPath("/new-box"), CancellationToken.None);
        await fs.CreateDirectoryAsync(new VfsPath("/new-box"), CancellationToken.None);
        var act = () => fs.CreateDirectoryAsync(new VfsPath("/Bad_Name"), CancellationToken.None);

        fake.Containers.Keys.Should().Equal("new-box");
        await act.Should().ThrowAsync<IOException>().WithMessage("*cannot be a container*");
    }

    // ── Links ────────────────────────────────────────────────────────────────

    [Fact]
    public void CreateReadLink_AccountKey_IsAReadOnlyServiceSasForThatBlob()
    {
        var connection = new ConnectionInfo
        {
            Protocol = NetworkProtocol.AzureBlob, Username = "myaccount", Password = AzureSharedKeyTests.Key, Bucket = "photos",
        };
        using var fs = new AzureBlobFileSystem(connection, new HttpClient(new FakeBlobService())) { Clock = () => Now };

        var link = fs.CreateReadLink(new VfsPath("/2026/beach day.jpg"), TimeSpan.FromDays(1));

        link.AbsoluteUri.Should().Be(
            "https://myaccount.blob.core.windows.net/photos/2026/beach%20day.jpg" +
            "?sv=2023-11-03&spr=https&se=2026-10-14T08%3A00%3A00Z&sr=b&sp=r&sig=" +
            Uri.EscapeDataString("FcB02nIFCK6jkqXwwnvhwFw+tUlKehqYLCqZKl+hlp4="));
    }

    [Fact]
    public void CreateReadLink_Emulator_SignsTheDecodedNameAndAllowsHttp()
    {
        using var fs = Create(new FakeBlobService(), container: null, account: "devstoreaccount1",
            host: "127.0.0.1", port: 10000, useHttp: true);

        var link = fs.CreateReadLink(new VfsPath("/photos/résumé+1 100%.txt"), TimeSpan.FromDays(1));

        link.AbsoluteUri.Should().Be(
            "http://127.0.0.1:10000/devstoreaccount1/photos/r%C3%A9sum%C3%A9%2B1%20100%25.txt" +
            "?sv=2023-11-03&spr=https%2Chttp&se=2026-10-14T08%3A00%3A00Z&sr=b&sp=r&sig=" +
            Uri.EscapeDataString("IGrKom0NBv9WPATRNxeJDBVt4FtUZqGHKQSfIBVCdlY="));
    }

    [Fact]
    public void CreateReadLink_SasConnection_IsNotSupported()
    {
        using var fs = Create(new FakeBlobService(), password: "sv=2023-11-03&sig=x");

        var act = () => fs.CreateReadLink(new VfsPath("/a.txt"), TimeSpan.FromHours(1));

        act.Should().Throw<NotSupportedException>().WithMessage("*account key*");
    }
}
