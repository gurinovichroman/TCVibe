using FluentAssertions;
using TCV.Network;

namespace TCV.Network.Tests;

/// <summary>Covers the WebDAV PROPFIND parser added in task 6.23.</summary>
public class WebDavResponseTests
{
    private const string Listing =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <D:multistatus xmlns:D="DAV:">
          <D:response>
            <D:href>/dav/project/</D:href>
            <D:propstat><D:prop>
              <D:resourcetype><D:collection/></D:resourcetype>
              <D:getlastmodified>Mon, 27 Jul 2026 10:00:00 GMT</D:getlastmodified>
            </D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat>
          </D:response>
          <D:response>
            <D:href>/dav/project/src/</D:href>
            <D:propstat><D:prop>
              <D:resourcetype><D:collection/></D:resourcetype>
              <D:getlastmodified>Mon, 27 Jul 2026 10:05:00 GMT</D:getlastmodified>
            </D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat>
          </D:response>
          <D:response>
            <D:href>/dav/project/read%20me.md</D:href>
            <D:propstat><D:prop>
              <D:resourcetype/>
              <D:getcontentlength>1234</D:getcontentlength>
              <D:getlastmodified>Mon, 27 Jul 2026 11:00:00 GMT</D:getlastmodified>
            </D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat>
          </D:response>
        </D:multistatus>
        """;

    [Fact]
    public void ParseMultiStatus_ReadsFilesAndCollections()
    {
        var resources = WebDavResponse.ParseMultiStatus(Listing, "/dav/project");

        resources.Should().HaveCount(2);
        resources.Should().Contain(r => r.Name == "src" && r.IsDirectory);
        resources.Should().Contain(r => r.Name == "read me.md" && !r.IsDirectory && r.Size == 1234);
    }

    [Fact]
    public void ParseMultiStatus_DropsTheRequestedCollectionItself()
    {
        // Every server returns the collection alongside its children; keeping it would make each
        // directory appear to contain itself.
        WebDavResponse.ParseMultiStatus(Listing, "/dav/project")
            .Should().NotContain(r => r.Name == "project");
    }

    [Fact]
    public void ParseMultiStatus_TrailingSlashOnTheBasePath_StillMatches()
    {
        WebDavResponse.ParseMultiStatus(Listing, "/dav/project/").Should().HaveCount(2);
    }

    [Fact]
    public void ParseMultiStatus_DecodesPercentEncodedNames()
    {
        WebDavResponse.ParseMultiStatus(Listing, "/dav/project")
            .Single(r => !r.IsDirectory).Href.Should().Be("/dav/project/read me.md");
    }

    [Fact]
    public void ParseMultiStatus_CollectionHasNoSize()
    {
        WebDavResponse.ParseMultiStatus(Listing, "/dav/project")
            .Single(r => r.IsDirectory).Size.Should().Be(0);
    }

    [Fact]
    public void ParseMultiStatus_TrailingSlashAloneMarksACollection()
    {
        // Some servers omit <resourcetype> entirely and rely on the href's trailing slash.
        const string xml =
            """
            <D:multistatus xmlns:D="DAV:">
              <D:response><D:href>/base/child/</D:href></D:response>
            </D:multistatus>
            """;

        WebDavResponse.ParseMultiStatus(xml, "/base").Single().IsDirectory.Should().BeTrue();
    }

    [Fact]
    public void ParseMultiStatus_AbsoluteHref_IsReducedToItsPath()
    {
        const string xml =
            """
            <D:multistatus xmlns:D="DAV:">
              <D:response><D:href>http://host:8080/base/file.txt</D:href></D:response>
            </D:multistatus>
            """;

        WebDavResponse.ParseMultiStatus(xml, "/base").Single().Href.Should().Be("/base/file.txt");
    }

    [Fact]
    public void ParseMultiStatus_MalformedXml_IsEmptyRatherThanThrowing()
    {
        var act = () => WebDavResponse.ParseMultiStatus("<not xml", "/base");

        act.Should().NotThrow();
        WebDavResponse.ParseMultiStatus("<not xml", "/base").Should().BeEmpty();
    }

    [Fact]
    public void ParseMultiStatus_ResponseWithoutHref_IsSkipped()
    {
        const string xml =
            """
            <D:multistatus xmlns:D="DAV:">
              <D:response><D:propstat><D:prop/></D:propstat></D:response>
            </D:multistatus>
            """;

        WebDavResponse.ParseMultiStatus(xml, "/base").Should().BeEmpty();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/a/b/", "/a/b")]
    [InlineData("a/b", "/a/b")]
    [InlineData("", "/")]
    [InlineData("/", "/")]
    public void NormalizePath_HasOneLeadingAndNoTrailingSlash(string input, string expected) =>
        WebDavResponse.NormalizePath(input).Should().Be(expected);

    [Theory]
    [InlineData("/a/b/c.txt", "c.txt")]
    [InlineData("/a/b/", "b")]
    [InlineData("solo", "solo")]
    public void LastSegment_IsTheName(string path, string expected) =>
        WebDavResponse.LastSegment(path).Should().Be(expected);

    [Fact]
    public void ParseDate_ReadsRfc1123()
    {
        WebDavResponse.ParseDate("Mon, 27 Jul 2026 10:00:00 GMT")
            .ToUniversalTime().Should().Be(new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void ParseDate_ReadsIso8601()
    {
        WebDavResponse.ParseDate("2026-07-27T10:00:00Z")
            .ToUniversalTime().Should().Be(new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a date")]
    public void ParseDate_Unparseable_IsMinValue(string? text) =>
        WebDavResponse.ParseDate(text).Should().Be(DateTime.MinValue);
}
