using System.Net;
using FluentAssertions;
using TCV.Core.Domain;
using TCV.Network;

namespace TCV.Network.Tests;

/// <summary>How <see cref="AzureBlobResponse"/> reads the service's XML, and what a refusal tells the user.</summary>
public sealed class AzureBlobResponseTests
{
    private const string Listing = """
        <?xml version="1.0" encoding="utf-8"?>
        <EnumerationResults ServiceEndpoint="https://myaccount.blob.core.windows.net/" ContainerName="c">
          <Prefix>docs/</Prefix>
          <Delimiter>/</Delimiter>
          <Blobs>
            <Blob><Name>docs/</Name><Properties><Last-Modified>Mon, 12 Oct 2026 10:00:00 GMT</Last-Modified><Content-Length>0</Content-Length></Properties><Metadata /></Blob>
            <Blob><Name>docs/a b.txt</Name><Properties><Last-Modified>Mon, 12 Oct 2026 10:00:00 GMT</Last-Modified><Content-Length>12</Content-Length></Properties><Metadata /></Blob>
            <Blob><Name>docs/hns</Name><Properties><Last-Modified>Sun, 11 Oct 2026 09:00:00 GMT</Last-Modified><Content-Length>0</Content-Length></Properties><Metadata><hdi_isfolder>true</hdi_isfolder></Metadata></Blob>
            <BlobPrefix><Name>docs/hns/</Name></BlobPrefix>
            <BlobPrefix><Name>docs/virtual/</Name></BlobPrefix>
            <Blob><Name Encoded="true">docs/odd%EF%BF%BF.txt</Name><Properties><Content-Length>3</Content-Length></Properties></Blob>
          </Blobs>
          <NextMarker>2!88!MDAwMDE2</NextMarker>
        </EnumerationResults>
        """;

    [Fact]
    public void ParseBlobList_Page_ReadsBlobsPrefixesMarkerAndFolderMetadata()
    {
        var page = AzureBlobResponse.ParseBlobList(Listing);

        page.Blobs.Select(b => b.Name).Should().Equal("docs/", "docs/a b.txt", "docs/hns", "docs/odd￿.txt");
        page.Blobs.Single(b => b.Name == "docs/a b.txt").Size.Should().Be(12);
        page.Blobs.Single(b => b.Name == "docs/hns").IsFolderMarker.Should().BeTrue();
        page.Prefixes.Should().Equal("docs/hns/", "docs/virtual/");
        page.NextMarker.Should().Be("2!88!MDAwMDE2");
    }

    [Fact]
    public void BuildEntries_HierarchicalFolderAndOwnMarker_ShowsEachFolderOnceAndNotTheFolderItself()
    {
        var page = AzureBlobResponse.ParseBlobList(Listing);

        var entries = AzureBlobResponse.BuildEntries(new VfsPath("/c/docs"), "docs/", page.Blobs, page.Prefixes);

        entries.Select(e => (e.Name, e.IsDirectory)).Should().BeEquivalentTo(new[]
        {
            ("hns", true), ("virtual", true), ("a b.txt", false), ("odd￿.txt", false),
        }, "the \"docs/\" marker is the folder itself, and \"hns\" is both a directory blob and a prefix");
        entries.Single(e => e.Name == "a b.txt").Path.Value.Should().Be("/c/docs/a b.txt");
        entries.Single(e => e.Name == "hns").LastModified.Should().Be(
            new DateTimeOffset(2026, 10, 11, 9, 0, 0, TimeSpan.Zero).LocalDateTime, "times are shown in local time");
        entries.Single(e => e.Name == "hns").Size.Should().Be(0);
    }

    [Fact]
    public void BuildEntries_EmptySegmentPrefix_IsSkipped()
    {
        var entries = AzureBlobResponse.BuildEntries(VfsPath.Root, "", [], ["a/", "/"]);

        entries.Should().ContainSingle().Which.Path.Value.Should().Be("/a");
    }

