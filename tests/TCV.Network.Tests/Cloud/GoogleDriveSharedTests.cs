using System.Net;
using FluentAssertions;
using TCV.Core.Domain;
using TCV.Core.Interfaces;
using TCV.Network.Cloud;

namespace TCV.Network.Tests.Cloud;

/// <summary>
/// The shared drives and "Shared with me" as virtual folders of the root. The answers are written from
/// the Drive API v3 documentation; no live account has seen this.
/// </summary>
public sealed class GoogleDriveSharedTests
{
    private const string Files = "https://www.googleapis.com/drive/v3/files";
    private const string Drives = "https://www.googleapis.com/drive/v3/drives";
    private const string Folder = GoogleDriveFileSystem.FolderMime;
    private const string SharedDrives = GoogleDriveFileSystem.SharedDrivesName;
    private const string SharedWithMe = GoogleDriveFileSystem.SharedWithMeName;

    private static string Item(string id, string name, string mime = "text/plain", long? size = 10) =>
        $$$"""{"id":"{{{id}}}","name":"{{{name}}}","mimeType":"{{{mime}}}"{{{(size is null ? "" : $",\"size\":\"{size}\"")}}},"modifiedTime":"2026-09-02T10:00:00.000Z","createdTime":"2026-01-01T00:00:00Z","headRevisionId":"rev1"}""";

    private static HttpResponseMessage Found(params string[] items) => Kit.Json($$"""{"files":[{{string.Join(',', items)}}]}""");

    private static HttpResponseMessage OneDrive(string id = "drv1", string name = "Engineering") =>
        Kit.Json($$"""{"drives":[{"id":"{{id}}","name":"{{name}}","createdTime":"2025-01-01T00:00:00Z"}]}""");

    private static bool IsQuery(Recorded r) => r.Method == HttpMethod.Get && r.Uri.GetLeftPart(UriPartial.Path) == Files;

    private static bool IsDriveList(Recorded r) => r.Method == HttpMethod.Get && r.Uri.GetLeftPart(UriPartial.Path) == Drives;

    /// <summary>A Drive with one shared drive (Engineering, holding Specs/plan.txt) and otherwise empty.</summary>
    private static CloudHttp SharedDriveAccount() => new(r =>
    {
        if (IsDriveList(r))
        {
            return OneDrive();
        }

        if (!IsQuery(r))
        {
            return Kit.Json("""{"id":"new"}""");
        }

        var q = r.Query["q"]!;
        return q switch
        {
            _ when q.StartsWith("'drv1' in parents", StringComparison.Ordinal) && q.Contains("name = 'Specs'") => Found(Item("specs", "Specs", Folder, null)),
            _ when q.StartsWith("'drv1' in parents", StringComparison.Ordinal) && q.Contains("name =") => Found(),
            _ when q.StartsWith("'drv1' in parents", StringComparison.Ordinal) => Found(Item("specs", "Specs", Folder, null), Item("top", "top.txt")),
            _ when q.StartsWith("'specs' in parents", StringComparison.Ordinal) && q.Contains("name = 'plan.txt'") => Found(Item("plan", "plan.txt", size: 7)),
            _ when q.StartsWith("'specs' in parents", StringComparison.Ordinal) && q.Contains("name =") => Found(),
            _ when q.StartsWith("'specs' in parents", StringComparison.Ordinal) => Found(Item("plan", "plan.txt", size: 7)),
            _ => Found(),
        };
    });

    /// <summary>What others shared with this account: a file and a folder holding a file.</summary>
    private static CloudHttp SharedWithMeAccount() => new(r =>
    {
        if (!IsQuery(r))
        {
            return r.Uri.AbsolutePath.StartsWith("/drive/v3/files/", StringComparison.Ordinal) || r.Method != HttpMethod.Get
                ? Kit.Json("""{"id":"x"}""")
                : Kit.Unexpected(r);
        }

        var q = r.Query["q"]!;
        return q switch
        {
            _ when q.StartsWith("sharedWithMe = true", StringComparison.Ordinal) && q.Contains("name = 'report.txt'") => Found(Item("rep", "report.txt", size: 5)),
            _ when q.StartsWith("sharedWithMe = true", StringComparison.Ordinal) && q.Contains("name = 'Team'") => Found(Item("team", "Team", Folder, null)),
            _ when q.StartsWith("sharedWithMe = true", StringComparison.Ordinal) && q.Contains("name =") => Found(),
            _ when q.StartsWith("sharedWithMe = true", StringComparison.Ordinal) => Found(Item("rep", "report.txt", size: 5), Item("team", "Team", Folder, null)),
            _ when q.StartsWith("'team' in parents", StringComparison.Ordinal) && q.Contains("name = 'inner.txt'") => Found(Item("inner", "inner.txt", size: 3)),
            _ when q.StartsWith("'team' in parents", StringComparison.Ordinal) && q.Contains("name =") => Found(),
            _ when q.StartsWith("'team' in parents", StringComparison.Ordinal) => Found(Item("inner", "inner.txt", size: 3)),
            _ when q.Contains("name = 'dst'") => Found(Item("dst", "dst", Folder, null)),
            _ => Found(),
        };
    });

