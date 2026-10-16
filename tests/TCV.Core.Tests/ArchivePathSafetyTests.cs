using FluentAssertions;
using TCV.Core.PluginSdk;

namespace TCV.Core.Tests;

public class ArchivePathSafetyTests
{
    private static readonly string Dest = Path.Combine(Path.GetTempPath(), "tcv_extract_root");

    [Theory]
    [InlineData("file.txt")]
    [InlineData("folder/file.txt")]
    [InlineData(@"folder\file.txt")]
    [InlineData("/leading/slash.txt")]
    [InlineData("folder/../file.txt")]   // stays inside once resolved
    public void ResolveDestination_RelativeEntry_StaysUnderDestination(string entryName)
    {
        var resolved = ArchivePathSafety.ResolveDestination(Dest, entryName);

        resolved.Should().StartWith(Path.GetFullPath(Dest) + Path.DirectorySeparatorChar);
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("folder/../../evil.txt")]
    [InlineData(@"..\..\evil.txt")]
    [InlineData(@"C:\Windows\evil.txt")]
    [InlineData("C:/Windows/evil.txt")]
    [InlineData("")]
    [InlineData("/")]
    public void ResolveDestination_EscapingEntry_Throws(string entryName)
    {
        var act = () => ArchivePathSafety.ResolveDestination(Dest, entryName);

        act.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("logs/12:00.log", "logs", "12_00.log")]
    [InlineData("file.txt:stream", "file.txt_stream")]
    [InlineData("what?.txt", "what_.txt")]
    [InlineData("trailing. ", "trailing__")]
    [InlineData("dir/CON.txt", "dir", "_CON.txt")]
    [InlineData("./a/./b.txt", "a", "b.txt")]
    public void ResolveDestination_NameWindowsCannotHold_IsSanitizedPerSegment(string entryName, params string[] expected)
    {
        var resolved = ArchivePathSafety.ResolveDestination(Dest, entryName);

        resolved.Should().Be(Path.Combine([Path.GetFullPath(Dest), .. expected]));
    }

    [Fact]
    public void ResolveDestination_SiblingDirectoryWithSharedPrefix_Throws()
    {
        // "…/tcv_extract_root_evil" starts with the destination string but is not inside it
        var act = () => ArchivePathSafety.ResolveDestination(Dest, "../tcv_extract_root_evil/x.txt");

        act.Should().Throw<InvalidDataException>();
    }
}