    [Fact]
    public void ParseContainerList_Page_ReadsNamesDatesAndMarker()
    {
        const string xml = """
            <EnumerationResults ServiceEndpoint="https://myaccount.blob.core.windows.net/">
              <Containers>
                <Container><Name>logs</Name><Properties><Last-Modified>Mon, 12 Oct 2026 10:00:00 GMT</Last-Modified></Properties></Container>
                <Container><Name>$web</Name><Properties /></Container>
              </Containers>
              <NextMarker />
            </EnumerationResults>
            """;

        var page = AzureBlobResponse.ParseContainerList(xml);

        page.Containers.Select(c => c.Name).Should().Equal("logs", "$web");
        page.NextMarker.Should().BeNull("an empty NextMarker ends the listing");
        AzureBlobResponse.BuildContainerEntries(page.Containers).Select(e => e.Path.Value).Should().Equal("/logs", "/$web");
    }

    [Fact]
    public void ParseBlobList_NotXml_IsAnErrorRatherThanAnEmptyFolder()
    {
        var act = () => AzureBlobResponse.ParseBlobList("<html>proxy login</htm");

        act.Should().Throw<IOException>().WithMessage("*could not be read*");
    }

    [Fact]
    public void ParseError_ServiceBody_KeepsOnlyTheFirstLineOfMessageAndDetail()
    {
        const string body = """
            <?xml version="1.0" encoding="utf-8"?><Error><Code>AuthenticationFailed</Code><Message>Server failed to authenticate the request.
            RequestId:1f0e
            Time:2026-10-13T08:00:00Z</Message><AuthenticationErrorDetail>Signature not valid in the specified time frame: Start [..] - Expiry [..]</AuthenticationErrorDetail></Error>
            """;

        var error = AzureBlobResponse.ParseError(body);

        error.Code.Should().Be("AuthenticationFailed");
        error.Message.Should().Be("Server failed to authenticate the request.");
        error.AuthenticationDetail.Should().StartWith("Signature not valid in the specified time frame");
    }

    [Theory]
    [InlineData("Signature not valid in the specified time frame: Start [x] - Expiry [y]", true, "*SAS token has expired*")]
    [InlineData("Request date header too old: 'Tue, 13 Oct 2026 07:00:00 GMT'", false, "*clock*")]
    [InlineData("The MAC signature found in the HTTP request '…' is not the same as any computed signature.", false, "*account key does not match*")]
    [InlineData("Signature did not match. String to sign used was r", true, "*SAS token's signature*")]
    [InlineData(null, false, "*account key*clock*")]
    public void Describe_AuthenticationFailed_SaysWhatToCheck(string? detail, bool sas, string expected)
    {
        var message = AzureStorageException.Describe(HttpStatusCode.Forbidden, "AuthenticationFailed", null, detail, null, sas);

        message.Should().Match(expected).And.EndWith("(AuthenticationFailed, HTTP 403)");
    }

    [Theory]
    [InlineData("AuthorizationPermissionMismatch", "*permissions*")]
    [InlineData("AuthorizationResourceTypeMismatch", "*enter the container in the connection*")]
    [InlineData("ContainerNotFound", "*container does not exist*")]
    [InlineData("BlobNotFound", "*\"a.txt\" does not exist*")]
    public void Describe_KnownCode_NamesTheFix(string code, string expected) =>
        AzureStorageException.Describe(HttpStatusCode.NotFound, code, "server words", null, "a.txt", usesSasToken: true)
            .Should().Match(expected);

    [Theory]
    [InlineData(true, "*SAS token may have expired*firewall*")]
    [InlineData(false, "*Check the account key*firewall*")]
    public void Describe_AuthorizationFailure_NamesTheCredentialInUseAndTheFirewall(bool sas, string expected) =>
        AzureStorageException.Describe(HttpStatusCode.Forbidden, "AuthorizationFailure", null, null, null, sas)
            .Should().Match(expected);

    [Fact]
    public void Describe_UnknownCode_FallsBackToTheServersOwnWords()
    {
        var message = AzureStorageException.Describe(
            HttpStatusCode.BadRequest, "InvalidHeaderValue", "The API version 2023-11-03 is not supported by Azurite.", null, null, false);

        message.Should().Be(
            "Azure Blob Storage refused the request: The API version 2023-11-03 is not supported by Azurite. (InvalidHeaderValue, HTTP 400)");
    }
}