    // ── The root ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_Root_ShowsBothVirtualFoldersBesideMyDriveWithoutAskingDriveAboutThem()
    {
        var http = new CloudHttp(_ => Found(Item("d1", "notes.txt")));
        using var fs = Kit.Google(http);

        var entries = await fs.ListAsync(VfsPath.Root);

        entries.Select(e => (e.Path.Value, e.Name, e.IsDirectory)).Should().BeEquivalentTo(new[]
        {
            ("/notes.txt", "notes.txt", false),
            ("/" + SharedDrives, SharedDrives, true),
            ("/" + SharedWithMe, SharedWithMe, true),
        });
        http.Api.Should().ContainSingle("a root listing costs one request, as it did");
    }

    [Fact]
    public async Task ListAsync_RootOfMyDrive_KeepsItsQueryAndAddsTheAllDrivesFlags()
    {
        var http = new CloudHttp(_ => Found());
        using var fs = Kit.Google(http);

        await fs.ListAsync(VfsPath.Root);

        var query = http.Api[0];
        query.Query["q"].Should().Be("'root' in parents and trashed = false");
        query.Query["supportsAllDrives"].Should().Be("true");
        query.Query["includeItemsFromAllDrives"].Should().Be("true");
        query.Query["corpora"].Should().BeNull("My Drive stays on Drive's default");
    }

    [Fact]
    public async Task GetEntryAsync_VirtualFolder_IsAFolder()
    {
        var http = new CloudHttp(_ => Found());
        using var fs = Kit.Google(http);

        var entry = await fs.GetEntryAsync(new VfsPath("/" + SharedDrives));

        entry.Should().NotBeNull();
        entry!.IsDirectory.Should().BeTrue();
        entry.Path.Value.Should().Be("/" + SharedDrives);
    }

    // ── A real item of the same name ─────────────────────────────────────────

    [Fact]
    public async Task ListAsync_RealFolderHoldsTheName_ShowsItAndTheVirtualOneUnderItsSecondName()
    {
        var http = new CloudHttp(_ => Found(Item("mine", SharedDrives, Folder, null)));
        using var fs = Kit.Google(http);

        var entries = await fs.ListAsync(VfsPath.Root);

        entries.Select(e => e.Name).Should().BeEquivalentTo(
            SharedDrives, SharedDrives + GoogleDriveFileSystem.SecondNameSuffix, SharedWithMe);
    }

    [Fact]
    public async Task ListAsync_RealItemDiffersOnlyInCase_StillHoldsTheName()
    {
        var http = new CloudHttp(_ => Found(Item("mine", "shared WITH me", size: 1)));
        using var fs = Kit.Google(http);

        var entries = await fs.ListAsync(VfsPath.Root);

        entries.Select(e => e.Name).Should().BeEquivalentTo(
            "shared WITH me", SharedWithMe + GoogleDriveFileSystem.SecondNameSuffix, SharedDrives);
    }

    [Fact]
    public async Task ListAsync_BothNamesTaken_ShowsOnlyTheRealItems()
    {
        var second = SharedDrives + GoogleDriveFileSystem.SecondNameSuffix;
        var http = new CloudHttp(_ => Found(Item("a", SharedDrives, Folder, null), Item("b", second, Folder, null)));
        using var fs = Kit.Google(http);

        var entries = await fs.ListAsync(VfsPath.Root);

        entries.Select(e => e.Name).Should().BeEquivalentTo(SharedDrives, second, SharedWithMe);
    }

