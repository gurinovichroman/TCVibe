using FluentAssertions;
using TCV.Network;

namespace TCV.Network.Tests;

/// <summary>
/// Covers the portable address book (task 6.27): the file lives where it is told to live, secrets
/// survive a round trip, and a book pointed at an explicit file never reaches for the old
/// <c>%AppData%</c> one.
/// </summary>
public class ConnectionBookTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "tcv-book-" + Guid.NewGuid().ToString("N"));

    private string BookPath => Path.Combine(_dir, "connections.json");

    public ConnectionBookTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static ConnectionInfo Sample() => new()
    {
        Name = "build box",
        Protocol = NetworkProtocol.Sftp,
        Host = "build.example.com",
        Port = 22,
        Username = "ci",
        Password = "s3cret",
        RemotePath = "/srv/app",
    };

    [Fact]
    public void Add_writes_to_the_file_it_was_given()
    {
        var book = new ConnectionBook(BookPath);
        book.FilePath.Should().Be(BookPath);

        book.Add(Sample());

        File.Exists(BookPath).Should().BeTrue("the book must live next to its exe, not in %AppData%");
    }

    [Fact]
    public void Default_book_lives_next_to_the_exe()
    {
        // Not written to — constructing only reads. INV-03 is about the location, and that is the
        // one thing the parameterless constructor decides.
        var expected = Path.Combine(AppContext.BaseDirectory, "connections.json");
        new ConnectionBook().FilePath.Should().Be(expected);
    }

    [Fact]
    public void Password_survives_a_reload_and_is_not_stored_in_the_clear()
    {
        new ConnectionBook(BookPath).Add(Sample());

        File.ReadAllText(BookPath).Should().NotContain("s3cret");

        var reloaded = new ConnectionBook(BookPath).Connections.Should().ContainSingle().Subject;
        reloaded.Host.Should().Be("build.example.com");
        reloaded.Password.Should().Be("s3cret");
    }

    [Fact]
    public void Update_and_remove_persist()
    {
        var book = new ConnectionBook(BookPath);
        var info = Sample();
        book.Add(info);

        info.Name = "renamed";
        book.Update(info);
        new ConnectionBook(BookPath).Connections.Single().Name.Should().Be("renamed");

        book.Remove(info.Id);
        new ConnectionBook(BookPath).Connections.Should().BeEmpty();
    }

    [Fact]
    public void The_legacy_book_is_carried_over_to_the_portable_file()
    {
        var legacy = Path.Combine(_dir, "legacy.json");
        new ConnectionBook(legacy).Add(Sample());

        var book = new ConnectionBook(BookPath, legacy);

        book.Connections.Should().ContainSingle().Which.Host.Should().Be("build.example.com");
        File.Exists(BookPath).Should().BeTrue("the portable copy becomes authoritative right away");
    }

    [Fact]
    public void A_migration_that_cannot_write_still_keeps_the_connections_it_read()
    {
        // Read-only exe directory, a stick mounted read-only, a second instance holding the file:
        // the write fails, and losing the address book over it would be far worse than deferring
        // the migration. Simulated by a target whose parent directory cannot be created — the
        // "directory" is an existing file.
        var legacy = Path.Combine(_dir, "legacy.json");
        new ConnectionBook(legacy).Add(Sample());

        var blocker = Path.Combine(_dir, "blocked");
        File.WriteAllText(blocker, "not a directory");
        var unwritable = Path.Combine(blocker, "connections.json");

        var book = new ConnectionBook(unwritable, legacy);

        book.Connections.Should().ContainSingle().Which.Host.Should().Be("build.example.com");
        File.Exists(legacy).Should().BeTrue("the legacy file is left intact for the next attempt");
    }

    [Fact]
    public void An_explicit_path_never_falls_back_to_the_legacy_appdata_book()
    {
        // The migration is only for the portable default; a second profile (or a test) that names
        // its own file must start empty rather than inherit whatever this machine has in %AppData%.
        new ConnectionBook(BookPath).Connections.Should().BeEmpty();
    }
}
