using System.Collections.Specialized;
using FluentAssertions;
using TCV.Network;

namespace TCV.Network.Tests;

/// <summary>
/// What a folder put in the queue asks of it (6.96): items that make a folder, and 100 000 items at once
/// that must not reach the window one notification at a time (TRAP-107).
/// </summary>
public sealed class TransferQueueFolderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tcv-queue-" + Guid.NewGuid().ToString("N"));
    private readonly TransferQueue _queue = new();

    public TransferQueueFolderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _queue.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
    }

    private static async Task<bool> WaitFor(Func<bool> condition, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }

    private TransferItem FolderItem(string destination, string display) => new()
    {
        SourcePath = "/www/sub",
        DestinationPath = destination,
        Direction = TransferDirection.Download,
        Connection = new ConnectionInfo { Protocol = NetworkProtocol.Ftp, Host = "127.0.0.1", Port = 1 },
        IsFolder = true,
        DisplayPath = display,
    };

    [Fact]
    public async Task Enqueue_AFolderItemOfADownload_MakesTheFolderWithoutAConnection()
    {
        var target = Path.Combine(_dir, "www", "sub", "deep");
        var item = FolderItem(target, "www/sub/deep");

        _queue.Enqueue(item);

        (await WaitFor(() => item.Status == TransferStatus.Complete)).Should().BeTrue(item.ErrorMessage);
        Directory.Exists(target).Should().BeTrue();
        item.PercentComplete.Should().Be(100);
    }

    [Fact]
    public async Task Enqueue_AFolderThatIsAlreadyThere_IsFine()
    {
        var target = Path.Combine(_dir, "www");
        Directory.CreateDirectory(target);
        var item = FolderItem(target, "www");

        _queue.Enqueue(item);

        (await WaitFor(() => item.Status == TransferStatus.Complete)).Should().BeTrue(item.ErrorMessage);
    }

    [Fact]
    public async Task Enqueue_AFolderItem_ReportsItsEnd()
    {
        var finished = new TaskCompletionSource<TransferFinishedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Finished += (_, e) => finished.TrySetResult(e);

        _queue.Enqueue(FolderItem(Path.Combine(_dir, "www"), "www"));

        var args = await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
        args.Status.Should().Be(TransferStatus.Complete);
        args.Item.IsFolder.Should().BeTrue();
    }

    [Fact]
    public void DisplayName_OfAnItemFromAFolder_IsItsPathUnderTheSelection()
    {
        new TransferItem { SourcePath = "/www/a/b.txt", DisplayPath = "www/a/b.txt" }.DisplayName.Should().Be("www/a/b.txt");
        new TransferItem { SourcePath = "/www/a/b.txt" }.DisplayName.Should().Be("b.txt");
    }

    private static List<TransferItem> ManyFinishedOrWaiting(int count, Func<int, string> destination) =>
        Enumerable.Range(0, count).Select(i => new TransferItem
        {
            SourcePath = "/f" + i,
            DestinationPath = destination(i),
            Direction = TransferDirection.Download,
            Connection = new ConnectionInfo { Protocol = NetworkProtocol.Ftp, Host = "127.0.0.1", Port = 1 },
            IsFolder = true,
        }).ToList();

    [Fact]
    public async Task EnqueueRange_100000Items_TheWindowHearsOnceAndAllAreThere()
    {
        var notifications = 0;
        _queue.Items.CollectionChanged += (_, _) => Interlocked.Increment(ref notifications);
        var root = Path.Combine(_dir, "many");

        _queue.EnqueueRange(ManyFinishedOrWaiting(100_000, _ => root));

        _queue.Items.Should().HaveCount(100_000);
        notifications.Should().Be(1, "a grid told 100 000 times locks the program up");

        (await WaitFor(() => !_queue.HasActiveWork, 120)).Should().BeTrue("the worker must not search the whole list for each next item");
    }

    [Fact]
    public void EnqueueRange_AFewItems_AreOrdinaryChanges()
    {
        var actions = new List<NotifyCollectionChangedAction>();
        _queue.Items.CollectionChanged += (_, e) => actions.Add(e.Action);
        var root = Path.Combine(_dir, "few");

        _queue.EnqueueRange(ManyFinishedOrWaiting(3, _ => root));

        // The selection and the scroll position in the window do not notice these.
        actions.Should().Equal(NotifyCollectionChangedAction.Add, NotifyCollectionChangedAction.Add, NotifyCollectionChangedAction.Add);
    }

    [Fact]
    public void EnqueueRange_Nothing_DoesNothing()
    {
        var notifications = 0;
        _queue.Items.CollectionChanged += (_, _) => notifications++;

        _queue.EnqueueRange([]);

        notifications.Should().Be(0);
    }

    [Fact]
    public async Task ClearFinished_ManyItems_TheWindowHearsOnceAndOnlyTheFinishedGo()
    {
        var root = Path.Combine(_dir, "clear");
        _queue.EnqueueRange(ManyFinishedOrWaiting(5_000, _ => root));
        (await WaitFor(() => !_queue.HasActiveWork, 60)).Should().BeTrue();
        _queue.AddWithoutStarting(new TransferItem { SourcePath = "/waits", DestinationPath = root, IsFolder = true });
        var notifications = 0;
        _queue.Items.CollectionChanged += (_, _) => notifications++;

        _queue.ClearFinished();

        _queue.Items.Should().ContainSingle().Which.SourcePath.Should().Be("/waits");
        notifications.Should().Be(1);
    }
}
