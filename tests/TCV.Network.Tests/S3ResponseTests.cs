using System.Net;
using FluentAssertions;

namespace TCV.Network.Tests;

/// <summary><see cref="S3Response"/> (the XML), <see cref="S3Exception.Describe"/> (the words) and <see cref="S3Presets"/>.</summary>
public sealed class S3ResponseTests
{
    [Fact]
    public void ParseListObjects_UrlEncodedListing_DecodesPlusAsSpaceAndPercentTwoBAsPlus()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
              <Name>bucket</Name><Prefix>my+dir%2F</Prefix><EncodingType>url</EncodingType>
              <IsTruncated>true</IsTruncated><NextContinuationToken>1ueGcxLPRx1Tr/XYExHnhbYLgveDs2J/wm36Hy4vbOwM=</NextContinuationToken>
              <Contents><Key>my+dir%2Fa%2Bb+%C3%BC.txt</Key><LastModified>2009-10-12T17:50:30.000Z</LastModified>
                <ETag>"fba9dede5f27731c9771645a39863328"</ETag><Size>434234</Size></Contents>
              <CommonPrefixes><Prefix>my+dir%2Fsub%2F</Prefix></CommonPrefixes>
            </ListBucketResult>
            """;

        var page = S3Response.ParseListObjects(xml);

        var item = page.Objects.Should().ContainSingle().Subject;
        item.Key.Should().Be("my dir/a+b ü.txt");
        item.Size.Should().Be(434234);
        item.LastModified.Should().Be(new DateTime(2009, 10, 12, 17, 50, 30, DateTimeKind.Utc).ToLocalTime());
        item.LastModified.Kind.Should().Be(DateTimeKind.Local);
        page.Prefixes.Should().Equal("my dir/sub/");
        page.IsTruncated.Should().BeTrue();
        page.NextContinuationToken.Should().Be("1ueGcxLPRx1Tr/XYExHnhbYLgveDs2J/wm36Hy4vbOwM=");
    }

    [Fact]
    public void ParseListObjects_StoreThatIgnoredEncodingType_TakesKeysAsTheyAre()
    {
        const string xml = "<ListBucketResult><IsTruncated>false</IsTruncated>" +
                           "<Contents><Key>a+b%20c.txt</Key><Size>1</Size></Contents></ListBucketResult>";

        S3Response.ParseListObjects(xml).Objects.Single().Key.Should().Be("a+b%20c.txt");
    }

    [Fact]
    public void ParseListObjects_NotXml_SaysTheEndpointIsWrong()
    {
        var act = () => S3Response.ParseListObjects("<html><body>Welcome to nginx!");

        act.Should().Throw<IOException>().WithMessage("*did not answer like an S3 endpoint*");
    }

    [Fact]
    public void ParseBuckets_List_ReturnsNamesDatesAndTheNextToken()
    {
        const string xml = """
            <ListAllMyBucketsResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
              <Owner><ID>x</ID></Owner>
              <Buckets>
                <Bucket><Name>alpha</Name><CreationDate>2020-01-01T00:00:00.000Z</CreationDate></Bucket>
                <Bucket><Name>beta</Name><CreationDate>2021-06-01T12:00:00.000Z</CreationDate></Bucket>
              </Buckets>
              <ContinuationToken>next</ContinuationToken>
            </ListAllMyBucketsResult>
            """;

        var (buckets, token) = S3Response.ParseBuckets(xml);

        buckets.Select(b => b.Name).Should().Equal("alpha", "beta");
        buckets[0].Created.Should().Be(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToLocalTime());
        token.Should().Be("next");
    }

    [Fact]
    public void ParseError_ErrorDocument_ReturnsCodeMessageAndRegion()
    {
        var error = S3Response.ParseError(
            "<Error><Code>AuthorizationHeaderMalformed</Code><Message>the region 'us-east-1' is wrong; expecting 'eu-west-1'</Message>" +
            "<Region>eu-west-1</Region><RequestId>1</RequestId></Error>");

        error.Should().Be(new S3Error("AuthorizationHeaderMalformed", "the region 'us-east-1' is wrong; expecting 'eu-west-1'", "eu-west-1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<CopyObjectResult><ETag>x</ETag></CopyObjectResult>")]
    [InlineData("Service Unavailable")]
    public void ParseError_AnythingButAnErrorDocument_IsNull(string? body)
    {
        S3Response.ParseError(body).Should().BeNull();
    }

    [Fact]
    public void ParseDeleteResult_QuietAnswerWithErrors_ListsTheKeysThatStayed()
    {
        const string xml = "<DeleteResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">" +
                           "<Error><Key>locked.txt</Key><Code>AccessDenied</Code><Message>Access Denied</Message></Error></DeleteResult>";

        S3Response.ParseDeleteResult(xml).Should().Equal(new S3DeleteFailure("locked.txt", "AccessDenied", "Access Denied"));
        S3Response.ParseDeleteResult("<DeleteResult/>").Should().BeEmpty();
    }

    [Fact]
    public void BuildDeleteBody_KeysWithXmlCharacters_AreEscaped()
    {
        var body = S3Response.BuildDeleteBody(["a&b<c>.txt", "plain"]);

        body.Should().Be(
            "<Delete xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Quiet>true</Quiet>" +
            "<Object><Key>a&amp;b&lt;c&gt;.txt</Key></Object><Object><Key>plain</Key></Object></Delete>");
    }

    [Fact]
    public void BuildCompleteBody_Parts_ListsNumbersAndETagsInOrder()
    {
        S3Response.BuildCompleteBody([(1, "\"a\""), (2, "\"b\"")]).Should().Be(
            "<CompleteMultipartUpload xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">" +
            "<Part><PartNumber>1</PartNumber><ETag>\"a\"</ETag></Part><Part><PartNumber>2</PartNumber><ETag>\"b\"</ETag></Part>" +
            "</CompleteMultipartUpload>");
    }

    // ── Messages ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "AccessDenied", "Access denied (AccessDenied): check the access key and its permissions.")]
    [InlineData(HttpStatusCode.Forbidden, "SignatureDoesNotMatch", "*secret key, the region and the addressing style*")]
    [InlineData(HttpStatusCode.Forbidden, "InvalidAccessKeyId", "*access key ID*")]
    [InlineData(HttpStatusCode.Forbidden, "RequestTimeTooSkewed", "*system time*")]
    [InlineData(HttpStatusCode.NotFound, "NoSuchBucket", "The bucket \"photos\" does not exist (NoSuchBucket)*")]
    [InlineData(HttpStatusCode.Conflict, "BucketNotEmpty", "The bucket you tried to delete is not empty (BucketNotEmpty).")]
    public void Describe_ErrorCode_TellsWhatToCheck(HttpStatusCode status, string code, string expected)
    {
        var message = code == "BucketNotEmpty" ? "The bucket you tried to delete is not empty" : "server text";

        S3Exception.Describe(status, new S3Error(code, message, null), "photos", null, anonymous: false)
            .Should().Match(expected);
    }

    [Fact]
    public void Describe_HeadRefusedWithoutBody_StillNamesTheLikelyCauses()
    {
        S3Exception.Describe(HttpStatusCode.Forbidden, null, "b", "k", anonymous: false)
            .Should().Be("Access denied (403): check the access key, the secret key and the permissions.");
        S3Exception.Describe(HttpStatusCode.Forbidden, null, "b", "k", anonymous: true)
            .Should().Contain("no access key");
    }

    [Fact]
    public void Describe_WrongRegionWithTheRightOneNamed_SaysWhichToSet()
    {
        S3Exception.Describe(HttpStatusCode.MovedPermanently, new S3Error("PermanentRedirect", "x", "eu-west-1"), "b", null, false)
            .Should().Be("The bucket \"b\" is in region eu-west-1 (PermanentRedirect): set this region in the connection.");
    }

    // ── Presets ──────────────────────────────────────────────────────────────

    [Fact]
    public void Presets_All_HaveUniqueIdsAndEndWithOther()
    {
        S3Presets.All.Select(p => p.Id).Should().OnlyHaveUniqueItems();
        S3Presets.All[^1].Id.Should().Be(S3Presets.OtherId);
        S3Presets.Find("R2")!.Name.Should().Be("Cloudflare R2");
        S3Presets.Find("unknown").Should().BeNull();
    }

    [Theory]
    [InlineData("aws", "eu-central-1", null, "s3.eu-central-1.amazonaws.com")]
    [InlineData("aws", "", null, "s3.us-east-1.amazonaws.com")]
    [InlineData("r2", "auto", " 0123abcd ", "0123abcd.r2.cloudflarestorage.com")]
    [InlineData("b2", "eu-central-003", null, "s3.eu-central-003.backblazeb2.com")]
    [InlineData("do", "fra1", null, "fra1.digitaloceanspaces.com")]
    [InlineData("hetzner", "hel1", null, "hel1.your-objectstorage.com")]
    [InlineData("gcs", "auto", null, "storage.googleapis.com")]
    public void BuildHost_RegionAndAccount_FillTheTemplate(string id, string region, string? account, string expected)
    {
        S3Presets.Find(id)!.BuildHost(region, account).Should().Be(expected);
    }

    [Fact]
    public void Presets_MinIo_IsLocalPathStyleHttpOnPort9000()
    {
        var minio = S3Presets.Find("minio")!;

        (minio.PathStyle, minio.UseHttp, minio.Port, minio.BuildHost(string.Empty, null))
            .Should().Be((true, true, 9000, "localhost"));
        S3Presets.Find("r2")!.NeedsAccount.Should().BeTrue();
        S3Presets.Find("aws")!.RegionInHost.Should().BeTrue();
    }
}
