using System.Text;
using FluentAssertions;
using TCV.Core.Domain;
using TCV.Network;

namespace TCV.Network.Tests;

/// <summary>
/// The connection model shared by every protocol: the book keeps the new fields and encrypts the
/// browser sign-in's secrets, and a read-only connection refuses every write.
/// </summary>
public sealed class ConnectionModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tcv-model-" + Guid.NewGuid().ToString("N"));

    public ConnectionModelTests() => Directory.CreateDirectory(_dir);

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

    private string BookPath => Path.Combine(_dir, "connections.json");

    [Fact]
    public void Book_KeepsTheObjectStorageAndPresentationFields()
    {
        var info = new ConnectionInfo
        {
            Name = "logs", Protocol = NetworkProtocol.S3, Host = "s3.eu-west-1.amazonaws.com", Port = 443,
            Username = "AKIAEXAMPLE", Password = "secret", Region = "eu-west-1", Bucket = "logs",
            PathStyle = true, UseHttp = false, Provider = "aws", Profile = "work", Color = "#E5484D", ReadOnly = true,
        };
        new ConnectionBook(BookPath).Add(info);

        var loaded = new ConnectionBook(BookPath).Connections.Single();

        loaded.Should().BeEquivalentTo(info, o => o.Excluding(c => c.SecretsChanged));
    }

    [Fact]
    public void Book_EncryptsTheClientSecretAndTheRefreshToken()
    {
        var info = new ConnectionInfo
        {
            Name = "drive", Protocol = NetworkProtocol.GoogleDrive, ClientId = "client.apps.example",
            ClientSecret = "GOCSPX-plain-secret", RefreshToken = "1//refresh-token-plain",
        };
        new ConnectionBook(BookPath).Add(info);

        var json = File.ReadAllText(BookPath);
        json.Should().NotContain("GOCSPX-plain-secret").And.NotContain("1//refresh-token-plain");
        json.Should().Contain("client.apps.example", "a client id is no secret");

        var loaded = new ConnectionBook(BookPath).Connections.Single();
        loaded.ClientSecret.Should().Be("GOCSPX-plain-secret");
        loaded.RefreshToken.Should().Be("1//refresh-token-plain");
    }

    [Fact]
    public void Book_AnOlderFileWithoutTheNewFields_StillReads()
    {
        File.WriteAllText(BookPath,
            """[{"Id":"7d1c5d0e-5a0e-4c39-9f6e-2f3f0b4f3a11","Name":"old","Protocol":2,"Host":"h","Port":22,"Username":"u","AuthMethod":0,"RemotePath":"/"}]""");

        var loaded = new ConnectionBook(BookPath).Connections.Single();

        loaded.Protocol.Should().Be(NetworkProtocol.Sftp);
        loaded.Bucket.Should().BeNull();
        loaded.ReadOnly.Should().BeFalse();
    }

    [Fact]
    public void Clone_CopiesEveryFieldButTheSecretsListener()
    {
        var info = new ConnectionInfo { Name = "x", RefreshToken = "t", SecretsChanged = _ => { } };

        var copy = info.Clone();

        copy.Should().NotBeSameAs(info);
        copy.RefreshToken.Should().Be("t");
        copy.SecretsChanged.Should().BeNull("the copy is not the connection a file system reports to");
    }

    [Theory]
    [InlineData(NetworkProtocol.AzureBlob, true, "sv=2024-01-01&sig=abc", true)]
    [InlineData(NetworkProtocol.AzureBlob, true, "base64accountkey==", false)]
    [InlineData(NetworkProtocol.AzureBlob, false, "sv=2024-01-01&sig=abc", false)]
    [InlineData(NetworkProtocol.S3, true, "secret", false)]
    public void SendsCredentialsInClear_OnlyWhenASecretTravelsReadable(
        NetworkProtocol protocol, bool useHttp, string secret, bool expected)
    {
        var info = new ConnectionInfo { Protocol = protocol, UseHttp = useHttp, Username = "u", Password = secret };

        info.SendsCredentialsInClear.Should().Be(expected);
    }

    // ── Read-only ────────────────────────────────────────────────────────────

    private sealed class OneFile : TCV.Core.Interfaces.IVirtualFileSystem
    {
        public bool Disposed { get; private set; }
        public string Scheme => "s3";
        public string DisplayName => "bucket";
#pragma warning disable CS0067 // Never raised: the test file system does not change by itself.
        public event EventHandler<VfsChangedEventArgs>? Changed;
#pragma warning restore CS0067

        public Task<IReadOnlyList<TCV.Core.Interfaces.IFileEntry>> ListAsync(VfsPath path, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TCV.Core.Interfaces.IFileEntry>>([]);

        public Task<TCV.Core.Interfaces.IFileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct = default) =>
            Task.FromResult<TCV.Core.Interfaces.IFileEntry?>(null);

        public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct = default) =>
            Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("data")));

        public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite = false, CancellationToken ct = default) =>
            Task.FromResult<Stream>(new MemoryStream());

        public Task CopyAsync(VfsPath source, VfsPath destination, IProgress<FileProgress>? progress, CancellationToken ct) => Task.CompletedTask;
        public Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct) => Task.CompletedTask;
        public Task CreateDirectoryAsync(VfsPath path, CancellationToken ct) => Task.CompletedTask;
        public Task RenameAsync(VfsPath path, string newName, CancellationToken ct) => Task.CompletedTask;
        public bool SupportsOperation(VfsOperation operation) => true;
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task ReadOnly_ReadsGoThrough()
    {
        var fs = new ReadOnlyFileSystem(new OneFile());

        using var reader = new StreamReader(await fs.OpenReadAsync(new VfsPath("/a.txt")));
        (await reader.ReadToEndAsync()).Should().Be("data");
        fs.SupportsOperation(VfsOperation.Read).Should().BeTrue();
    }

    [Fact]
    public async Task ReadOnly_EveryWriteIsRefused()
    {
        var fs = new ReadOnlyFileSystem(new OneFile());
        var path = new VfsPath("/a.txt");

        await FluentActions.Awaiting(() => fs.OpenWriteAsync(path, true)).Should().ThrowAsync<ReadOnlyConnectionException>();
        await FluentActions.Awaiting(() => fs.DeleteAsync(path, true, default)).Should().ThrowAsync<ReadOnlyConnectionException>();
        await FluentActions.Awaiting(() => fs.RenameAsync(path, "b.txt", default)).Should().ThrowAsync<ReadOnlyConnectionException>();
        await FluentActions.Awaiting(() => fs.MoveAsync(path, new VfsPath("/b"), default)).Should().ThrowAsync<ReadOnlyConnectionException>();
        await FluentActions.Awaiting(() => fs.CopyAsync(path, new VfsPath("/b"), null, default)).Should().ThrowAsync<ReadOnlyConnectionException>();
        await FluentActions.Awaiting(() => fs.CreateDirectoryAsync(new VfsPath("/d"), default)).Should().ThrowAsync<ReadOnlyConnectionException>();
        fs.SupportsOperation(VfsOperation.Write).Should().BeFalse("F4 asks this before uploading an edit");
    }

    [Fact]
    public void ReadOnly_DisposeClosesTheConnectionUnderneath()
    {
        var inner = new OneFile();

        new ReadOnlyFileSystem(inner).Dispose();

        inner.Disposed.Should().BeTrue();
    }
}
