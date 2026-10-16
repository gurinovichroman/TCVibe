using FluentAssertions;
using TCV.Archives.Engines;

namespace TCV.Archives.Tests;

public class CabEngineTests
{
    [Fact]
    public void CabEngine_CanWrite_IsFalse()
    {
        new CabEngine().CanWrite.Should().BeFalse();
    }

    [Fact]
    public void CabEngine_Pack_ThrowsNotSupported()
    {
        var engine = new CabEngine();
        var act = () => engine.Pack("dummy.cab", Enumerable.Empty<(string, string)>());
        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void CabEngine_ListAll_NonCabFile_ReturnsEmpty()
    {
        var tempFile = System.IO.Path.GetTempFileName();
        try
        {
            // Write non-CAB content
            System.IO.File.WriteAllBytes(tempFile, [0x00, 0x01, 0x02, 0x03]);
            var engine = new CabEngine();
            var entries = engine.ListAll(tempFile);
            entries.Should().BeEmpty();
        }
        finally
        {
            System.IO.File.Delete(tempFile);
        }
    }
}