    [Fact]
    public async Task OpenReadAsync_FileNamedLikeAVirtualFolder_IsThatFile()
    {
        var http = new CloudHttp(r => IsQuery(r)
            ? Found(Item("mine", SharedWithMe, size: 4))
            : r.Uri.AbsolutePath == "/drive/v3/files/mine" ? Kit.Bytes("mine"u8.ToArray()) : Kit.Unexpected(r));
        using var fs = Kit.Google(http);

        var entry = await fs.GetEntryAsync(new VfsPath("/" + SharedWithMe));
        var bytes = await Kit.ReadAllAsync(await fs.OpenReadAsync(new VfsPath("/" + SharedWithMe)));

        entry!.IsDirectory.Should().BeFalse();
        entry.Size.Should().Be(4);
        bytes.Should().Equal("mine"u8.ToArray());
    }

    [Fact]
    public async Task DeleteAsync_FileNamedLikeAVirtualFolder_TrashesThatFile()
    {
        var http = new CloudHttp(r => IsQuery(r) ? Found(Item("mine", SharedDrives, size: 4)) : Kit.Json("""{"id":"mine"}"""));
        using var fs = Kit.Google(http);

        await fs.DeleteAsync(new VfsPath("/" + SharedDrives), recursive: true, CancellationToken.None);

        var trash = http.Api.Single(r => r.Method != HttpMethod.Get);
        trash.Uri.AbsolutePath.Should().Be("/drive/v3/files/mine");
    }

    [Fact]
    public async Task ListAsync_RealFolderNamedLikeAVirtualOne_ListsThatFolder()
    {
        var http = new CloudHttp(r =>
        {
            var q = r.Query["q"]!;
            return q.StartsWith("'root' in parents", StringComparison.Ordinal)
                ? Found(Item("mine", SharedDrives, Folder, null))
                : q.StartsWith("'mine' in parents", StringComparison.Ordinal)
                    ? Found(Item("x", "inside.txt"))
                    : Kit.Unexpected(r);
        });
        using var fs = Kit.Google(http);

        var entries = await fs.ListAsync(new VfsPath("/" + SharedDrives));

        entries.Should().ContainSingle().Which.Path.Value.Should().Be($"/{SharedDrives}/inside.txt");
        http.Api.Should().NotContain(r => IsDriveList(r));
    }

    [Fact]
    public async Task ListAsync_SecondName_IsTheVirtualFolderWhateverTheRootHolds()
    {
        var http = new CloudHttp(r => IsDriveList(r) ? OneDrive() : Found());
        using var fs = Kit.Google(http);

        var entries = await fs.ListAsync(new VfsPath("/" + SharedDrives + GoogleDriveFileSystem.SecondNameSuffix));

        entries.Should().ContainSingle().Which.Name.Should().Be("Engineering");
    }

    [Fact]
    public async Task ListAsync_RootCannotBeAskedAboutTheName_FailsInsteadOfGuessingVirtual()
    {
        var http = new CloudHttp(_ => Kit.Json(
            """{"error":{"code":500,"message":"Backend Error","errors":[{"reason":"backendError"}]}}""", HttpStatusCode.InternalServerError));
        using var fs = Kit.Google(http);

        var act = () => fs.ListAsync(new VfsPath("/" + SharedDrives));

        await act.Should().ThrowAsync<CloudDriveException>();
        http.Api.Should().NotContain(r => IsDriveList(r));
    }

    // ── Shared drives ────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_SharedDrives_ListsTheDrivesAsFoldersAcrossPages()
    {
        var http = new CloudHttp(r => r.Query["pageToken"] switch
        {
            _ when !IsDriveList(r) => Found(),
            null => Kit.Json("""{"drives":[{"id":"d1","name":"Engineering"}],"nextPageToken":"p2"}"""),
            "p2" => Kit.Json("""{"drives":[{"id":"d2","name":"Sales/EU"}]}"""),
            _ => Kit.Unexpected(r),
        });
        using var fs = Kit.Google(http);

        var entries = await fs.ListAsync(new VfsPath("/" + SharedDrives));

        entries.Select(e => (e.Path.Value, e.IsDirectory)).Should().BeEquivalentTo(new[]
        {
            ($"/{SharedDrives}/Engineering", true),
            ($"/{SharedDrives}/Sales{CloudNames.SlashStandIn}EU", true),
        });
    }

