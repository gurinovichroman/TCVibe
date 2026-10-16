using FluentAssertions;

namespace TCV.Network.Tests;

/// <summary>
/// Reading <c>~/.ssh/config</c> for the import (6.82): concrete hosts only, every matching block in
/// file order with the first value winning — as <c>ssh alias</c> resolves it — and includes.
/// </summary>
public class SshConfigParserTests
{
    private static SshConfigHost Host(SshConfigParseResult result, string alias) =>
        result.Hosts.Single(h => h.Alias == alias);

    [Fact]
    public void Parse_AHostBlock_ReadsItsSettings()
    {
        var result = SshConfigParser.Parse("""
            Host web
                HostName web1.example.com
                User deploy
                Port 2222
                IdentityFile ~/.ssh/id_web
            """);

        var web = Host(result, "web");
        web.HostName.Should().Be("web1.example.com");
        web.User.Should().Be("deploy");
        web.Port.Should().Be(2222);
        web.IdentityFiles.Should().Equal("~/.ssh/id_web");
        web.ProxyJump.Should().BeNull();
        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Parse_WildcardHosts_AreNotImportedButTheirSettingsApply()
    {
        var result = SshConfigParser.Parse("""
            Host *.internal db?
                User ops
            Host db1.internal
                Port 2200
            Host *
                ServerAliveInterval 60
            """);

        result.Hosts.Select(h => h.Alias).Should().Equal("db1.internal");
        Host(result, "db1.internal").User.Should().Be("ops");
    }

    [Fact]
    public void Parse_TheFirstValueWins_AsSshResolvesIt()
    {
        var result = SshConfigParser.Parse("""
            Host *
                User everyone
            Host early
                User early-user
            Host late
                HostName late.example
            Host late
                HostName ignored.example
            """);

        Host(result, "early").User.Should().Be("everyone", "a Host * block above a host wins over it, as in ssh");
        Host(result, "late").HostName.Should().Be("late.example");
    }

    [Fact]
    public void Parse_GlobalSectionBeforeTheFirstHost_AppliesToAll()
    {
        var result = SshConfigParser.Parse("""
            User global-user
            Port 2022

            Host a
                Port 22
            """);

        Host(result, "a").User.Should().Be("global-user");
        Host(result, "a").Port.Should().Be(2022);
    }

    [Fact]
    public void Parse_ANegatedPattern_KeepsABlockFromAHost()
    {
        var result = SshConfigParser.Parse("""
            Host bastion app
                HostName %h.example.com
            Host * !bastion
                ProxyJump bastion
            """);

        Host(result, "bastion").ProxyJump.Should().BeNull();
        Host(result, "app").ProxyJump.Should().Be("bastion");
        Host(result, "app").HostName.Should().Be("app.example.com", "%h is the alias");
    }

    [Fact]
    public void Parse_EqualsQuotesCommentsAndCase_AreRead()
    {
        var result = SshConfigParser.Parse("""
            # a comment
            HOST "my box"
              hostname=box.example   # where it lives
              USER = "admin user"
              IdentityFile "C:\Keys\my key"
            """);

        var box = Host(result, "my box");
        box.HostName.Should().Be("box.example");
        box.User.Should().Be("admin user");
        box.IdentityFiles.Should().Equal(@"C:\Keys\my key");
    }

    [Fact]
    public void Parse_EveryIdentityFile_IsKeptInOrder()
    {
        var result = SshConfigParser.Parse("""
            Host k
                IdentityFile ~/.ssh/first
            Host *
                IdentityFile ~/.ssh/second
            """);

        Host(result, "k").IdentityFiles.Should().Equal("~/.ssh/first", "~/.ssh/second");
    }

    [Fact]
    public void Parse_AnAliasNamedTwice_IsOneHost()
    {
        var result = SshConfigParser.Parse("""
            Host a b
                User x
            Host A
                Port 2
            """);

        result.Hosts.Select(h => h.Alias).Should().Equal("a", "b");
    }

    [Fact]
    public void Parse_ProxyCommand_IsReported()
    {
        var result = SshConfigParser.Parse("""
            Host p
                ProxyCommand ssh -W %h:%p gate
            Host q
                ProxyJump none
            """);

        Host(result, "p").HasProxyCommand.Should().BeTrue();
        Host(result, "q").ProxyJump.Should().BeNull("\"none\" switches a jump host off");
    }

    [Fact]
    public void Parse_MatchAll_AppliesButOtherMatchIsSkippedWithAWarning()
    {
        var result = SshConfigParser.Parse("""
            Host a
            Match host a exec "true"
                User from-match
            Match all
                Port 2201
            """);

        Host(result, "a").User.Should().BeNull();
        Host(result, "a").Port.Should().Be(2201);
        result.Warnings.Should().ContainSingle(w => w.Kind == SshConfigWarningKind.MatchSkipped);
    }

    [Fact]
    public void Parse_Include_ReadsTheFileInTheBlockThatIncludesIt()
    {
        var files = new Dictionary<string, string>
        {
            ["conf.d/*"] = """
                User included-user
                Host extra
                    HostName extra.example
                """,
        };

        var result = SshConfigParser.Parse("""
            Host main
                Include conf.d/*
                Port 2022
            """, arg => files.TryGetValue(arg, out var text) ? [new SshConfigInclude(arg, text)] : []);

        result.Hosts.Select(h => h.Alias).Should().Equal("main", "extra");
        Host(result, "main").User.Should().Be("included-user", "lines before the included file's first Host belong to the block that included it");
        Host(result, "main").Port.Should().Be(2022, "after the include, the including block goes on");
        Host(result, "extra").Port.Should().BeNull();
        Host(result, "extra").HostName.Should().Be("extra.example");
    }

    [Fact]
    public void Parse_AMissingInclude_IsAWarning()
    {
        var result = SshConfigParser.Parse("Include nowhere\nHost a\n", _ => []);

        result.Warnings.Should().ContainSingle(w => w.Kind == SshConfigWarningKind.IncludeMissing && w.Detail == "nowhere");
        result.Hosts.Should().ContainSingle();
    }

    [Fact]
    public void Parse_AnIncludeOfItself_StopsAtTheDepthLimit()
    {
        const string text = "Include self\nHost a\n";

        var result = SshConfigParser.Parse(text, arg => [new SshConfigInclude(arg, text)]);

        result.Hosts.Select(h => h.Alias).Should().Equal("a");
        result.Warnings.Should().Contain(w => w.Kind == SshConfigWarningKind.IncludeTooDeep);
    }

    [Theory]
    [InlineData("*", "anything", true)]
    [InlineData("*.example.com", "web.EXAMPLE.com", true)]
    [InlineData("db?", "db1", true)]
    [InlineData("db?", "db12", false)]
    [InlineData("web", "web2", false)]
    [InlineData("a*b*c", "aXXbYYc", true)]
    [InlineData("a*b*c", "aXXbYY", false)]
    public void GlobMatch_MatchesLikeSsh(string pattern, string host, bool expected) =>
        SshConfigParser.GlobMatch(pattern, host).Should().Be(expected);

    [Fact]
    public void SplitLine_CommentsAndBlankLines_AreNothing()
    {
        SshConfigParser.SplitLine("   # comment").Should().BeNull();
        SshConfigParser.SplitLine("   ").Should().BeNull();
        SshConfigParser.SplitLine("Port=22")!.Value.Arguments.Should().Equal("22");
    }
}

/// <summary>Reading the configuration and its includes from disk — a folder of the test's own, never the real <c>~/.ssh</c>.</summary>
public sealed class SshConfigFileTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "tcv-sshcfg-" + Guid.NewGuid().ToString("N"));

    public SshConfigFileTests() => Directory.CreateDirectory(Path.Combine(_home, ".ssh", "conf.d"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private string Ssh(string name) => Path.Combine(_home, ".ssh", name);

    [Fact]
    public void Read_NoFile_IsNull() =>
        SshConfigFile.Read(SshConfigFile.DefaultPath(_home), _home).Should().BeNull();

    [Fact]
    public void Read_FollowsRelativeAndHomeIncludesInNameOrder()
    {
        File.WriteAllText(Ssh("config"), "Include conf.d/*.conf\nInclude ~/.ssh/extra\n");
        File.WriteAllText(Path.Combine(_home, ".ssh", "conf.d", "b.conf"), "Host b\n");
        File.WriteAllText(Path.Combine(_home, ".ssh", "conf.d", "a.conf"), "Host a\n");
        File.WriteAllText(Path.Combine(_home, ".ssh", "conf.d", "a.conf~"), "Host backup\n");
        File.WriteAllText(Ssh("extra"), "Host c\n");

        var result = SshConfigFile.Read(SshConfigFile.DefaultPath(_home), _home);

        result!.Hosts.Select(h => h.Alias).Should().Equal("a", "b", "c");
        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void ResolveInclude_AWildcardFolder_IsRefused()
    {
        var act = () => SshConfigFile.ResolveInclude("conf.*/x", _home, Path.Combine(_home, ".ssh"));

        act.Should().Throw<NotSupportedException>();
    }
}
