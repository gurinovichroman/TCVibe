using System.IO;
using FluentAssertions;
using TCV.App.Services;
using TCV.Core.Domain;
using TCV.Core.Interfaces;
using TCV.Network;

namespace TCV.App.Tests;

/// <summary>«Copy link» for files in S3 or Azure Blob (6.81): the lifetime asked for and the text copied.</summary>
public class ReadLinksTests
{
    [Theory]
    [InlineData("1", 1)]
    [InlineData("24", 24)]
    [InlineData("168", 168)]
    public void Lifetime_OfferedHours_AreTaken(string hours, int expected)
    {
        ReadLinks.Lifetime(hours).Should().Be(TimeSpan.FromHours(expected));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1000")]   // S3 signs a week at most
    [InlineData("-1")]
    [InlineData("day")]
    public void Lifetime_NothingOrSomethingNotOffered_IsTheShortest(string? hours)
    {
        // A link anyone can open: the palette, which names no lifetime, gets the least exposure.
        ReadLinks.Lifetime(hours).Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public void Lifetimes_AreWithinWhatS3Signs()
    {
        ReadLinks.Lifetimes.Should().OnlyContain(l => l.Hours >= 1 && l.Hours <= 7 * 24);
        ReadLinks.Lifetimes.Select(l => l.Hours).Should().BeInAscendingOrder();
    }

    [Fact]
    public void Text_IsALinkPerFile_LeavingFoldersOut()
    {
        var source = new FakeSource();

        var text = ReadLinks.Text(
            source, [new Entry("/b/a.txt", false), new Entry("/b/dir", true), new Entry("/b/c d.bin", false)], TimeSpan.FromDays(1));

        text.Split(Environment.NewLine).Should().Equal(
            "https://example.org/b/a.txt?ttl=86400",
            "https://example.org/b/c%20d.bin?ttl=86400");
        source.Asked.Should().Equal("/b/a.txt", "/b/c d.bin");
    }

    [Fact]
    public void SourceOf_ConnectionOpenedReadOnly_IsTheStoreUnderIt()
    {
        // The read-only wrapper hid the store: no «Copy link» in the menu, and the command said the
        // file was not in S3. A link only reads, so the wrapper must not stand in its way.
        using var s3 = new S3FileSystem(new ConnectionInfo
        {
            Protocol = NetworkProtocol.S3,
            Host = "s3.us-east-1.amazonaws.com",
            Bucket = "bucket",
            Region = "us-east-1",
            Username = "AKIDEXAMPLE",
            Password = "secret",
        });

        ReadLinks.SourceOf(s3).Should().BeSameAs(s3);
        ReadLinks.SourceOf(new ReadOnlyFileSystem(s3)).Should().BeSameAs(s3);
    }

    [Fact]
    public void SourceOf_StoreThatSignsNoLinks_IsNone()
    {
        var disk = new InMemoryFileSystem();

        ReadLinks.SourceOf(disk).Should().BeNull();
        ReadLinks.SourceOf(new ReadOnlyFileSystem(disk)).Should().BeNull();
    }

    private sealed class FakeSource : IReadLinkSource
    {
        public List<string> Asked { get; } = [];

        public bool CanCreateReadLink => true;

        public Uri CreateReadLink(VfsPath path, TimeSpan lifetime)
        {
            Asked.Add(path.Value);
            return new Uri($"https://example.org{path.Value}?ttl={lifetime.TotalSeconds}");
        }
    }

    private sealed record Entry(string FullPath, bool IsDirectory) : IFileEntry
    {
        public VfsPath Path => new(FullPath);
        public string Name => System.IO.Path.GetFileName(FullPath);
        public long Size => 0;
        public DateTime LastModified => new(2026, 1, 1);
        public FileAttributes Attributes => IsDirectory ? FileAttributes.Directory : FileAttributes.Normal;
    }
}
