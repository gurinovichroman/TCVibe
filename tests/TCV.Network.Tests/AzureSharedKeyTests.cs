using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using TCV.Network;

namespace TCV.Network.Tests;

/// <summary>
/// The exact strings <see cref="AzureSharedKey"/> signs. A wrong newline only ever shows up as
/// "403 AuthenticationFailed", so each layout is pinned here; the vectors marked "docs" are copied
/// from "Authorize with Shared Key" on learn.microsoft.com, the HMAC values were computed apart
/// from this code (Python's hmac module).
/// </summary>
public sealed class AzureSharedKeyTests
{
    /// <summary>Azurite's published development key; any base64 key would do.</summary>
    internal const string Key = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private const string Date = "Tue, 13 Oct 2026 08:00:00 GMT";

    /// <summary>The verb and the eleven standard header lines, all empty.</summary>
    private static string Head(string verb) => verb + new string('\n', 12);

    private static HttpRequestMessage Request(HttpMethod method, string uri, string version = AzureSharedKey.ApiVersion)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("x-ms-date", Date);
        request.Headers.TryAddWithoutValidation("x-ms-version", version);
        return request;
    }

    [Fact]
    public void StringToSign_DocsGetContainerMetadata_MatchesTheDocumentedString()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "http://myaccount.blob.core.windows.net/mycontainer?restype=container&comp=metadata&timeout=20");
        request.Headers.TryAddWithoutValidation("x-ms-date", "Fri, 26 Jun 2015 23:39:12 GMT");
        request.Headers.TryAddWithoutValidation("x-ms-version", "2015-02-21");

        AzureSharedKey.StringToSign(request, "myaccount").Should().Be(
            "GET\n\n\n\n\n\n\n\n\n\n\n\nx-ms-date:Fri, 26 Jun 2015 23:39:12 GMT\nx-ms-version:2015-02-21\n" +
            "/myaccount/mycontainer\ncomp:metadata\nrestype:container\ntimeout:20");
    }

    [Fact]
    public void StringToSign_DocsZeroLengthPut_SignsTheLengthAsEmpty()
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "http://myaccount/mycontainer?restype=container&timeout=30")
        {
            Content = new ByteArrayContent([]),
        };
        request.Headers.TryAddWithoutValidation("x-ms-version", "2015-02-21");
        request.Headers.TryAddWithoutValidation("x-ms-date", "Fri, 26 Jun 2015 23:39:12 GMT");

        AzureSharedKey.StringToSign(request, "myaccount").Should().Be(
            "PUT\n\n\n\n\n\n\n\n\n\n\n\nx-ms-date:Fri, 26 Jun 2015 23:39:12 GMT\nx-ms-version:2015-02-21\n" +
            "/myaccount/mycontainer\nrestype:container\ntimeout:30",
            "since 2015-02-21 a zero Content-Length is signed as an empty string");
    }

    [Fact]
    public void CanonicalizedResource_DocsRepeatedParameter_JoinsItsSortedValues()
    {
        var uri = new Uri("http://myaccount.blob.core.windows.net/mycontainer?restype=container&comp=list" +
                          "&include=snapshots&include=metadata&include=uncommittedblobs");

        AzureSharedKey.CanonicalizedResource(uri, "myaccount").Should().Be(
            "/myaccount/mycontainer\ncomp:list\ninclude:metadata,snapshots,uncommittedblobs\nrestype:container");
    }

    [Fact]
    public void StringToSign_ListBlobsWithEncodedPrefix_DecodesQueryValuesAndSortsNames()
    {
        using var request = Request(HttpMethod.Get,
            "https://myaccount.blob.core.windows.net/mycontainer?restype=container&comp=list" +
            "&prefix=my%20dir%2F&delimiter=%2F&include=metadata&maxresults=5000");

        AzureSharedKey.StringToSign(request, "myaccount").Should().Be(
            Head("GET") +
            "x-ms-date:" + Date + "\n" +
            "x-ms-version:2023-11-03\n" +
            "/myaccount/mycontainer\ncomp:list\ndelimiter:/\ninclude:metadata\nmaxresults:5000\nprefix:my dir/\nrestype:container");
    }

    [Fact]
    public void ComputeSignature_ListBlobsString_MatchesAnIndependentHmac()
    {
        var stringToSign = Head("GET") + "x-ms-date:" + Date + "\nx-ms-version:2023-11-03\n" +
                           "/myaccount/mycontainer\ncomp:list\ndelimiter:/\ninclude:metadata\nmaxresults:5000\nprefix:my dir/\nrestype:container";

        AzureSharedKey.ComputeSignature(stringToSign, Convert.FromBase64String(Key))
            .Should().Be("aqhjgTVp6nGuZJzMi7OsXxmnlsELD95EsA70J3xAW0w=");
    }

    [Fact]
    public void StringToSign_PutBlobWithHeaders_SignsLengthTypeConditionAndLowerCasedMetadata()
    {
        using var request = Request(HttpMethod.Put,
            "https://myaccount.blob.core.windows.net/mycontainer/my%20dir/r%C3%A9sum%C3%A9%2B1.txt");
        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes("hello world"));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        request.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
        request.Headers.TryAddWithoutValidation("x-ms-meta-Owner", "me");

        AzureSharedKey.StringToSign(request, "myaccount").Should().Be(
            "PUT\n" +
            "\n" +             // Content-Encoding
            "\n" +             // Content-Language
            "11\n" +           // Content-Length
            "\n" +             // Content-MD5
            "text/plain\n" +   // Content-Type
            "\n" +             // Date (x-ms-date is used)
            "\n" +             // If-Modified-Since
            "\n" +             // If-Match
            "*\n" +            // If-None-Match
            "\n" +             // If-Unmodified-Since
            "\n" +             // Range
            "x-ms-blob-type:BlockBlob\n" +
            "x-ms-date:" + Date + "\n" +
            "x-ms-meta-owner:me\n" +
            "x-ms-version:2023-11-03\n" +
            "/myaccount/mycontainer/my%20dir/r%C3%A9sum%C3%A9%2B1.txt",
            "the path is signed exactly as encoded in the URI");
    }

    [Fact]
    public void StringToSign_RangeReadWithETag_SignsRangeAndIfMatch()
    {
        using var request = Request(HttpMethod.Get, "https://myaccount.blob.core.windows.net/c/big.bin");
        request.Headers.Range = new RangeHeaderValue(100, null);
        request.Headers.TryAddWithoutValidation("If-Match", "\"0x8DCE0\"");

        AzureSharedKey.StringToSign(request, "myaccount").Should().Be(
            "GET\n\n\n\n\n\n\n\n" +   // verb, then Content-* (5), Date, If-Modified-Since
            "\"0x8DCE0\"\n" +          // If-Match
            "\n\n" +                    // If-None-Match, If-Unmodified-Since
            "bytes=100-\n" +            // Range
            "x-ms-date:" + Date + "\nx-ms-version:2023-11-03\n" +
            "/myaccount/c/big.bin");
    }

    [Fact]
    public void StringToSign_EmulatorPathStyle_NamesTheAccountTwice()
    {
        using var request = Request(HttpMethod.Get,
            "http://127.0.0.1:10000/devstoreaccount1/photos?restype=container&comp=list");

        AzureSharedKey.StringToSign(request, "devstoreaccount1").Should().EndWith(
            "\n/devstoreaccount1/devstoreaccount1/photos\ncomp:list\nrestype:container",
            "the docs: against the emulator the account name appears twice in the resource");
    }

    [Fact]
    public void Authorize_Request_SetsSharedKeyHeaderOverTheRequestsOwnString()
    {
        using var request = Request(HttpMethod.Get,
            "https://myaccount.blob.core.windows.net/mycontainer?restype=container&comp=list" +
            "&prefix=my%20dir%2F&delimiter=%2F&include=metadata&maxresults=5000");

        AzureSharedKey.Authorize(request, "myaccount", Convert.FromBase64String(Key));

        request.Headers.Authorization!.Scheme.Should().Be("SharedKey");
        request.Headers.Authorization.Parameter.Should().Be("myaccount:aqhjgTVp6nGuZJzMi7OsXxmnlsELD95EsA70J3xAW0w=");
    }

    [Fact]
    public void BlobReadSasStringToSign_Version20201206_HasSixteenFieldsInTheDocumentedOrder()
    {
        var text = AzureSharedKey.BlobReadSasStringToSign(
            "/blob/myaccount/photos/2026/beach day.jpg", "2026-10-14T08:00:00Z", "https", "2023-11-03");

        text.Should().Be("r\n\n2026-10-14T08:00:00Z\n/blob/myaccount/photos/2026/beach day.jpg\n\n\nhttps\n2023-11-03\nb\n\n\n\n\n\n\n");
        text.Split('\n').Should().HaveCount(16);
    }

    [Fact]
    public void BlobReadSasQuery_KnownKey_CarriesTheIndependentSignatureEscaped()
    {
        var query = AzureSharedKey.BlobReadSasQuery("myaccount", Convert.FromBase64String(Key),
            "photos", "2026/beach day.jpg", new DateTime(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc), httpsOnly: true);

        query.Should().Be("sv=2023-11-03&spr=https&se=2026-10-14T08%3A00%3A00Z&sr=b&sp=r&sig=" +
                          Uri.EscapeDataString("FcB02nIFCK6jkqXwwnvhwFw+tUlKehqYLCqZKl+hlp4="));
    }
}
