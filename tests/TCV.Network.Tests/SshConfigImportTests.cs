using FluentAssertions;

namespace TCV.Network.Tests;

/// <summary>Hosts of <c>~/.ssh/config</c> as SFTP connections (6.82): gaps filled as ssh fills them, duplicates flagged.</summary>
public class SshConfigImportTests
{
    private const string Home = @"C:\Users\me";

    private static SshConfigHost Host(
        string alias, string? hostName = null, string? user = "deploy", int? port = null,
        IReadOnlyList<string>? keys = null, string? proxyJump = null) =>
        new(alias, hostName, user, port, keys ?? [], proxyJump, false);

    private static IReadOnlyList<SshImportCandidate> Plan(
        IEnumerable<SshConfigHost> hosts, IEnumerable<ConnectionInfo>? existing = null, params string[] files) =>
        SshConfigImport.Plan(hosts, existing ?? [], Home, "me", path => files.Contains(path, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void Plan_AHost_BecomesAnSftpConnectionNamedByItsAlias()
    {
        var plan = Plan([Host("web", "web1.example.com", "deploy", 2222, ["~/.ssh/id_web"])], null, @"C:\Users\me\.ssh\id_web");

        var c = plan.Single().Connection;
        c.Name.Should().Be("web");
        c.Protocol.Should().Be(NetworkProtocol.Sftp);
        c.Host.Should().Be("web1.example.com");
        c.Port.Should().Be(2222);
        c.Username.Should().Be("deploy");
        c.AuthMethod.Should().Be(SshAuthMethod.KeyFile);
        c.KeyFilePath.Should().Be(@"C:\Users\me\.ssh\id_web");
        plan.Single().Notes.Should().BeEmpty();
        plan.Single().IsDuplicate.Should().BeFalse();
    }

    [Fact]
    public void Plan_NoHostNameUserOrPort_FillsThemAsSshWould()
    {
        var plan = Plan([Host("box", user: null)]);

        var c = plan.Single().Connection;
        c.Host.Should().Be("box");
        c.Port.Should().Be(22);
        c.Username.Should().Be("me");
        plan.Single().Notes.Should().Contain(SshImportNote.LocalUser);
    }

    [Fact]
    public void Plan_NoIdentityFile_TakesTheFirstDefaultKeyThere()
    {
        var plan = Plan([Host("box")], null, @"C:\Users\me\.ssh\id_ed25519");

        plan.Single().Connection.KeyFilePath.Should().Be(@"C:\Users\me\.ssh\id_ed25519");
        plan.Single().Notes.Should().Contain(SshImportNote.DefaultKey);
    }

    [Fact]
    public void Plan_NoKeyAtAll_AsksForAPassword()
    {
        var plan = Plan([Host("box")]);

        plan.Single().Connection.AuthMethod.Should().Be(SshAuthMethod.Password);
        plan.Single().Notes.Should().Contain(SshImportNote.NoKey);
    }

    [Fact]
    public void Plan_AMissingKeyFile_IsKeptAndFlagged()
    {
        var plan = Plan([Host("box", keys: ["~/.ssh/gone", "~/.ssh/also-gone"])]);

        plan.Single().Connection.KeyFilePath.Should().Be(@"C:\Users\me\.ssh\gone");
        plan.Single().Notes.Should().Contain(SshImportNote.KeyFileMissing);
    }

    [Fact]
    public void Plan_TheFirstExistingKey_IsTheOneUsed()
    {
        var plan = Plan([Host("box", keys: ["~/.ssh/gone", "%d/.ssh/id_%h"])], null, @"C:\Users\me\.ssh\id_box");

        plan.Single().Connection.KeyFilePath.Should().Be(@"C:\Users\me\.ssh\id_box");
    }

    [Fact]
    public void Plan_AJumpHost_IsFlaggedAsNotSupported()
    {
        var plan = Plan([Host("inner", proxyJump: "bastion")]);

        plan.Single().Notes.Should().Contain(SshImportNote.ProxyIgnored);
    }

    [Fact]
    public void Plan_AnAlreadySavedLogin_IsADuplicate()
    {
        var saved = new ConnectionInfo { Name = "Prod", Protocol = NetworkProtocol.Sftp, Host = "WEB1.example.com", Port = 22, Username = "deploy" };

        var plan = Plan([Host("web", "web1.example.com"), Host("web-alt", "web1.example.com", port: 2222)], [saved]);

        plan[0].DuplicateOf.Should().Be("Prod");
        plan[1].IsDuplicate.Should().BeFalse("another port is another login");
    }

    [Fact]
    public void Plan_TwoAliasesOfOneLogin_FlagTheSecond()
    {
        var plan = Plan([Host("web", "web1.example.com"), Host("web1.example.com")]);

        plan[0].IsDuplicate.Should().BeFalse();
        plan[1].DuplicateOf.Should().Be("web");
    }

    [Fact]
    public void Plan_AnFtpConnectionToTheSameHost_IsNoDuplicate()
    {
        var ftp = new ConnectionInfo { Name = "ftp", Protocol = NetworkProtocol.Ftp, Host = "web1.example.com", Port = 22, Username = "deploy" };

        Plan([Host("web", "web1.example.com")], [ftp]).Single().IsDuplicate.Should().BeFalse();
    }

    [Theory]
    [InlineData("~/.ssh/id_rsa", @"C:\Users\me\.ssh\id_rsa")]
    [InlineData("%d/keys/%r@%h-%p", @"C:\Users\me\keys\deploy@web1-2222")]
    [InlineData("C:/keys/%u/%n", @"C:\keys\me\web")]
    [InlineData("100%%/key", @"100%\key")]
    public void ExpandPath_ReplacesTheTokens(string path, string expected) =>
        SshConfigImport.ExpandPath(path, Home, "me", "web1", "deploy", 2222, "web").Should().Be(expected);
}
