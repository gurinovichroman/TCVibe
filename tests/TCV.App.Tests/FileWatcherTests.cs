using System.IO;
using FluentAssertions;
using TCV.App.FileSystem;

namespace TCV.App.Tests;

/// <summary>
/// The panel's folder watcher starts off the caller's thread: entering a share that stopped answering
/// froze the window for its whole timeout, with Cancel out of reach. The last start wins.
/// </summary>
public sealed class FileWatcherTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly string _a = NewFolder();
    private readonly string _b = NewFolder();

    private static string NewFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "tcv-watcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        Directory.Delete(_a, recursive: true);
        Directory.Delete(_b, recursive: true);
    }

    private static TaskCompletionSource<string> Heard(FileWatcher watcher)
    {
        var heard = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Changed += (_, e) => heard.TrySetResult(e.Path.Value);
        return heard;
    }

    [Fact]
    public async Task Watch_HearsAChangeInTheFolder()
    {
        using var watcher = new FileWatcher();
        var heard = Heard(watcher);

        watcher.Watch(_a);
        await watcher.Starting.WaitAsync(Patience);
        await File.WriteAllTextAsync(Path.Combine(_a, "new.txt"), "x");

        (await heard.Task.WaitAsync(Patience)).Should().Contain("new.txt");
    }

    [Fact]
    public async Task Watch_AFolderThatDoesNotAnswer_DoesNotHoldTheCaller()
    {
        using var answer = new ManualResetEventSlim();
        using var watcher = new FileWatcher(_ =>
        {
            answer.Wait(Patience);
            return false;
        });

        watcher.Watch(_a);

        watcher.Starting.IsCompleted.Should().BeFalse("the share has not answered, and the caller is the UI thread");
        answer.Set();
        await watcher.Starting.WaitAsync(Patience);
    }

    [Fact]
    public async Task Watch_OvertakenWhileItStarts_TheNewerFolderWins()
    {
        using var answer = new ManualResetEventSlim();
        using var watcher = new FileWatcher(path =>
        {
            if (path == _a)
            {
                answer.Wait(Patience);
            }

            return Directory.Exists(path);
        });
        var heard = Heard(watcher);

        watcher.Watch(_a);
        var overtaken = watcher.Starting;
        watcher.Watch(_b);
        await watcher.Starting.WaitAsync(Patience);
        answer.Set();
        await overtaken.WaitAsync(Patience);

        await File.WriteAllTextAsync(Path.Combine(_a, "left-behind.txt"), "x");
        await Task.Delay(700);   // longer than the debounce
        heard.Task.IsCompleted.Should().BeFalse("the panel has left that folder");

        await File.WriteAllTextAsync(Path.Combine(_b, "here.txt"), "x");
        (await heard.Task.WaitAsync(Patience)).Should().Contain("here.txt");
    }

    [Fact]
    public async Task Stop_WhileItStarts_LeavesNothingWatching()
    {
        using var answer = new ManualResetEventSlim();
        using var watcher = new FileWatcher(path =>
        {
            answer.Wait(Patience);
            return Directory.Exists(path);
        });
        var heard = Heard(watcher);

        watcher.Watch(_a);
        var starting = watcher.Starting;
        watcher.Stop();   // a server was mounted over the disk meanwhile
        answer.Set();
        await starting.WaitAsync(Patience);

        await File.WriteAllTextAsync(Path.Combine(_a, "new.txt"), "x");
        await Task.Delay(700);   // longer than the debounce

        heard.Task.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task Released_WithAStartLeftUnderWay_WaitsForIt()
    {
        // What closing a share waits for: Windows refuses while a handle on it may still be opened.
        using var asked = new ManualResetEventSlim();
        using var answer = new ManualResetEventSlim();
        using var watcher = new FileWatcher(path =>
        {
            asked.Set();
            answer.Wait(Patience);
            return Directory.Exists(path);
        });

        watcher.Watch(_a);
        asked.Wait(Patience).Should().BeTrue();
        watcher.Stop();

        var released = watcher.Released;
        released.IsCompleted.Should().BeFalse("the start has not answered, and it may still open a handle");

        answer.Set();
        await released.WaitAsync(Patience);
    }

    [Fact]
    public async Task Released_AfterLeavingAWatchedFolder_Completes()
    {
        using var watcher = new FileWatcher();
        watcher.Watch(_a);
        await watcher.Starting.WaitAsync(Patience);

        watcher.Stop();

        await watcher.Released.WaitAsync(Patience);
    }

    [Fact]
    public void Released_WithNothingEverWatched_IsDone()
    {
        using var watcher = new FileWatcher();

        watcher.Released.IsCompleted.Should().BeTrue();
    }
}
