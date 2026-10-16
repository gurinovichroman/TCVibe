using System.Diagnostics;
using FluentAssertions;
using TCV.Ai.Mcp;

namespace TCV.Ai.Tests;

/// <summary>
/// The MCP server's security boundary. The interesting test is
/// <see cref="Check_JunctionLeavingTheRoot_IsRefused"/>: a prefix-only scope check passes it happily,
/// and that is the EscapeRoute vulnerability class this code exists to avoid.
/// </summary>
public sealed class PathScopeTests : IDisposable
{
    private readonly string _temp;
    private readonly string _approved;
    private readonly string _secret;

    public PathScopeTests()
    {
        // The real path, not the %TEMP% spelling: on Windows the temp directory itself often sits
        // under a short (8.3) path, and comparing a resolved path against an unresolved root would
        // fail for reasons that have nothing to do with what is being tested.
        _temp = Path.Combine(Path.GetTempPath(), "tcv-scope-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(_temp);
        _temp = new DirectoryInfo(_temp).FullName;

        _approved = Path.Combine(_temp, "approved");
        _secret = Path.Combine(_temp, "secret");
        Directory.CreateDirectory(_approved);
        Directory.CreateDirectory(_secret);
        File.WriteAllText(Path.Combine(_approved, "ok.txt"), "fine");
        File.WriteAllText(Path.Combine(_secret, "passwords.txt"), "hunter2");
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); }
        catch { /* a leftover temp directory is not worth failing a test run over */ }
    }

    [Fact]
    public void Check_NoRootsApproved_RefusesEverything()
    {
        var scope = new PathScope([]);

        scope.IsEmpty.Should().BeTrue();
        var result = scope.Check(Path.Combine(_approved, "ok.txt"));

        result.IsAllowed.Should().BeFalse();
        result.Verdict.Should().Be(ScopeVerdict.NoScope);
    }

    [Fact]
    public void Check_FileInsideApprovedRoot_IsAllowed()
    {
        var scope = new PathScope([_approved]);

        var result = scope.Check(Path.Combine(_approved, "ok.txt"));

        result.IsAllowed.Should().BeTrue();
        result.ResolvedPath.Should().Be(Path.Combine(_approved, "ok.txt"));
    }

    [Fact]
    public void Check_ApprovedRootItself_IsAllowed() =>
        new PathScope([_approved]).Check(_approved).IsAllowed.Should().BeTrue();

    [Fact]
    public void Check_PathOutsideEveryRoot_IsRefused()
    {
        var result = new PathScope([_approved]).Check(Path.Combine(_secret, "passwords.txt"));

        result.IsAllowed.Should().BeFalse();
        result.Verdict.Should().Be(ScopeVerdict.OutsideScope);
    }

    [Fact]
    public void Check_DotDotEscape_IsRefused()
    {
        // GetFullPath normalises this away, so the resolved path lands outside the root.
        var sneaky = Path.Combine(_approved, "..", "secret", "passwords.txt");

        new PathScope([_approved]).Check(sneaky).IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void Check_SiblingRootWithSharedPrefix_IsRefused()
    {
        var sibling = _approved + "-other";
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "x.txt"), "x");

        new PathScope([_approved]).Check(Path.Combine(sibling, "x.txt")).IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void Check_FileThatDoesNotExistYet_IsAllowedInsideTheRoot()
    {
        // A write has to be able to name a file that is not there yet.
        var target = Path.Combine(_approved, "new", "deeper", "file.txt");

        new PathScope([_approved]).Check(target).IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void Check_EmptyPath_IsInvalid() =>
        new PathScope([_approved]).Check("  ").Verdict.Should().Be(ScopeVerdict.Invalid);

    [Fact]
    public void Check_JunctionLeavingTheRoot_IsRefused()
    {
        // The CVE-class case: a link *inside* the approved root pointing outside it. Every segment is
        // resolved before containment is tested, so this has to come back refused — a naive
        // "does the string start with the root?" check would allow it.
        var link = Path.Combine(_approved, "escape");
        if (!TryCreateJunction(link, _secret)) return;   // no privilege on this machine

        var result = new PathScope([_approved]).Check(Path.Combine(link, "passwords.txt"));

        result.IsAllowed.Should().BeFalse();
        result.Verdict.Should().Be(ScopeVerdict.SymlinkEscape);
    }

    [Fact]
    public void Check_LinkWhoseTargetRunsThroughAnotherEscapingLink_IsRefused()
    {
        // approved\b  ->  secret              (an escaping link, refused on its own)
        // approved\a  ->  approved\b\sub      (its target *looks* inside the root, but runs through b)
        // A resolver that only followed the chain at the last segment would yield approved\b\sub —
        // lexically inside — while the disk read lands in secret\sub. Current .NET/NTFS already refuse
        // this on their own; the test pins it so a change in either cannot reopen the hole, and
        // PathScope walks the target again from its root regardless.
        Directory.CreateDirectory(Path.Combine(_secret, "sub"));
        File.WriteAllText(Path.Combine(_secret, "sub", "loot.txt"), "loot");

        var b = Path.Combine(_approved, "b");
        if (!TryCreateJunction(b, _secret)) return;

        var a = Path.Combine(_approved, "a");
        if (!TryCreateJunction(a, Path.Combine(b, "sub"))) return;

        var result = new PathScope([_approved]).Check(Path.Combine(a, "loot.txt"));

        result.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void Check_LinkCycle_IsRefusedRatherThanHanging()
    {
        var one = Path.Combine(_approved, "one");
        Directory.CreateDirectory(one);
        if (!TryCreateJunction(Path.Combine(one, "loop"), _approved)) return;

        // approved\one\loop -> approved, so approved\one\loop\one\loop\... never ends lexically.
        var deep = Path.Combine(one, "loop", "one", "loop", "one", "loop", "ok.txt");

        var act = () => new PathScope([_approved]).Check(deep);

        act.Should().NotThrow();
    }

    [Fact]
    public void Check_JunctionStayingInsideTheRoot_IsAllowed()
    {
        var inner = Path.Combine(_approved, "real");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, "fine.txt"), "fine");

        var link = Path.Combine(_approved, "alias");
        if (!TryCreateJunction(link, inner)) return;

        new PathScope([_approved]).Check(Path.Combine(link, "fine.txt")).IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void Constructor_ResolvesRootsSoALinkedRootStillMatches()
    {
        var linkedRoot = Path.Combine(_temp, "linked-root");
        if (!TryCreateJunction(linkedRoot, _approved)) return;

        // Approving the link approves what it points at, and a request naming the real path matches.
        var scope = new PathScope([linkedRoot]);

        scope.Roots.Should().ContainSingle().Which.Should().Be(_approved);
        scope.Check(Path.Combine(_approved, "ok.txt")).IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void Constructor_DeduplicatesRootsCaseInsensitively()
    {
        var scope = new PathScope([_approved, _approved.ToUpperInvariant(), _approved + "\\"]);

        scope.Roots.Should().HaveCount(1);
    }

    [Theory]
    [InlineData(@"C:\", @"C:\")]
    [InlineData("C:", @"C:\")]
    [InlineData(@" D:\work\repo\ ", @"D:\work\repo")]
    [InlineData(@"\\server\share\", @"\\server\share")]
    public void NormalizeConfiguredRoot_KeepsTheSeparatorOnlyOnADriveRoot(string input, string expected) =>
        PathScope.NormalizeConfiguredRoot(input).Should().Be(expected);

    [Fact]
    public void Constructor_DriveRootStoredWithoutSeparator_ApprovesTheWholeDrive()
    {
        // Older builds trimmed «C:\» to «C:», which Path.GetFullPath reads as the current directory
        // on that drive — the scope silently became some other folder.
        var drive = Path.GetPathRoot(_temp)!.TrimEnd('\\');

        var scope = new PathScope([drive]);

        scope.Roots.Should().ContainSingle().Which.Should().Be(drive + "\\");
        scope.Check(Path.Combine(_approved, "ok.txt")).IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void NamesSameRoot_ResolvedFormOfAJunctionRoot_MatchesTheStoredLink()
    {
        // The dashboard lists roots resolved; the setting holds what was picked. Revoke has to match.
        var linkedRoot = Path.Combine(_temp, "linked-root");
        if (!TryCreateJunction(linkedRoot, _approved)) return;

        var shown = new PathScope([linkedRoot]).Roots.Single();

        PathScope.NamesSameRoot(linkedRoot + "\\", shown).Should().BeTrue();
        PathScope.NamesSameRoot(_secret, shown).Should().BeFalse();
    }

    [Fact]
    public void TryResolveFinal_PlainPath_NormalisesIt()
    {
        PathScope.TryResolveFinal(Path.Combine(_approved, ".", "ok.txt"), out var resolved)
            .Should().BeTrue();

        resolved.Should().Be(Path.Combine(_approved, "ok.txt"));
    }

    /// <summary>
    /// Creates a directory junction. Junctions rather than symlinks on purpose: a symlink needs
    /// SeCreateSymbolicLinkPrivilege (admin, or Developer Mode) while a junction does not, so the test
    /// runs on an ordinary developer machine. Returns false when even that is unavailable, and the
    /// caller then skips rather than failing for a reason unrelated to the code under test.
    /// </summary>
    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null) return false;
            process.WaitForExit(10_000);

            return process.ExitCode == 0 && Directory.Exists(link);
        }
        catch
        {
            return false;
        }
    }
}
