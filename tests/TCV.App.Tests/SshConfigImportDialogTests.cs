using System.IO;
using FluentAssertions;
using TCV.App.ViewModels.Dialogs;
using TCV.Network;

namespace TCV.App.Tests;

/// <summary>
/// Importing hosts from <c>~/.ssh/config</c> in the connection manager (6.82): a preview first, what is
/// saved already left unticked, nothing written until Import — and never a byte written under <c>.ssh</c>.
/// The home folder is the test's own, never the real profile.
/// </summary>
public sealed class SshConfigImportDialogTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "tcv-sshimport-" + Guid.NewGuid().ToString("N"));

    public SshConfigImportDialogTests() => Directory.CreateDirectory(Path.Combine(_home, ".ssh"));

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

    private string ConfigPath => Path.Combine(_home, ".ssh", "config");

    private ConnectionBook Book() => new(Path.Combine(_home, "connections.json"));

    private ConnectionManagerDialogViewModel Manager(
        ConnectionBook book, Func<SshConfigImportDialogViewModel, bool> showImport, List<string>? infos = null) =>
        new(book,
            confirm: (_, _) => false,
            showMessage: (_, message) => infos?.Add(message),
            showInfo: (_, message) => infos?.Add(message),
            showImport: showImport,
            homeDirectory: _home);

    [Fact]
    public void Import_AddsTheTickedHosts_AndLeavesSavedOnesOut()
    {
        File.WriteAllText(ConfigPath, """
            Host *
                User deploy
            Host web
                HostName web1.example.com
            Host db
                HostName db.example.com
                Port 2200
            """);
        var configBefore = File.ReadAllText(ConfigPath);
        var book = Book();
        book.Add(new ConnectionInfo { Name = "Web (saved)", Protocol = NetworkProtocol.Sftp, Host = "web1.example.com", Port = 22, Username = "deploy" });
        SshConfigImportDialogViewModel? shown = null;
        var vm = Manager(book, preview =>
        {
            shown = preview;
            return true;
        });

        vm.ImportSshConfigCommand.Execute(null);

        shown.Should().NotBeNull();
        shown!.Rows.Select(r => (r.Title, r.IsSelected, r.IsDuplicate)).Should().Equal(("web", false, true), ("db", true, false));
        book.Connections.Select(c => c.Name).Should().BeEquivalentTo("Web (saved)", "db");
        var db = book.Connections.Single(c => c.Name == "db");
        db.Protocol.Should().Be(NetworkProtocol.Sftp);
        db.Port.Should().Be(2200);
        db.Username.Should().Be("deploy");
        vm.SelectedRow!.Title.Should().Be("db", "the first imported connection is shown");
        File.ReadAllText(ConfigPath).Should().Be(configBefore, "~/.ssh is only ever read");
        Directory.GetFiles(Path.Combine(_home, ".ssh")).Should().ContainSingle();
    }

    [Fact]
    public void Import_Cancelled_AddsNothing()
    {
        File.WriteAllText(ConfigPath, "Host web\n    HostName web1.example.com\n");
        var book = Book();
        var vm = Manager(book, _ => false);

        vm.ImportSshConfigCommand.Execute(null);

        book.Connections.Should().BeEmpty();
    }

    [Fact]
    public void Import_WithoutAConfig_SaysSoWithoutAPreview()
    {
        var infos = new List<string>();
        var previewed = false;
        var vm = Manager(Book(), _ => previewed = true, infos);

        vm.ImportSshConfigCommand.Execute(null);

        previewed.Should().BeFalse();
        infos.Should().ContainSingle();
    }

    [Fact]
    public void Import_OnlyWildcards_SaysThereIsNothingToImport()
    {
        File.WriteAllText(ConfigPath, "Host *\n    User me\n");
        var infos = new List<string>();
        var previewed = false;
        var vm = Manager(Book(), _ => previewed = true, infos);

        vm.ImportSshConfigCommand.Execute(null);

        previewed.Should().BeFalse();
        infos.Should().ContainSingle();
    }

    [Fact]
    public void Preview_NothingTicked_CannotImport()
    {
        var host = new SshConfigHost("web", "web1", "u", null, [], null, false);
        var candidate = SshConfigImport.Plan([host], [], _home, "me", _ => false).Single();
        var preview = new SshConfigImportDialogViewModel(ConfigPath, [candidate],
            [new SshConfigWarning(SshConfigWarningKind.IncludeMissing, "conf.d/x")]);

        preview.ImportCommand.CanExecute(null).Should().BeTrue();
        preview.HasWarnings.Should().BeTrue();

        preview.Rows.Single().IsSelected = false;

        preview.ImportCommand.CanExecute(null).Should().BeFalse();
        preview.Chosen.Should().BeEmpty();
    }
}
