using System.Net;
using FluentAssertions;
using TCV.Core.Domain;
using TCV.Network;

namespace TCV.Network.Tests;

/// <summary>
/// Covers the request URIs <see cref="WebDavFileSystem"/> builds (6.23). The transport is a stub,
/// so these are about what goes on the wire, not about any server's behaviour.
/// </summary>
public class WebDavFileSystemTests
{
    private const string EmptyListing =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <D:multistatus xmlns:D="DAV:"></D:multistatus>
        """;

    /// <summary>Answers every request with the same body and records where it was aimed.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent(EmptyListing),
            });
        }
    }

    private static (WebDavFileSystem Fs, RecordingHandler Handler) Create()
    {
        var handler = new RecordingHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://dav.example:8080") };
        return (new WebDavFileSystem(client), handler);
    }

    [Fact]
    public async Task ListAsync_Root_RequestsASingleSlash()
    {
        var (fs, handler) = Create();

        // "//" would be read as the start of an authority, and building the absolute URI throws
        // before the request goes out — a connection mounted at the root could not be listed.
        await fs.ListAsync(new VfsPath("/"));

        handler.Requests.Should().ContainSingle()
            .Which.AbsolutePath.Should().Be("/");
    }

    [Fact]
    public async Task ListAsync_Subdirectory_AsksForTheCollectionWithATrailingSlash()
    {
        var (fs, handler) = Create();

        await fs.ListAsync(new VfsPath("/project/src"));

        handler.Requests.Single().AbsolutePath.Should().Be("/project/src/");
    }

    [Fact]
    public async Task CreateDirectoryAsync_Root_DoesNotDoubleTheSlash()
    {
        var (fs, handler) = Create();

        await fs.CreateDirectoryAsync(new VfsPath("/"), CancellationToken.None);

        handler.Requests.Single().AbsolutePath.Should().Be("/");
    }
}
