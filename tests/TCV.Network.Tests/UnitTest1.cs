using System.ComponentModel;
using FluentAssertions;

namespace TCV.Network.Tests;

public class NetworkPluginTests
{
    [Theory]
    [InlineData(NetworkProtocol.Ftp, "ftp", 21)]
    [InlineData(NetworkProtocol.Ftps, "ftps", 21)]
    [InlineData(NetworkProtocol.FtpsImplicit, "ftps", 990)]
    [InlineData(NetworkProtocol.WebDav, "dav", 80)]
    [InlineData(NetworkProtocol.WebDavs, "davs", 443)]
    [InlineData(NetworkProtocol.Sftp, "sftp", 22)]
    public void ConnectionInfo_ReportsSchemeAndDefaultPort(
        NetworkProtocol protocol, string scheme, int port)
    {
        var info = new ConnectionInfo { Protocol = protocol };

        info.GetScheme().Should().Be(scheme);
        info.DefaultPort().Should().Be(port);
    }

    [Theory]
    [InlineData(NetworkProtocol.Ftp)]
    [InlineData(NetworkProtocol.Ftps)]
    public void CreateFileSystem_BuildsAnFtpVfsForFtpProtocols(NetworkProtocol protocol)
    {
        var info = new ConnectionInfo
        {
            Protocol = protocol,
            Host = "example.org",
            Port = 21,
            Username = "anonymous",
        };

        using var vfs = NetworkPlugin.CreateFileSystem(info);

        vfs.Should().BeOfType<FtpFileSystem>();
        vfs.Scheme.Should().Be(info.GetScheme());
    }

    [Fact]
    public void CreateFileSystem_BuildsAnSftpVfsForSftp()
    {
        var info = new ConnectionInfo
        {
            Protocol = NetworkProtocol.Sftp,
            Host = "example.org",
            Port = 22,
            Username = "user",
            Password = "secret",
        };

        using var vfs = NetworkPlugin.CreateFileSystem(info);

        vfs.Should().BeOfType<SftpFileSystem>();
        vfs.Scheme.Should().Be("sftp");
    }

    [Fact]
    public void CreateFileSystem_RejectsNull()
    {
        var act = () => NetworkPlugin.CreateFileSystem(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Create_FromUri_CarriesCredentialsAndPath()
    {
        var plugin = new NetworkPlugin();

        using var vfs = plugin.Create("ftp://bob:pw@files.example.org:2121/incoming");

        vfs.Should().BeOfType<FtpFileSystem>();
        vfs.DisplayName.Should().Be("bob@files.example.org");
    }
}

public class TransferItemTests
{
    private static TransferItem Make(long total = 1000) => new()
    {
        SourcePath = "/pub/data/report.csv",
        DestinationPath = @"C:\downloads\report.csv",
        TotalBytes = total,
        Connection = new ConnectionInfo { Host = "example.org" },
        Direction = TransferDirection.Download,
    };

    [Fact]
    public void FileName_IsTakenFromTheSourcePath()
    {
        Make().FileName.Should().Be("report.csv");
    }

    [Fact]
    public void FileName_HandlesWindowsSourcePathsForUploads()
    {
        var item = new TransferItem { SourcePath = @"C:\work\notes.txt" };
        item.FileName.Should().Be("notes.txt");
    }

    [Fact]
    public void PercentComplete_TracksBytesAgainstTotal()
    {
        var item = Make(total: 1000);
        item.PercentComplete.Should().Be(0);

        item.BytesTransferred = 250;
        item.PercentComplete.Should().Be(25);
    }

    [Fact]
    public void PercentComplete_IsZeroWhileTheSizeIsUnknown()
    {
        var item = Make(total: -1);
        item.BytesTransferred = 500;

        item.PercentComplete.Should().Be(0);
    }

    [Fact]
    public void PercentComplete_NeverExceedsOneHundred()
    {
        var item = Make(total: 100);
        item.BytesTransferred = 250;

        item.PercentComplete.Should().Be(100);
    }

    [Fact]
    public void BytesTransferred_NotifiesProgressSoTheQueueWindowUpdates()
    {
        var item = Make();
        var changed = new List<string?>();
        ((INotifyPropertyChanged)item).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        item.BytesTransferred = 500;

        changed.Should().Contain(nameof(TransferItem.BytesTransferred));
        changed.Should().Contain(nameof(TransferItem.PercentComplete));
    }

    [Fact]
    public void Status_NotifiesOnChangeOnly()
    {
        var item = Make();
        var changed = new List<string?>();
        ((INotifyPropertyChanged)item).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        item.Status = TransferStatus.Transferring;
        item.Status = TransferStatus.Transferring;   // same value — no second event

        changed.Should().ContainSingle(n => n == nameof(TransferItem.Status));
    }

    [Fact]
    public void NewItem_StartsPendingWithNothingTransferred()
    {
        var item = Make();

        item.Status.Should().Be(TransferStatus.Pending);
        item.BytesTransferred.Should().Be(0);
        item.ErrorMessage.Should().BeNull();
    }
}

public class TransferQueueTests
{
    [Fact]
    public void Enqueue_AddsTheItemToTheVisibleQueue()
    {
        using var queue = new TransferQueue();
        var item = new TransferItem
        {
            SourcePath = "/pub/x.bin",
            DestinationPath = @"C:\tmp\x.bin",
            // Host is unreachable: the transfer fails, but enqueueing must still be observable
            Connection = new ConnectionInfo { Host = "invalid.invalid", Port = 21 },
            Direction = TransferDirection.Download,
        };

        queue.Enqueue(item);

        queue.Items.Should().ContainSingle().Which.Should().BeSameAs(item);
    }

    [Fact]
    public void Cancel_MarksAPendingItemCancelled()
    {
        using var queue = new TransferQueue();
        var item = new TransferItem
        {
            SourcePath = "/pub/y.bin",
            Connection = new ConnectionInfo { Host = "invalid.invalid" },
        };
        queue.AddWithoutStarting(item);

        queue.Cancel(item.Id);

        item.Status.Should().Be(TransferStatus.Cancelled);
    }

    [Fact]
    public void Cancel_WithAnUnknownIdIsIgnored()
    {
        using var queue = new TransferQueue();

        var act = () => queue.Cancel(Guid.NewGuid());

        act.Should().NotThrow();
    }

    [Fact]
    public void Pause_OnlyAppliesToARunningTransfer()
    {
        using var queue = new TransferQueue();
        var item = new TransferItem { Connection = new ConnectionInfo() };
        queue.AddWithoutStarting(item);

        queue.Pause(item.Id);

        item.Status.Should().Be(TransferStatus.Pending, "a queued item is not running yet");
    }
}
