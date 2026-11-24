using FluentAssertions;

namespace TCV.Network.Tests;

/// <summary>A yes to an unencrypted password holds for that address, in this run: never wider.</summary>
public class ClearTextConsentsTests
{
    private readonly ClearTextConsents _consents = new();
    private int _asked;

    private Task<bool> Ask(ConnectionInfo info, bool answer) => _consents.AskOnceAsync(info, () =>
    {
        _asked++;
        return Task.FromResult(answer);
    });

    private static ConnectionInfo Ftp(string host = "ftp.example.org", int port = 21, string user = "bob") => new()
    {
        Protocol = NetworkProtocol.Ftp,
        Host = host,
        Port = port,
        Username = user,
    };

    [Fact]
    public async Task AskOnceAsync_FirstConnect_Asks()
    {
        (await Ask(Ftp(), answer: true)).Should().BeTrue();

        _asked.Should().Be(1);
    }

    [Fact]
    public async Task AskOnceAsync_SameAddressAgain_DoesNotAsk()
    {
        await Ask(Ftp(), answer: true);

        (await Ask(Ftp(), answer: false)).Should().BeTrue("the user has agreed already: this is the Reconnect");

        _asked.Should().Be(1);
    }

    [Fact]
    public async Task AskOnceAsync_Declined_AsksAgainNextTime()
    {
        (await Ask(Ftp(), answer: false)).Should().BeFalse();

        (await Ask(Ftp(), answer: true)).Should().BeTrue();

        _asked.Should().Be(2);
    }

    [Fact]
    public async Task AskOnceAsync_HostInOtherCaseOrWithSpaces_IsTheSameAddress()
    {
        await Ask(Ftp("ftp.example.org"), answer: true);

        await Ask(Ftp(" FTP.Example.ORG "), answer: false);

        _asked.Should().Be(1);
    }

    [Theory]
    [InlineData("other.example.org", 21, "bob")]
    [InlineData("ftp.example.org", 2121, "bob")]
    [InlineData("ftp.example.org", 21, "alice")]
    [InlineData("ftp.example.org", 21, "BOB")]
    public async Task AskOnceAsync_AnyOtherAddress_AsksAgain(string host, int port, string user)
    {
        await Ask(Ftp(), answer: true);

        (await Ask(Ftp(host, port, user), answer: false)).Should().BeFalse();

        _asked.Should().Be(2);
    }

    [Fact]
    public async Task AskOnceAsync_OtherProtocolOnTheSameAddress_AsksAgain()
    {
        await Ask(Ftp(), answer: true);
        var dav = Ftp();
        dav.Protocol = NetworkProtocol.WebDav;

        (await Ask(dav, answer: false)).Should().BeFalse();
    }

    [Fact]
    public async Task AskOnceAsync_ANewRun_AsksAgain()
    {
        await Ask(Ftp(), answer: true);

        // Nothing is kept outside the instance, and an application run holds one.
        var asked = false;
        await new ClearTextConsents().AskOnceAsync(Ftp(), () =>
        {
            asked = true;
            return Task.FromResult(true);
        });

        asked.Should().BeTrue();
    }
}
