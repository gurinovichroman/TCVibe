using FluentAssertions;
using TCV.Network;

namespace TCV.Network.Tests;

/// <summary>
/// Resume must never splice two versions of a file together, and FTP times must mean the same moment
/// as the local disk's.
/// </summary>
public class ResumeAndTimeTests
{
    [Fact]
    public void ReconcileSource_SourceUnchanged_KeepsTheProgress()
    {
        var item = new TransferItem();
        var modified = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        TransferQueue.ReconcileSource(item, 1000, modified);
        item.BytesTransferred = 400;

        TransferQueue.ReconcileSource(item, 1000, modified);

        item.BytesTransferred.Should().Be(400);
    }

    [Fact]
    public void ReconcileSource_SourceReplacedMeanwhile_StartsOver()
    {
        var item = new TransferItem();
        TransferQueue.ReconcileSource(item, 1000, new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc));
        item.BytesTransferred = 400;

        // Same size, new build: only the time gives it away.
        TransferQueue.ReconcileSource(item, 1000, new DateTime(2026, 9, 1, 11, 0, 0, DateTimeKind.Utc));

        item.BytesTransferred.Should().Be(0, "continuing would append the new file's tail to the old file's head");
    }

    [Fact]
    public void ReconcileSource_SameMomentInLocalAndUtc_IsTheSameSource()
    {
        var item = new TransferItem();
        var local = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Local);
        TransferQueue.ReconcileSource(item, 1000, local);
        item.BytesTransferred = 400;

        TransferQueue.ReconcileSource(item, 1000, local.ToUniversalTime());

        item.BytesTransferred.Should().Be(400);
    }

    [Fact]
    public void ToLocal_UtcFromAMachineListing_BecomesLocalTimeOfTheSameMoment()
    {
        var utc = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);

        var shown = FtpFileSystem.ToLocal(utc);

        shown.Kind.Should().Be(DateTimeKind.Local);
        shown.ToUniversalTime().Should().Be(utc);
    }

    [Fact]
    public void ToLocal_TimeWithoutZone_IsLeftAsTheServerSentIt()
    {
        var unspecified = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Unspecified);

        FtpFileSystem.ToLocal(unspecified).Should().Be(unspecified);
    }
}
