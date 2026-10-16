using FluentAssertions;
using TCV.Core.Domain;

namespace TCV.Core.Tests;

public sealed class AtomicFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tcv-atomic-" + Guid.NewGuid().ToString("N")[..8]);

    public AtomicFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void WriteAllText_NewFile_CreatesItWithoutLeftovers()
    {
        var path = Path.Combine(_dir, "a.json");

        AtomicFile.WriteAllText(path, "one");

        File.ReadAllText(path).Should().Be("one");
        File.Exists(path + ".tmp").Should().BeFalse();
        File.Exists(path + ".bak").Should().BeFalse();
    }

    [Fact]
    public void WriteAllText_ExistingFile_KeepsPreviousAsBak()
    {
        var path = Path.Combine(_dir, "a.json");
        AtomicFile.WriteAllText(path, "one");

        AtomicFile.WriteAllText(path, "two");

        File.ReadAllText(path).Should().Be("two");
        File.ReadAllText(path + ".bak").Should().Be("one");
    }

    [Fact]
    public void WriteAllText_MissingDirectory_IsCreated()
    {
        var path = Path.Combine(_dir, "sub", "deeper", "a.json");

        AtomicFile.WriteAllText(path, "x");

        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void WriteAllText_NoByteOrderMark()
    {
        var path = Path.Combine(_dir, "a.json");

        AtomicFile.WriteAllText(path, "x");

        File.ReadAllBytes(path).Should().Equal((byte)'x');
    }

    [Fact]
    public void ReadAllTextOrNull_MissingFile_IsNull()
    {
        AtomicFile.ReadAllTextOrNull(Path.Combine(_dir, "none.json")).Should().BeNull();
    }

    [Fact]
    public void ReadAllTextOrNull_PermanentlyLockedFile_Throws()
    {
        var path = Path.Combine(_dir, "a.json");
        File.WriteAllText(path, "x");
        using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        var act = () => AtomicFile.ReadAllTextOrNull(path, attempts: 2);

        act.Should().Throw<IOException>();
    }

    [Fact]
    public void PreserveCorrupt_CopiesTheFileAndLeavesTheOriginal()
    {
        var path = Path.Combine(_dir, "a.json");
        File.WriteAllText(path, "broken");

        var copy = AtomicFile.PreserveCorrupt(path);

        copy.Should().NotBeNull();
        File.ReadAllText(copy!).Should().Be("broken");
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void PreserveCorrupt_MissingFile_ReturnsNull()
    {
        AtomicFile.PreserveCorrupt(Path.Combine(_dir, "none.json")).Should().BeNull();
    }
}
