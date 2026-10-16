using System.IO;
using FluentAssertions;
using TCV.App.Services;
using TCV.App.ViewModels;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Tests;

/// <summary>
/// The drop-stack count in the menu bar (6.51): it follows <see cref="DropStack.Changed"/>, which
/// had no subscriber, hides when the stack is empty, and only ever updates through the dispatcher.
/// </summary>
public class DropStackBadgeTests
{
    private static IFileEntry Entry(string path) => new StubEntry(path);

    private static readonly Action<Action> Inline = update => update();

    [Fact]
    public void EmptyStack_IsHidden()
    {
        var badge = new DropStackBadge(new DropStack(), Inline);

        badge.Count.Should().Be(0);
        badge.IsVisible.Should().BeFalse();
        badge.Text.Should().BeEmpty();
    }

    [Fact]
    public void AddingFiles_ShowsTheCount()
    {
        var stack = new DropStack();
        var badge = new DropStackBadge(stack, Inline);
        var changed = new List<string?>();
        badge.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        stack.Add([Entry(@"D:\a.txt"), Entry(@"D:\b.txt")]);

        badge.Count.Should().Be(2);
        badge.IsVisible.Should().BeTrue();
        badge.Text.Should().NotBeEmpty();
        badge.ToolTip.Should().NotBeEmpty();
        changed.Should().Contain([nameof(DropStackBadge.Count), nameof(DropStackBadge.IsVisible)]);
    }

    [Fact]
    public void ClearingTheStack_HidesItAgain()
    {
        var stack = new DropStack();
        stack.Add([Entry(@"D:\a.txt")]);
        var badge = new DropStackBadge(stack, Inline);
        badge.IsVisible.Should().BeTrue();

        stack.Clear();

        badge.IsVisible.Should().BeFalse();
        badge.Text.Should().BeEmpty();
    }

    [Fact]
    public void RemovingOne_UpdatesTheCount()
    {
        var stack = new DropStack();
        var a = Entry(@"D:\a.txt");
        stack.Add([a, Entry(@"D:\b.txt")]);
        var badge = new DropStackBadge(stack, Inline);

        stack.Remove(a);

        badge.Count.Should().Be(1);
    }

    [Fact]
    public void Changes_AreAppliedOnlyThroughThePost()
    {
        // The stack may change on any thread; the bound properties must change only where the
        // window's dispatcher runs them (TRAP-47).
        var stack = new DropStack();
        var queued = new Queue<Action>();
        var badge = new DropStackBadge(stack, queued.Enqueue);
        while (queued.Count > 0)
        {
            queued.Dequeue()();
        }

        stack.Add([Entry(@"D:\a.txt")]);
        stack.Add([Entry(@"D:\b.txt")]);

        badge.Count.Should().Be(0, "nothing has run on the dispatcher yet");
        queued.Should().HaveCount(2);

        queued.Dequeue()();

        badge.Count.Should().Be(2, "an update reads the live count when it runs, so it is never stale");
    }

    private sealed record StubEntry(string FullPath) : IFileEntry
    {
        public VfsPath Path => new(FullPath);
        public string Name => System.IO.Path.GetFileName(FullPath);
        public bool IsDirectory => false;
        public long Size => 0;
        public DateTime LastModified => new(2026, 1, 1);
        public FileAttributes Attributes => FileAttributes.Normal;
    }
}
