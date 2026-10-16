using System.IO;
using FluentAssertions;
using TCV.App.Services;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Tests;

/// <summary>Covers the drop stack added in task 6.24.</summary>
public class DropStackTests
{
    private static IFileEntry Entry(string path) => new StubEntry(path);

    [Fact]
    public void Add_PutsEntriesOnTheStackInOrder()
    {
        var stack = new DropStack();

        stack.Add([Entry(@"D:\a.txt"), Entry(@"D:\b.txt")]).Should().Be(2);
        stack.Entries.Select(e => e.Name).Should().BeEquivalentTo(
            ["a.txt", "b.txt"], o => o.WithStrictOrdering());
    }

    [Fact]
    public void Add_SkipsDuplicatesAndReportsHowManyWereActuallyAdded()
    {
        var stack = new DropStack();
        stack.Add([Entry(@"D:\a.txt")]);

        stack.Add([Entry(@"D:\a.txt"), Entry(@"D:\b.txt")]).Should().Be(1);
        stack.Count.Should().Be(2);
    }

    [Fact]
    public void Add_ComparesPathsCaseInsensitively()
    {
        var stack = new DropStack();
        stack.Add([Entry(@"D:\A.txt")]);

        stack.Add([Entry(@"d:\a.txt")]).Should().Be(0);
    }

    [Fact]
    public void Remove_DropsOneEntry()
    {
        var stack = new DropStack();
        stack.Add([Entry(@"D:\a.txt"), Entry(@"D:\b.txt")]);

        stack.Remove(Entry(@"D:\a.txt")).Should().BeTrue();
        stack.Count.Should().Be(1);
        stack.Remove(Entry(@"D:\missing.txt")).Should().BeFalse();
    }

    [Fact]
    public void Clear_EmptiesTheStack()
    {
        var stack = new DropStack();
        stack.Add([Entry(@"D:\a.txt")]);

        stack.Clear();

        stack.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Changed_FiresOnlyWhenSomethingActuallyChanged()
    {
        var stack = new DropStack();
        var fired = 0;
        stack.Changed += (_, _) => fired++;

        stack.Add([Entry(@"D:\a.txt")]);   // 1
        stack.Add([Entry(@"D:\a.txt")]);   // duplicate — no change
        stack.Remove(Entry(@"D:\nope"));   // absent — no change
        stack.Clear();                     // 2
        stack.Clear();                     // already empty — no change

        fired.Should().Be(2);
    }

    [Fact]
    public void Snapshot_IsDetachedFromTheLiveStack()
    {
        var stack = new DropStack();
        stack.Add([Entry(@"D:\a.txt")]);

        var snapshot = stack.Snapshot();
        stack.Clear();

        // An operation started from the stack must survive the stack being emptied under it.
        snapshot.Should().ContainSingle();
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