    [Fact]
    public async Task ListAsync_SharedDrive_AsksInThatDrive()
    {
        var http = SharedDriveAccount();
        using var fs = Kit.Google(http);

        var entries = await fs.ListAsync(new VfsPath($"/{SharedDrives}/Engineering"));

        entries.Select(e => e.Name).Should().BeEquivalentTo("Specs", "top.txt");
        var query = http.Api[^1];
        query.Query["q"].Should().Be("'drv1' in parents and trashed = false");
        query.Query["corpora"].Should().Be("drive");
        query.Query["driveId"].Should().Be("drv1");
        query.Query["includeItemsFromAllDrives"].Should().Be("true");
        query.Query["supportsAllDrives"].Should().Be("true");
    }

    [Fact]
    public async Task ListAsync_FolderInASharedDrive_StaysInThatDrive()
    {
        var http = SharedDriveAccount();
        using var fs = Kit.Google(http);

        var entries = await fs.ListAsync(new VfsPath($"/{SharedDrives}/Engineering/Specs"));

        entries.Should().ContainSingle().Which.Name.Should().Be("plan.txt");
        http.Api[^1].Query["q"].Should().StartWith("'specs' in parents");
        http.Api[^1].Query["driveId"].Should().Be("drv1", "a folder deeper down is searched in the same drive");
    }

    [Fact]
    public async Task OpenReadAsync_FileInASharedDrive_DownloadsWithTheAllDrivesFlag()
    {
        var http = new CloudHttp(r =>
            IsDriveList(r) ? OneDrive()
            : IsQuery(r) ? (r.Query["q"]!.Contains("name = 'top.txt'") && r.Query["q"]!.StartsWith("'drv1'", StringComparison.Ordinal) ? Found(Item("top", "top.txt")) : Found())
            : Kit.Bytes("data"u8.ToArray()));
        using var fs = Kit.Google(http);

        await Kit.ReadAllAsync(await fs.OpenReadAsync(new VfsPath($"/{SharedDrives}/Engineering/top.txt")));

        var download = http.Api[^1];
        download.Uri.AbsolutePath.Should().Be("/drive/v3/files/top");
        download.Query["supportsAllDrives"].Should().Be("true", "without it Drive answers 404 for a file in a shared drive");
    }

    [Fact]
    public async Task OpenWriteAsync_IntoASharedDrive_NamesTheFolderAsParentWithTheAllDrivesFlag()
    {
        var http = SharedDriveAccount();
        using var fs = Kit.Google(http);

        await Kit.UploadAsync(fs, $"/{SharedDrives}/Engineering/new.txt", "hello"u8.ToArray());

        var upload = http.Api.Single(r => r.Method == HttpMethod.Post);
        upload.Query["supportsAllDrives"].Should().Be("true");
        upload.Text.Should().Contain("""{"name":"new.txt","parents":["drv1"]}""", "a shared drive's id is the id of its root folder");
    }

    [Fact]
    public async Task EveryChange_InASharedDrive_CarriesTheAllDrivesFlag()
    {
        var http = SharedDriveAccount();
        using var fs = Kit.Google(http);
        var plan = $"/{SharedDrives}/Engineering/Specs/plan.txt";

        await fs.RenameAsync(new VfsPath(plan), "plan2.txt", CancellationToken.None);
        await fs.CopyAsync(new VfsPath(plan), new VfsPath($"/{SharedDrives}/Engineering/copy.txt"), null, CancellationToken.None);
        await fs.CreateDirectoryAsync(new VfsPath($"/{SharedDrives}/Engineering/New"), CancellationToken.None);
        await fs.DeleteAsync(new VfsPath(plan), recursive: true, CancellationToken.None);
        await fs.MoveAsync(new VfsPath(plan), new VfsPath($"/{SharedDrives}/Engineering/moved.txt"), CancellationToken.None);

        var changes = http.Api.Where(r => r.Method != HttpMethod.Get).ToList();
        changes.Should().HaveCount(5);
        changes.Should().OnlyContain(r => r.Query["supportsAllDrives"] == "true");
    }

