using FluentAssertions;
using TCV.Archives;

namespace TCV.Archives.Tests;

/// <summary>Stage 4 extensions to the archive detector (ISO, CAB, SFX).</summary>
public class ArchiveDetectorStage4Tests
{
    [Theory]
    [InlineData("image.iso", ArchiveDetector.ArchiveFormat.Iso)]
    [InlineData("disk.img", ArchiveDetector.ArchiveFormat.Iso)]
    [InlineData("IMAGE.ISO", ArchiveDetector.ArchiveFormat.Iso)]
    public void DetectByExtension_IsoExtension_ReturnsIso(
        string filename, ArchiveDetector.ArchiveFormat expected)
    {
        ArchiveDetector.DetectByExtension(filename).Should().Be(expected);
    }

    [Theory]
    [InlineData("installer.cab", ArchiveDetector.ArchiveFormat.Cab)]
    [InlineData("update.CAB", ArchiveDetector.ArchiveFormat.Cab)]
    public void DetectByExtension_CabExtension_ReturnsCab(
        string filename, ArchiveDetector.ArchiveFormat expected)
    {
        ArchiveDetector.DetectByExtension(filename).Should().Be(expected);
    }

    [Fact]
    public void DetectFormat_CabMagicBytes_ReturnsCab()
    {
        // MSCF header bytes
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.cab");
        try
        {
            // Write MSCF magic + enough bytes for parsing
            var header = new byte[64];
            header[0] = 0x4D; // M
            header[1] = 0x53; // S
            header[2] = 0x43; // C
            header[3] = 0x46; // F
            File.WriteAllBytes(tempFile, header);

            ArchiveDetector.DetectFormat(tempFile).Should().Be(ArchiveDetector.ArchiveFormat.Cab);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void CreateEngine_IsoFormat_ReturnsIsoEngine()
    {
        // Just check we can create the engine for .iso extension
        var engine = ArchiveDetector.CreateEngine("dummy.iso");
        engine.Should().BeOfType<TCV.Archives.Engines.IsoEngine>();
    }

    [Fact]
    public void CreateEngine_CabFormat_ReturnsCabEngine()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.cab");
        try
        {
            var header = new byte[64];
            header[0] = 0x4D; header[1] = 0x53; header[2] = 0x43; header[3] = 0x46;
            File.WriteAllBytes(tempFile, header);

            var engine = ArchiveDetector.CreateEngine(tempFile);
            engine.Should().BeOfType<TCV.Archives.Engines.CabEngine>();
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
