using System.IO;
using FluentAssertions;
using TCV.App.Agents;
using TCV.App.Operations;
using TCV.App.Services;
using TCV.App.ViewModels.Dialogs;
using TCV.Core.Domain;
using TCV.Core.Interfaces;
using TCV.Network;

namespace TCV.App.Tests;

/// <summary>
/// INV-10 for the writers that run beside the operation queue (6.49): transfer-queue runs and remote-edit
/// write-backs become operation-log lines shaped like a copy's, which the log's readers understand.
/// </summary>
public sealed class RemoteTransferLogTests : IDisposable
{
    private static readonly DateTime At = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Local);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tcv-rtlog-" + Guid.NewGuid().ToString("N"));

    public RemoteTransferLogTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
    }

    private static TransferItem Item(TransferDirection direction, string source, string destination) => new()
    {
        SourcePath = source,
        DestinationPath = destination,
        Direction = direction,
        Connection = new ConnectionInfo { Protocol = NetworkProtocol.Sftp, Host = "example.org", Port = 22, Username = "deploy" },
    };

    [Fact]
    public void Entry_AFolderMadeForATransfer_IsANewFolderLine()
    {
        var downloaded = new TransferItem
        {
            SourcePath = "/www/sub",
            DestinationPath = "C:/in/www/sub",
            Direction = TransferDirection.Download,
            Connection = new ConnectionInfo { Protocol = NetworkProtocol.Sftp, Host = "example.org", Port = 22, Username = "me" },
            IsFolder = true,
        };
        var uploaded = new TransferItem
        {
            SourcePath = @"C:\in\www\sub",
            DestinationPath = "/www/sub",
            Direction = TransferDirection.Upload,
            Connection = new ConnectionInfo { Protocol = NetworkProtocol.Sftp, Host = "example.org", Port = 22, Username = "me" },
            IsFolder = true,
        };

        var local = RemoteTransferLog.Entry(new TransferFinishedEventArgs(downloaded, TransferStatus.Complete, null), At)!;
        var remote = RemoteTransferLog.Entry(new TransferFinishedEventArgs(uploaded, TransferStatus.Complete, null), At)!;

        local.Kind.Should().Be(OperationKind.CreateFolder);
        local.Source.Should().Be(@"C:\in\www\sub");
        remote.Kind.Should().Be(OperationKind.CreateFolder);
        remote.Source.Should().Be("sftp://me@example.org/www/sub");
    }

    // ── The descriptor ────────────────────────────────────────────────────────

    [Fact]
    public void Describe_Download_IsTheServerFileIntoTheLocalFolder()
    {
        var d = RemoteTransferLog.Describe(TransferDirection.Download, "/var/www/app.js", @"D:\proj\app.js");

        d.Should().Be(new OperationLogDescriptor(OperationKind.Download, "/var/www/app.js", @"D:\proj", 1) { ArrivedIn = @"D:\proj" });
    }

    [Fact]
    public void Describe_Upload_IsTheLocalFileIntoTheServerFolder()
    {
        var d = RemoteTransferLog.Describe(TransferDirection.Upload, @"D:\proj\app.js", "/var/www/app.js");

        d.Should().Be(new OperationLogDescriptor(OperationKind.Upload, @"D:\proj\app.js", "/var/www", 1) { ArrivedIn = "/var/www" });
    }

    [Fact]
    public void Describe_RenamedOnArrival_NamesTheWholeDestination()
    {
        // "Rename automatically" in the conflict dialog: the folder alone would hide the new name.
        var d = RemoteTransferLog.Describe(TransferDirection.Download, "/var/www/app.js", @"D:\proj\app (1).js");

        d.Destination.Should().Be(@"D:\proj\app (1).js");
        d.ArrivedIn.Should().Be(@"D:\proj", "the name index rescans the folder the file landed in (6.63)");
    }

    [Theory]
    [InlineData(TransferDirection.Upload, @"D:\a.txt", "/a.txt", "/")]
    [InlineData(TransferDirection.Download, "/a.txt", @"D:\a.txt", @"D:\")]
    public void Describe_FileInARoot_KeepsTheRootSeparator(
        TransferDirection direction, string source, string destination, string expected) =>
        RemoteTransferLog.Describe(direction, source, destination).Destination.Should().Be(expected);

    [Fact]
    public void Describe_LocalPathBuiltFromAPanelPath_IsWrittenTheWindowsWay()
    {
        // The queue combines the panel's "D:/proj" with the name, which gives a mixed path.
        var d = RemoteTransferLog.Describe(TransferDirection.Download, "/srv/a.txt", @"D:/proj\a.txt");

        d.Destination.Should().Be(@"D:\proj");
    }

    // ── Naming the server (6.61) ──────────────────────────────────────────────

    [Fact]
    public void Describe_UploadOverAConnection_PutsTheServerBeforeTheFolder()
    {
        var server = new ConnectionInfo { Protocol = NetworkProtocol.Ftps, Host = "files.example.org", Port = 2121, Username = "me", Password = "secret" };

        var d = RemoteTransferLog.Describe(TransferDirection.Upload, @"D:\proj\app.js", "/var/www/app.js", server);

        d.Source.Should().Be(@"D:\proj\app.js");
        d.Destination.Should().Be("ftps://me@files.example.org:2121/var/www");
        d.Destination.Should().NotContain("secret");
    }

    [Fact]
    public void Describe_DownloadFromTheServerRoot_KeepsTheRootAndNamesTheServer()
    {
        var server = new ConnectionInfo { Protocol = NetworkProtocol.Sftp, Host = "example.org", Port = 22 };

        var up = RemoteTransferLog.Describe(TransferDirection.Upload, @"D:\a.txt", "/a.txt", server);
        var down = RemoteTransferLog.Describe(TransferDirection.Download, "/a.txt", @"D:\in\a.txt", server);

        up.Destination.Should().Be("sftp://example.org/");
        down.Source.Should().Be("sftp://example.org/a.txt");
        down.Destination.Should().Be(@"D:\in");
    }

    [Theory]
    [InlineData(NetworkProtocol.Sftp, 22, "sftp://h/x")]
    [InlineData(NetworkProtocol.Sftp, 2222, "sftp://h:2222/x")]
    [InlineData(NetworkProtocol.Ftp, 21, "ftp://h/x")]
    [InlineData(NetworkProtocol.WebDavs, 443, "davs://h/x")]
    public void OnServer_DefaultPort_IsLeftOut(NetworkProtocol protocol, int port, string expected) =>
        RemoteTransferLog.OnServer(new ConnectionInfo { Protocol = protocol, Host = "h", Port = port }, "/x")
            .Should().Be(expected);

    [Fact]
    public void OnServer_IPv6Host_IsBracketed() =>
        RemoteTransferLog.OnServer(new ConnectionInfo { Protocol = NetworkProtocol.Sftp, Host = "::1", Port = 22 }, "/x")
            .Should().Be("sftp://[::1]/x");

    [Fact]
    public void ToWriteFacts_DownloadNamedWithItsServer_StillClaimsTheLocalFileOnly()
    {
        var item = Item(TransferDirection.Download, "/srv/app.cs", @"D:\repo\src\app.cs");
        var download = RemoteTransferLog.Entry(new TransferFinishedEventArgs(item, TransferStatus.Complete, null), At)!;

        AgentChangeSet.ToWriteFacts([download]).Should().ContainSingle()
            .Which.Paths.Should().Equal(@"D:\repo\src\app.cs");
    }

    // ── Transfer-queue runs ───────────────────────────────────────────────────

    [Theory]
    [InlineData(TransferStatus.Complete, OperationOutcome.Completed)]
    [InlineData(TransferStatus.Failed, OperationOutcome.Failed)]
    [InlineData(TransferStatus.Cancelled, OperationOutcome.Cancelled)]
    public void Entry_EndOfARun_IsALineWithItsOutcome(TransferStatus status, OperationOutcome expected)
    {
        var item = Item(TransferDirection.Download, "/srv/a.txt", @"D:\in\a.txt");
        var error = status == TransferStatus.Failed ? "connection reset" : null;

        var entry = RemoteTransferLog.Entry(new TransferFinishedEventArgs(item, status, error), At);

        entry.Should().NotBeNull();
        entry!.Outcome.Should().Be(expected);
        entry.Error.Should().Be(error);
        entry.Kind.Should().Be(OperationKind.Download);
        entry.Source.Should().Be("sftp://deploy@example.org/srv/a.txt", "a server path alone does not say which server");
        entry.Destination.Should().Be(@"D:\in");
        entry.ArrivedIn.Should().Be(@"D:\in");
        entry.ItemCount.Should().Be(1);
        entry.Timestamp.Should().Be(At);
        entry.Origin.Should().BeNull("the user queued it, not an agent");
    }

    [Theory]
    [InlineData(TransferStatus.Pending)]
    [InlineData(TransferStatus.Transferring)]
    [InlineData(TransferStatus.Paused)]
    public void Entry_StateThatIsNotAnEnd_WritesNothing(TransferStatus status)
    {
        var item = Item(TransferDirection.Upload, @"D:\a.txt", "/srv/a.txt");

        RemoteTransferLog.Entry(new TransferFinishedEventArgs(item, status, null), At).Should().BeNull();
    }

    [Fact]
    public async Task Attach_RunThatEnds_IsWrittenToTheLog()
    {
        // An upload of a file that is not there fails before any connection is made.
        var log = new OperationLog(Path.Combine(_dir, "operation-log.json"));
        using var queue = new TransferQueue();
        RemoteTransferLog.Attach(queue, log);
        var missing = Path.Combine(_dir, "missing.bin");

        queue.Enqueue(new TransferItem
        {
            SourcePath = missing,
            DestinationPath = "/up/missing.bin",
            Direction = TransferDirection.Upload,
            Connection = new ConnectionInfo { Protocol = NetworkProtocol.WebDav, Host = "127.0.0.1", Port = 1 },
        });

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (log.LoadAll().Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        var entry = log.LoadAll().Should().ContainSingle().Subject;
        entry.Kind.Should().Be(OperationKind.Upload);
        entry.Outcome.Should().Be(OperationOutcome.Failed);
        entry.Source.Should().Be(missing);
        entry.Destination.Should().Be("dav://127.0.0.1:1/up");
        entry.Error.Should().NotBeNullOrEmpty();
    }

    // ── Remote-edit write-backs ───────────────────────────────────────────────

    [Theory]
    [InlineData(RemoteEditOutcome.Uploaded, OperationOutcome.Completed)]
    [InlineData(RemoteEditOutcome.Failed, OperationOutcome.Failed)]
    [InlineData(RemoteEditOutcome.Declined, OperationOutcome.Cancelled)]
    public void RemoteSave_IsAnUploadOfTheTemporaryCopy(RemoteEditOutcome outcome, OperationOutcome expected)
    {
        var copy = @"C:\Temp\tcv-edit-1a2b3c4d\nginx.conf";
        var error = outcome == RemoteEditOutcome.Failed ? "connection reset" : null;

        var entry = RemoteTransferLog.RemoteSave(copy, "/etc/nginx/nginx.conf", outcome, error, At);

        entry.Kind.Should().Be(OperationKind.Upload);
        entry.Source.Should().Be(copy, "after a failed save the edits are only there, and the log says where");
        entry.Destination.Should().Be("/etc/nginx");
        entry.Outcome.Should().Be(expected);
        entry.Error.Should().Be(error);
        entry.ItemCount.Should().Be(1);
    }

    [Theory]
    [InlineData("sftp", "sftp://deploy@example.org", "sftp://deploy@example.org/etc/nginx")]
    [InlineData("ftp", "bob@example.org", "ftp://bob@example.org/etc/nginx")]
    [InlineData("ftps", "bob@example.org", "ftps://bob@example.org/etc/nginx")]
    [InlineData("ftp", "@example.org", "ftp://example.org/etc/nginx")]
    [InlineData("dav", "example.org:8080", "dav://example.org:8080/etc/nginx")]
    [InlineData("s3", "my-bucket", "s3://my-bucket/etc/nginx")]
    public void RemoteSave_NamesTheServerTheSameWayWhateverItsKind(string scheme, string displayName, string expected)
    {
        var entry = RemoteTransferLog.RemoteSave(
            @"C:\Temp\nginx.conf", "/etc/nginx/nginx.conf", RemoteEditOutcome.Uploaded, null, At, new NamedFileSystem(scheme, displayName));

        entry.Destination.Should().Be(expected);
        entry.ArrivedIn.Should().Be(expected);
    }

    [Fact]
    public void RemoteSave_MatchesWhatTheQueueWritesForTheSameServer()
    {
        var server = new ConnectionInfo { Protocol = NetworkProtocol.Sftp, Host = "example.org", Port = 22, Username = "deploy" };
        var queued = RemoteTransferLog.Describe(TransferDirection.Upload, @"C:\Temp\a.txt", "/srv/a.txt", server);

        var saved = RemoteTransferLog.RemoteSave(
            @"C:\Temp\a.txt", "/srv/a.txt", RemoteEditOutcome.Uploaded, null, At, new NamedFileSystem("sftp", "sftp://deploy@example.org"));

        saved.Destination.Should().Be(queued.Destination);
    }

    [Fact]
    public void RemoteSave_FileInTheServersRoot_KeepsTheRoot()
    {
        var entry = RemoteTransferLog.RemoteSave(
            @"C:\Temp\a.txt", "/a.txt", RemoteEditOutcome.Uploaded, null, At, new NamedFileSystem("ftp", "bob@example.org"));

        entry.Destination.Should().Be("ftp://bob@example.org/");
    }

    [Fact]
    public void RemoteSave_FindInASearchOnAServer_NamesThatServerNotTheView()
    {
        var results = new TCV.App.FileSystem.SearchResultsFileSystem(new NamedFileSystem("ftp", "bob@example.org"), []);

        var entry = RemoteTransferLog.RemoteSave(@"C:\Temp\a.txt", "/srv/a.txt", RemoteEditOutcome.Uploaded, null, At, results);

        entry.Destination.Should().Be("ftp://bob@example.org/srv");
    }

    [Theory]
    [InlineData("zip", "archive.zip")]
    [InlineData("file", "Local File System")]
    public void RemoteSave_NotAServer_WritesThePathAlone(string scheme, string displayName)
    {
        var entry = RemoteTransferLog.RemoteSave(
            @"C:\Temp\a.txt", "/in/a.txt", RemoteEditOutcome.Uploaded, null, At, new NamedFileSystem(scheme, displayName));

        entry.Destination.Should().Be("/in");
    }

    /// <summary>All the log asks of a file system is what it calls itself.</summary>
    private sealed class NamedFileSystem(string scheme, string displayName) : IVirtualFileSystem
    {
        public string Scheme => scheme;
        public string DisplayName => displayName;
        public event EventHandler<VfsChangedEventArgs>? Changed { add { } remove { } }
        public Task<IReadOnlyList<IFileEntry>> ListAsync(VfsPath path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IFileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite = false, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CopyAsync(VfsPath source, VfsPath destination, IProgress<FileProgress>? progress, CancellationToken ct) => throw new NotSupportedException();
        public Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct) => throw new NotSupportedException();
        public Task CreateDirectoryAsync(VfsPath path, CancellationToken ct) => throw new NotSupportedException();
        public Task RenameAsync(VfsPath path, string newName, CancellationToken ct) => throw new NotSupportedException();
        public bool SupportsOperation(VfsOperation operation) => false;

        public void Dispose()
        {
        }
    }

    // ── What the log's readers make of the lines ──────────────────────────────

    [Fact]
    public void ToWriteFacts_Upload_ClaimsNothingOnThisDisk()
    {
        // An upload only reads the local file: claiming it would credit an agent's edit to the manager.
        var upload = RemoteTransferLog.RemoteSave(@"D:\repo\src\app.cs", "/srv/app.cs", RemoteEditOutcome.Uploaded, null, At);

        AgentChangeSet.ToWriteFacts([upload]).Should().BeEmpty();
    }

    [Fact]
    public void ToWriteFacts_Download_ClaimsTheDownloadedFile()
    {
        var item = Item(TransferDirection.Download, "/srv/app.cs", @"D:\repo\src\app.cs");
        var download = RemoteTransferLog.Entry(new TransferFinishedEventArgs(item, TransferStatus.Complete, null), At)!;

        var fact = AgentChangeSet.ToWriteFacts([download]).Should().ContainSingle().Subject;

        fact.Paths.Should().Equal(@"D:\repo\src\app.cs");
        fact.Origin.Should().BeEmpty();
    }

    [Fact]
    public void ToWriteFacts_DownloadRenamedOnArrival_ClaimsTheFileUnderItsNewName()
    {
        var item = Item(TransferDirection.Download, "/srv/app.cs", @"D:\repo\src\app.local.cs");
        var download = RemoteTransferLog.Entry(new TransferFinishedEventArgs(item, TransferStatus.Complete, null), At)!;

        AgentChangeSet.ToWriteFacts([download]).Should().ContainSingle()
            .Which.Paths.Should().Equal(@"D:\repo\src\app.local.cs");
    }

    [Fact]
    public void KindKey_EveryKind_HasItsOwnName()
    {
        // A kind without a case is shown as «Other» in the log window.
        Enum.GetValues<OperationKind>()
            .Where(kind => OperationLogRow.KindKey(kind) == "Log_KindOther")
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData(OperationKind.Download)]
    [InlineData(OperationKind.Upload)]
    public void FromLogEntry_Transfer_IsNotOfferedForReplay(OperationKind kind) =>
        SavedOperation.FromLogEntry(kind, "/srv/a.txt", @"D:\in", "again").Should().BeNull();
}