    [Fact]
    public async Task ChangingASharedDriveItself_IsRefusedHereAndNeverReachesDrive()
    {
        var http = SharedDriveAccount();
        using var fs = Kit.Google(http);
        var drive = new VfsPath($"/{SharedDrives}/Engineering");

        var delete = () => fs.DeleteAsync(drive, recursive: true, CancellationToken.None);
        var rename = () => fs.RenameAsync(drive, "Other", CancellationToken.None);
        var move = () => fs.MoveAsync(drive, new VfsPath("/moved"), CancellationToken.None);

        await delete.Should().ThrowAsync<NotSupportedException>().WithMessage("*shared drive*Google Drive itself*");
        await rename.Should().ThrowAsync<NotSupportedException>();
        await move.Should().ThrowAsync<NotSupportedException>();
        http.Api.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task CreatingADriveInTheList_IsRefused()
    {
        var http = SharedDriveAccount();
        using var fs = Kit.Google(http);

        var folder = () => fs.CreateDirectoryAsync(new VfsPath($"/{SharedDrives}/New"), CancellationToken.None);
        var file = () => fs.OpenWriteAsync(new VfsPath($"/{SharedDrives}/new.txt"), overwrite: true);

        await folder.Should().ThrowAsync<NotSupportedException>().WithMessage("*created in Google Drive itself*");
        await file.Should().ThrowAsync<NotSupportedException>();
        http.Api.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    // ── Shared with me ───────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_SharedWithMe_AsksForWhatWasSharedWithTheAccount()
    {
        var http = SharedWithMeAccount();
        using var fs = Kit.Google(http);

        var entries = await fs.ListAsync(new VfsPath("/" + SharedWithMe));

        entries.Select(e => (e.Path.Value, e.IsDirectory)).Should().BeEquivalentTo(new[]
        {
            ($"/{SharedWithMe}/report.txt", false),
            ($"/{SharedWithMe}/Team", true),
        });
        var query = http.Api[^1];
        query.Query["q"].Should().Be("sharedWithMe = true and trashed = false");
        query.Query["corpora"].Should().Be("user");
        query.Query["includeItemsFromAllDrives"].Should().Be("true");
        query.Query["supportsAllDrives"].Should().Be("true");
    }

    [Fact]
    public async Task ListAsync_FolderSharedWithMe_ListsItsItemsAsAnyFolder()
    {
        var http = SharedWithMeAccount();
        using var fs = Kit.Google(http);

        var entries = await fs.ListAsync(new VfsPath($"/{SharedWithMe}/Team"));

        entries.Should().ContainSingle().Which.Path.Value.Should().Be($"/{SharedWithMe}/Team/inner.txt");
        http.Api[^1].Query["q"].Should().Be("'team' in parents and trashed = false");
        http.Api[^1].Query["driveId"].Should().BeNull(
            "it may live in a shared drive this account is no member of, and naming that drive would answer 404");
    }

    [Fact]
    public async Task OpenReadAsync_FileSharedWithMe_Reads()
    {
        var http = new CloudHttp(r => IsQuery(r)
            ? (r.Query["q"]!.StartsWith("sharedWithMe", StringComparison.Ordinal) ? Found(Item("rep", "report.txt")) : Found())
            : Kit.Bytes("data"u8.ToArray()));
        using var fs = Kit.Google(http);

        var bytes = await Kit.ReadAllAsync(await fs.OpenReadAsync(new VfsPath($"/{SharedWithMe}/report.txt")));

        bytes.Should().Equal("data"u8.ToArray());
    }

    [Fact]
    public async Task CopyAsync_FromSharedWithMeToMyDrive_MakesACopyInMyDrive()
    {
        var http = SharedWithMeAccount();
        using var fs = Kit.Google(http);

        await fs.CopyAsync(new VfsPath($"/{SharedWithMe}/report.txt"), new VfsPath("/report.txt"), null, CancellationToken.None);

        var copy = http.Api.Single(r => r.Method == HttpMethod.Post);
        copy.Uri.AbsolutePath.Should().Be("/drive/v3/files/rep/copy");
        copy.Json.GetProperty("parents")[0].GetString().Should().Be("root");
    }

    [Fact]
    public async Task MoveAsync_OutOfSharedWithMe_IsRefusedAndAdvisesACopy()
    {
        var http = SharedWithMeAccount();
        using var fs = Kit.Google(http);

        var act = () => fs.MoveAsync(new VfsPath($"/{SharedWithMe}/report.txt"), new VfsPath("/report.txt"), CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>().WithMessage("*Copy it instead*");
        http.Api.Should().OnlyContain(r => r.Method == HttpMethod.Get, "the id of a view must not reach Drive as a parent to remove");
    }

    [Fact]
    public async Task MoveAsync_IntoSharedWithMe_IsRefused()
    {
        var http = new CloudHttp(r => IsQuery(r)
            ? (r.Query["q"]!.StartsWith("'root'", StringComparison.Ordinal) && r.Query["q"]!.Contains("name = 'a.txt'") ? Found(Item("a", "a.txt")) : Found())
            : Kit.Unexpected(r));
        using var fs = Kit.Google(http);

        var act = () => fs.MoveAsync(new VfsPath("/a.txt"), new VfsPath($"/{SharedWithMe}/a.txt"), CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>();
        http.Api.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task NewItems_InSharedWithMe_AreRefused()
    {
        var http = SharedWithMeAccount();
        using var fs = Kit.Google(http);

        var folder = () => fs.CreateDirectoryAsync(new VfsPath($"/{SharedWithMe}/New"), CancellationToken.None);
        var file = () => fs.OpenWriteAsync(new VfsPath($"/{SharedWithMe}/new.txt"), overwrite: true);
        var copy = () => fs.CopyAsync(new VfsPath($"/{SharedWithMe}/report.txt"), new VfsPath($"/{SharedWithMe}/copy.txt"), null, CancellationToken.None);

        await folder.Should().ThrowAsync<NotSupportedException>().WithMessage("*Shared with me*");
        await file.Should().ThrowAsync<NotSupportedException>();
        await copy.Should().ThrowAsync<NotSupportedException>();
        http.Api.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task OpenWriteAsync_OverAFileSharedWithMe_UpdatesItInPlaceWhereTheServerAllows()
    {
        var http = SharedWithMeAccount();
        using var fs = Kit.Google(http);

        await Kit.UploadAsync(fs, $"/{SharedWithMe}/report.txt", "new"u8.ToArray(), overwrite: true);

        var update = http.Api.Single(r => r.Method != HttpMethod.Get);
        update.Method.Should().Be(HttpMethod.Patch);
        update.Uri.AbsolutePath.Should().Be("/upload/drive/v3/files/rep");
    }

    [Fact]
    public async Task IntoAFolderSharedWithMe_NewItemsGoWithThatFolderAsParent()
    {
        var http = SharedWithMeAccount();
        using var fs = Kit.Google(http);

        await fs.CreateDirectoryAsync(new VfsPath($"/{SharedWithMe}/Team/New"), CancellationToken.None);

        http.Api.Single(r => r.Method == HttpMethod.Post).Json.GetProperty("parents")[0].GetString().Should().Be("team");
    }

    [Fact]
    public async Task DeleteAsync_ItemTheServerForbids_ReportsItInWordsAndDoesNotRetry()
    {
        var http = new CloudHttp(r => IsQuery(r)
            ? (r.Query["q"]!.StartsWith("sharedWithMe", StringComparison.Ordinal) ? Found(Item("rep", "report.txt")) : Found())
            : Kit.Json(
                """{"error":{"code":403,"message":"The user does not have sufficient permissions for this file.","errors":[{"reason":"insufficientFilePermissions"}]}}""",
                HttpStatusCode.Forbidden));
        using var fs = Kit.Google(http);

        var act = () => fs.DeleteAsync(new VfsPath($"/{SharedWithMe}/report.txt"), recursive: true, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<CloudDriveException>()).Which;
        thrown.Message.Should().Be("Google Drive: This account may not change this item: it belongs to someone else, or was shared read-only. (HTTP 403, insufficientFilePermissions)");
        ReconnectPolicy.IsWorthRetrying(thrown).Should().BeFalse("the panel must not reconnect over a refusal (TRAP-81)");
        http.Api.Count(r => r.Method == HttpMethod.Patch).Should().Be(1);
    }

    // ── The views themselves ─────────────────────────────────────────────────

    [Theory]
    [InlineData(SharedDrives)]
    [InlineData(SharedWithMe)]
    public async Task ChangingAVirtualFolder_IsRefusedAndItsIdNeverReachesDrive(string name)
    {
        var http = new CloudHttp(_ => Found());
        using var fs = Kit.Google(http);
        var view = new VfsPath("/" + name);

        var delete = () => fs.DeleteAsync(view, recursive: true, CancellationToken.None);
        var rename = () => fs.RenameAsync(view, "Other", CancellationToken.None);
        var move = () => fs.MoveAsync(view, new VfsPath("/moved"), CancellationToken.None);

        await delete.Should().ThrowAsync<NotSupportedException>().WithMessage("*view of Google Drive*");
        await rename.Should().ThrowAsync<NotSupportedException>();
        await move.Should().ThrowAsync<NotSupportedException>();
        http.Api.Should().OnlyContain(r => r.Method == HttpMethod.Get);
        http.Api.Should().NotContain(r => r.Url.Contains("tcv-virtual", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ANameTakenByAVirtualFolder_CannotBeWrittenOverOrTaken()
    {
        var http = new CloudHttp(r => IsQuery(r) && r.Query["q"]!.Contains("name = 'a.txt'") ? Found(Item("a", "a.txt")) : Found());
        using var fs = Kit.Google(http);

        var write = () => fs.OpenWriteAsync(new VfsPath("/" + SharedDrives), overwrite: true);
        var rename = () => fs.RenameAsync(new VfsPath("/a.txt"), SharedDrives, CancellationToken.None);

        await write.Should().ThrowAsync<IOException>().WithMessage("*is a folder*");
        await rename.Should().ThrowAsync<IOException>().WithMessage("*already exists*");
        http.Api.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task CreateDirectoryAsync_OnAVirtualFolder_IsTheFolderThatIsAlreadyThere()
    {
        var http = new CloudHttp(_ => Found());
        using var fs = Kit.Google(http);

        await fs.CreateDirectoryAsync(new VfsPath("/" + SharedWithMe), CancellationToken.None);

        http.Api.Should().OnlyContain(r => r.Method == HttpMethod.Get, "Drive would make a real folder that hides the view");
    }

    // ── Another file system, the same path ───────────────────────────────────

    [Fact]
    public async Task APathFromAnotherFileSystem_ResolvesWithoutHavingListedAnything()
    {
        // The transfer queue opens file systems of its own, and a tab comes back with only its path.
        var http = SharedDriveAccount();
        using var fs = Kit.Google(http);

        var entry = await fs.GetEntryAsync(new VfsPath($"/{SharedDrives}/Engineering/Specs/plan.txt"));

        entry.Should().NotBeNull();
        entry!.Size.Should().Be(7);
    }

    [Fact]
    public async Task ForgottenFolderIds_AreFoundAgainBelowAVirtualFolder()
    {
        var refreshed = 0;
        var inner = SharedDriveAccount();
        var http = new CloudHttp(async (r, ct) =>
        {
            // The first listing of the folder says it is gone: its id was deleted elsewhere.
            if (IsQuery(r) && r.Query["q"]!.StartsWith("'specs' in parents", StringComparison.Ordinal) && Interlocked.Increment(ref refreshed) == 1)
            {
                return Kit.Json("""{"error":{"code":404,"message":"File not found","errors":[{"reason":"notFound"}]}}""", HttpStatusCode.NotFound);
            }

            using var forward = new HttpRequestMessage(r.Method, r.Uri);
            return await new HttpMessageInvoker(inner).SendAsync(forward, ct);
        });
        using var fs = Kit.Google(http);

        var entries = await fs.ListAsync(new VfsPath($"/{SharedDrives}/Engineering/Specs"));

        entries.Should().ContainSingle().Which.Name.Should().Be("plan.txt");
    }

    [Fact]
    public void CloudPath_OfThePathsBelowAVirtualFolder_IsPlainPathArithmetic()
    {
        var path = CloudPath.Normalize($"/{SharedDrives}/Sales{CloudNames.SlashStandIn}EU/a.txt");

        CloudPath.IsRoot(path).Should().BeFalse();
        CloudPath.Name(path).Should().Be("a.txt");
        CloudPath.Parent(CloudPath.Parent(path)).Should().Be("/" + SharedDrives);
        CloudPath.IsSameOrUnder(path, "/" + SharedDrives, StringComparison.Ordinal).Should().BeTrue();
        CloudPath.IsSameOrUnder("/" + SharedDrives + " (Google)/a.txt", "/" + SharedDrives, StringComparison.Ordinal)
            .Should().BeFalse("the second name is another folder, not a folder under the first");
    }
}
