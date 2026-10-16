using System.Text.RegularExpressions;
using FluentAssertions;
using TCV.Network;

namespace TCV.Network.Tests;

/// <summary>
/// What the address book does when its file is damaged, locked, or written by another Windows
/// user: it must never quietly replace good data with an empty list.
/// </summary>
public sealed class ConnectionBookSafetyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tcv-booksafe-" + Guid.NewGuid().ToString("N"));

    public ConnectionBookSafetyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
    }

    private string BookPath => Path.Combine(_dir, "connections.json");

    private static ConnectionInfo Sample(string name = "box") => new()
    {
        Name = name,
        Protocol = NetworkProtocol.Ftp,
        Host = "ftp.example.com",
        Port = 21,
        Username = "u",
        Password = "s3cret",
    };

    [Fact]
    public void Load_DamagedFile_IsKeptAsACopyAndTheProblemIsReported()
    {
        File.WriteAllText(BookPath, "[ this is not json");

        var book = new ConnectionBook(BookPath);

        book.Connections.Should().BeEmpty();
        book.LoadError.Should().Contain("damaged");
        Directory.GetFiles(_dir, "connections.json.corrupt-*").Should().ContainSingle()
            .Which.Should().Match(p => File.ReadAllText(p) == "[ this is not json");
    }

    [Fact]
    public void Add_AfterADamagedLoad_WritesAValidFileAndTheOriginalSurvivesAsACopy()
    {
        File.WriteAllText(BookPath, "garbage");
        var book = new ConnectionBook(BookPath);

        book.Add(Sample());

        new ConnectionBook(BookPath).Connections.Should().ContainSingle();
        Directory.GetFiles(_dir, "*.corrupt-*").Should().ContainSingle();
    }

    [Fact]
    public void Add_WhileTheFileIsLockedByAnotherProcess_DoesNotOverwriteIt()
    {
        new ConnectionBook(BookPath).Add(Sample("precious"));
        using var lockHandle = new FileStream(BookPath, FileMode.Open, FileAccess.Read, FileShare.None);

        var book = new ConnectionBook(BookPath);
        var add = () => book.Add(Sample("intruder"));

        book.LoadError.Should().Contain("could not be read");
        add.Should().Throw<IOException>();
        lockHandle.Dispose();
        new ConnectionBook(BookPath).Connections.Should().ContainSingle().Which.Name.Should().Be("precious");
    }

    [Fact]
    public void Connections_PasswordFromAnotherUser_IsFlaggedNotSilentlyEmpty()
    {
        new ConnectionBook(BookPath).Add(Sample());
        // A blob DPAPI cannot open: what a copied portable folder looks like on another account.
        var text = File.ReadAllText(BookPath);
        File.WriteAllText(BookPath, Regex.Replace(text, "\"EncryptedPassword\": \"[^\"]+\"", "\"EncryptedPassword\": \"AAAAAAAA\""));

        var connection = new ConnectionBook(BookPath).Connections.Single();

        connection.Password.Should().BeNull();
        connection.PasswordUnreadable.Should().BeTrue();
    }

    [Fact]
    public void Update_UnreadablePasswordLeftBlank_KeepsTheStoredSecret()
    {
        new ConnectionBook(BookPath).Add(Sample());
        var text = File.ReadAllText(BookPath);
        File.WriteAllText(BookPath, Regex.Replace(text, "\"EncryptedPassword\": \"[^\"]+\"", "\"EncryptedPassword\": \"AAAAAAAA\""));
        var book = new ConnectionBook(BookPath);
        var info = book.Connections.Single();

        info.Name = "renamed";
        book.Update(info);

        File.ReadAllText(BookPath).Should().Contain("AAAAAAAA", "the stored blob is not thrown away until a new password replaces it");
        new ConnectionBook(BookPath).Connections.Single().Name.Should().Be("renamed");
    }

    [Fact]
    public void Update_TypingANewPassword_ReplacesTheUnreadableOne()
    {
        new ConnectionBook(BookPath).Add(Sample());
        var text = File.ReadAllText(BookPath);
        File.WriteAllText(BookPath, Regex.Replace(text, "\"EncryptedPassword\": \"[^\"]+\"", "\"EncryptedPassword\": \"AAAAAAAA\""));
        var book = new ConnectionBook(BookPath);
        var info = book.Connections.Single();

        info.Password = "fresh";
        info.PasswordUnreadable = false;
        book.Update(info);

        var reloaded = new ConnectionBook(BookPath).Connections.Single();
        reloaded.Password.Should().Be("fresh");
        reloaded.PasswordUnreadable.Should().BeFalse();
    }

    [Fact]
    public void Save_KeepsThePreviousVersionAsBak()
    {
        var book = new ConnectionBook(BookPath);
        book.Add(Sample("first"));
        book.Add(Sample("second"));

        File.Exists(BookPath + ".bak").Should().BeTrue();
        File.ReadAllText(BookPath + ".bak").Should().Contain("first").And.NotContain("second");
    }

    [Fact]
    public void Connections_OldFileWithStoredFtpsOnPort990_IsRecognisedAsImplicit()
    {
        var info = Sample();
        info.Protocol = NetworkProtocol.Ftps;
        info.Port = 990;
        new ConnectionBook(BookPath).Add(info);

        var loaded = new ConnectionBook(BookPath).Connections.Single();

        loaded.IsImplicitFtps.Should().BeTrue();
    }

    [Theory]
    [InlineData(NetworkProtocol.Ftp, true)]
    [InlineData(NetworkProtocol.WebDav, true)]
    [InlineData(NetworkProtocol.Ftps, false)]
    [InlineData(NetworkProtocol.Sftp, false)]
    [InlineData(NetworkProtocol.WebDavs, false)]
    public void SendsCredentialsInClear_OnlyForPlainProtocols(NetworkProtocol protocol, bool expected)
    {
        var info = Sample();
        info.Protocol = protocol;

        info.SendsCredentialsInClear.Should().Be(expected);
    }
}
