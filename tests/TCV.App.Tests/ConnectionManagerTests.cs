using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.FileSystem;
using TCV.App.Settings;
using TCV.App.ViewModels;
using TCV.App.ViewModels.Dialogs;
using TCV.Network;

namespace TCV.App.Tests;

/// <summary>
/// The connection manager in one window (list + editor), the editor's fields per kind of connection,
/// and the connection chip on a panel's drive strip.
/// </summary>
public sealed class ConnectionManagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tcv-connmgr-" + Guid.NewGuid().ToString("N"));

    public ConnectionManagerTests() => Directory.CreateDirectory(_dir);

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

    private ConnectionBook Book() => new(Path.Combine(_dir, "connections.json"));

    private static ConnectionInfo Prod() => new()
    {
        Name = "prod", Protocol = NetworkProtocol.Sftp, Host = "prod.example", Port = 22, Username = "deploy", Password = "pw",
    };

    private static ConnectionManagerDialogViewModel Manager(
        ConnectionBook book, bool confirm = true, List<Guid>? disconnected = null, Guid? select = null, string? address = null) =>
        new(book,
            whereOpen: info => info.Name == "prod" ? "left panel" : null,
            disconnect: id => disconnected?.Add(id),
            select: select,
            address: address,
            confirm: (_, _) => confirm,
            showMessage: (_, _) => { },
            showInfo: (_, _) => { });

    // ── Manager ──────────────────────────────────────────────────────────────

    [Fact]
    public void Open_WithAnEmptyBook_StartsANewConnection()
    {
        var vm = Manager(Book());

        vm.IsEmpty.Should().BeTrue();
        vm.Editor.Should().NotBeNull();
        vm.Editor!.IsNew.Should().BeTrue();
    }

    [Fact]
    public void Open_ShowsWhichConnectionsAreOpenAndWhere()
    {
        var book = Book();
        book.Add(Prod());
        book.Add(new ConnectionInfo { Name = "backup", Protocol = NetworkProtocol.WebDavs, Host = "b" });

        var vm = Manager(book);

        vm.Rows.Single(r => r.Title == "prod").WhereOpen.Should().Be("left panel");
        vm.Rows.Single(r => r.Title == "backup").IsOpen.Should().BeFalse();
    }

    [Fact]
    public void Save_ANewConnection_AddsItToTheBookAndSelectsIt()
    {
        var book = Book();
        var vm = Manager(book);
        var editor = vm.Editor!;
        editor.ConnectionName = "staging";
        editor.Host = "staging.example";
        editor.Username = "me";

        vm.SaveCommand.Execute(null);

        book.Connections.Should().ContainSingle(c => c.Name == "staging");
        vm.SelectedRow!.Title.Should().Be("staging");
        vm.Editor!.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void Connect_AnEditedConnection_SavesItFirstAndHandsItBack()
    {
        var book = Book();
        book.Add(Prod());
        var vm = Manager(book);
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;
        vm.Editor!.RemotePath = "/var/www";

        vm.ConnectCommand.Execute(null);

        closed.Should().BeTrue();
        vm.ConnectRequest!.RemotePath.Should().Be("/var/www");
        book.Connections.Single().RemotePath.Should().Be("/var/www", "what connects is what the user sees");
    }

    [Fact]
    public void Connect_IsUnavailableWhileTheFieldsAreIncomplete()
    {
        var vm = Manager(Book());

        vm.ConnectCommand.CanExecute(null).Should().BeFalse();
        vm.Editor!.ValidationMessage.Should().NotBeEmpty("a disabled button with no reason reads as broken");
    }

    [Fact]
    public void Disconnect_ClosesTheSelectedConnectionEverywhere()
    {
        var book = Book();
        var prod = Prod();
        book.Add(prod);
        var closed = new List<Guid>();
        var vm = Manager(book, disconnected: closed, select: prod.Id);

        vm.DisconnectCommand.Execute(null);

        closed.Should().Equal(prod.Id);
    }

    [Fact]
    public void Delete_AfterConfirmation_RemovesTheConnection()
    {
        var book = Book();
        book.Add(Prod());
        var vm = Manager(book);

        vm.DeleteCommand.Execute(null);

        book.Connections.Should().BeEmpty();
    }

    [Fact]
    public void Duplicate_StartsANewConnectionWithTheSameSettingsButNoSignIn()
    {
        var book = Book();
        var drive = new ConnectionInfo
        {
            Name = "drive", Protocol = NetworkProtocol.Dropbox, ClientId = "app-key", RefreshToken = "token",
        };
        book.Add(drive);
        var vm = Manager(book);

        vm.DuplicateCommand.Execute(null);

        vm.Editor!.IsNew.Should().BeTrue();
        vm.Editor.ClientId.Should().Be("app-key");
        vm.Editor.IsSignedIn.Should().BeFalse("a sign-in belongs to the connection that made it");
        vm.Editor.BuildConnectionInfo().Id.Should().NotBe(drive.Id);
    }

    [Fact]
    public void Filter_KeepsTheMatchingRows()
    {
        var book = Book();
        book.Add(Prod());
        book.Add(new ConnectionInfo { Name = "backup", Protocol = NetworkProtocol.WebDavs, Host = "b" });
        var vm = Manager(book);

        vm.Filter = "back";

        vm.Rows.Select(r => r.Title).Should().Equal("backup");
    }

    [Fact]
    public void Open_WithATypedAddress_FillsANewConnectionFromIt()
    {
        var vm = Manager(Book(), address: "s3://my-bucket/logs");

        var editor = vm.Editor!;
        editor.IsNew.Should().BeTrue();
        editor.IsS3.Should().BeTrue();
        editor.Bucket.Should().Be("my-bucket");
        editor.RemotePath.Should().Be("/logs");
        editor.Port.Should().Be(443, "an address without a port must not leave S3 on ConnectionInfo's FTP default of 21");
    }

    [Fact]
    public void Delete_WithUnsavedEdits_DoesNotOfferToSaveTheDeletedConnectionBack()
    {
        var book = Book();
        var prod = Prod();
        book.Add(prod);
        book.Add(new ConnectionInfo { Name = "backup", Protocol = NetworkProtocol.WebDavs, Host = "b" });
        var questions = 0;
        var vm = new ConnectionManagerDialogViewModel(
            book,
            select: prod.Id,
            confirm: (_, _) =>
            {
                questions++;
                return true;
            },
            showMessage: (_, _) => { },
            showInfo: (_, _) => { });
        vm.Editor!.RemotePath = "/srv";

        vm.DeleteCommand.Execute(null);

        questions.Should().Be(1, "only the delete itself is confirmed: its edits are gone with it");
        book.Connections.Should().ContainSingle(c => c.Name == "backup", "Update would add the deleted connection again");
        vm.SelectedRow!.Title.Should().Be("backup");
    }

    [Fact]
    public void Close_WithUnsavedChanges_AsksAndSavesOnYes()
    {
        var book = Book();
        book.Add(Prod());
        var vm = Manager(book, confirm: true);
        vm.Editor!.RemotePath = "/srv";

        vm.CanClose().Should().BeTrue();

        book.Connections.Single().RemotePath.Should().Be("/srv");
    }

    [Fact]
    public void Close_WithUnsavedChanges_DropsThemOnNo()
    {
        var book = Book();
        book.Add(Prod());
        var vm = Manager(book, confirm: false);
        vm.Editor!.RemotePath = "/srv";

        vm.CanClose().Should().BeTrue();

        book.Connections.Single().RemotePath.Should().Be("/");
    }

    // ── Editor ───────────────────────────────────────────────────────────────

    [Fact]
    public void Editor_PickingAProtocol_FollowsItsPortUnlessOneWasTyped()
    {
        var editor = new ConnectionEditorViewModel();
        editor.SelectedProtocol = editor.ProtocolOptions.First(p => p.Value == NetworkProtocol.Ftp);
        editor.Port.Should().Be(21);

        editor.Port = 2121;
        editor.SelectedProtocol = editor.ProtocolOptions.First(p => p.Value == NetworkProtocol.Sftp);

        editor.Port.Should().Be(2121, "a port typed on purpose stays");
    }

    [Fact]
    public void Editor_S3Preset_FillsTheEndpointAndFollowsTheRegion()
    {
        var editor = new ConnectionEditorViewModel();
        editor.SelectedProtocol = editor.ProtocolOptions.First(p => p.Value == NetworkProtocol.S3);
        editor.SelectedPreset = S3Presets.Find("aws");

        editor.Region = "eu-central-1";

        editor.Host.Should().Be("s3.eu-central-1.amazonaws.com");
        editor.Port.Should().Be(443);
    }

    [Fact]
    public void Editor_R2_WantsTheAccountIdInTheEndpoint()
    {
        var editor = new ConnectionEditorViewModel { ConnectionName = "r2", Username = "key", Password = "secret" };
        editor.SelectedProtocol = editor.ProtocolOptions.First(p => p.Value == NetworkProtocol.S3);
        editor.SelectedPreset = S3Presets.Find("r2");

        editor.Host.Should().Contain(ConnectionEditorViewModel.AccountPlaceholder);
        editor.IsValid.Should().BeFalse();

        editor.Host = "abc123.r2.cloudflarestorage.com";
        editor.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(NetworkProtocol.S3, true, false, false)]
    [InlineData(NetworkProtocol.AzureBlob, true, false, false)]
    [InlineData(NetworkProtocol.Dropbox, false, true, false)]
    [InlineData(NetworkProtocol.Smb, false, false, true)]
    [InlineData(NetworkProtocol.Sftp, false, false, false)]
    public void Editor_ShowsTheFieldsOfTheKind(NetworkProtocol protocol, bool storage, bool oauth, bool smb)
    {
        var editor = new ConnectionEditorViewModel();

        editor.SelectedProtocol = editor.ProtocolOptions.First(p => p.Value == protocol);

        editor.IsObjectStorage.Should().Be(storage);
        editor.IsOAuth.Should().Be(oauth);
        editor.IsSmb.Should().Be(smb);
        editor.ShowPasswordField.Should().Be(!oauth, "a drive signs in through the browser");
        editor.ShowPort.Should().Be(!oauth && !smb);
    }

    [Fact]
    public void Editor_S3WithAProfile_NeedsNoTypedKeys()
    {
        var editor = new ConnectionEditorViewModel { ConnectionName = "aws" };
        editor.SelectedProtocol = editor.ProtocolOptions.First(p => p.Value == NetworkProtocol.S3);
        editor.SelectedPreset = S3Presets.Find("aws");
        editor.IsValid.Should().BeFalse();

        editor.Profile = "work";

        editor.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Editor_SmbWithoutAShare_IsNotValid()
    {
        var editor = new ConnectionEditorViewModel { ConnectionName = "nas" };
        editor.SelectedProtocol = editor.ProtocolOptions.First(p => p.Value == NetworkProtocol.Smb);
        editor.Host = "nas";
        editor.RemotePath = "/";
        editor.IsValid.Should().BeFalse();

        editor.RemotePath = "/projects";

        editor.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Editor_ApplyAddress_FillsTheFields()
    {
        var editor = new ConnectionEditorViewModel { Address = "sftp://deploy@prod.example:2222/var/log" };

        editor.ApplyAddressCommand.Execute(null);

        editor.Protocol.Should().Be(NetworkProtocol.Sftp);
        editor.Host.Should().Be("prod.example");
        editor.Port.Should().Be(2222);
        editor.Username.Should().Be("deploy");
        editor.RemotePath.Should().Be("/var/log");
        editor.ConnectionName.Should().Be("prod.example");
        editor.Address.Should().Be("sftp://deploy@prod.example:2222/var/log", "the typed address stays in its box");
    }

    [Theory]
    [InlineData("https://my-bucket.s3.eu-west-1.amazonaws.com/a", NetworkProtocol.S3)]
    [InlineData("https://acct.blob.core.windows.net/builds", NetworkProtocol.AzureBlob)]
    public void Editor_ApplyAddress_CloudStorageAddress_PutsTheHttpsPortInTheField(string address, NetworkProtocol protocol)
    {
        // The address names no port; the field once got ConnectionInfo's starting 21 from it.
        var editor = new ConnectionEditorViewModel { Address = address };

        editor.ApplyAddressCommand.Execute(null);

        editor.Protocol.Should().Be(protocol);
        editor.Port.Should().Be(443);
    }

    [Fact]
    public void Editor_ApplyAddress_HttpsOfUnknownHostOnS3_FillsTheEndpoint()
    {
        var editor = new ConnectionEditorViewModel(new ConnectionInfo
        {
            Name = "Parsers dumps", Protocol = NetworkProtocol.S3, Provider = S3Presets.OtherId, Port = 443, Region = "us-east-1",
        })
        {
            Address = "https://eu2.contabostorage.com",
        };

        editor.ApplyAddressCommand.Execute(null);

        editor.ConnectionStatusIsError.Should().BeFalse();
        editor.Protocol.Should().Be(NetworkProtocol.S3);
        editor.Host.Should().Be("eu2.contabostorage.com");
        editor.Port.Should().Be(443);
        editor.UseHttp.Should().BeFalse();
        editor.ConnectionName.Should().Be("Parsers dumps");
        editor.Address.Should().Be("https://eu2.contabostorage.com");
    }

    [Fact]
    public void Editor_ApplyAddress_EndpointOnAnUnnamedConnection_NamesItAfterTheHost()
    {
        var editor = new ConnectionEditorViewModel(new ConnectionInfo { Name = "x", Protocol = NetworkProtocol.S3, Provider = "wasabi" });
        editor.ConnectionName = string.Empty;
        editor.Address = "http://localhost:9000/logs/2026";

        editor.ApplyAddressCommand.Execute(null);

        editor.SelectedPreset.Should().BeSameAs(S3Presets.Find(S3Presets.OtherId), "the address, not the old provider, says what the store is");
        editor.Host.Should().Be("localhost");
        editor.Port.Should().Be(9000);
        editor.UseHttp.Should().BeTrue();
        editor.PathStyle.Should().BeTrue();
        editor.Bucket.Should().Be("logs");
        editor.RemotePath.Should().Be("/2026");
        editor.ConnectionName.Should().Be("localhost");
    }

    [Fact]
    public void Editor_ApplyAddress_HttpsOfUnknownHostOffS3_StillNoConnection()
    {
        var editor = new ConnectionEditorViewModel { Address = "https://eu2.contabostorage.com" };

        editor.ApplyAddressCommand.Execute(null);

        editor.ConnectionStatusIsError.Should().BeTrue();
        editor.Host.Should().BeEmpty();
    }

    [Fact]
    public void Editor_BuildConnectionInfo_RoundTripsEveryField()
    {
        var original = new ConnectionInfo
        {
            Name = "logs", Protocol = NetworkProtocol.S3, Host = "s3.eu-west-1.amazonaws.com", Port = 443,
            Username = "AKIA", Password = "secret", Region = "eu-west-1", Bucket = "logs", PathStyle = true,
            Provider = "aws", RemotePath = "/2026", Color = "#E5484D", ReadOnly = true,
        };

        var built = new ConnectionEditorViewModel(original).BuildConnectionInfo();

        built.Should().BeEquivalentTo(original, o => o.Excluding(c => c.SecretsChanged));
    }

    [Fact]
    public void Editor_DriveWithAnUnreadableClientSecret_IsReadableOnceTheSecretIsTypedAgain()
    {
        // A drive has no password box, so the "unreadable" state of its stored secret (copied to another PC)
        // can only be cleared by typing the client secret; the password stays empty.
        var editor = new ConnectionEditorViewModel(new ConnectionInfo
        {
            Name = "g", Protocol = NetworkProtocol.GoogleDrive, ClientId = "id", PasswordUnreadable = true,
        });
        editor.BuildConnectionInfo().PasswordUnreadable.Should().BeTrue("nothing has been typed yet");

        editor.ClientSecret = "new-secret";

        editor.BuildConnectionInfo().PasswordUnreadable.Should().BeFalse();
    }

    [Fact]
    public void Editor_SignOut_ForgetsTheDrivesSignIn()
    {
        var editor = new ConnectionEditorViewModel(new ConnectionInfo
        {
            Name = "d", Protocol = NetworkProtocol.OneDrive, ClientId = "id", RefreshToken = "t", Username = "me@example.com",
        });
        editor.IsSignedIn.Should().BeTrue();

        editor.SignOutCommand.Execute(null);

        editor.IsSignedIn.Should().BeFalse();
        editor.BuildConnectionInfo().RefreshToken.Should().BeNull();
        editor.IsDirty.Should().BeTrue("signing out is a change to save");
    }

    [Fact]
    public void SameSettings_ANameOrColourChange_IsTheSameSession()
    {
        var saved = Prod();
        var renamed = saved.Clone();
        renamed.Name = "production";
        renamed.Color = "#E5484D";

        MountCommands.SameSettings(saved, renamed).Should().BeTrue();
    }

    [Theory]
    [InlineData("readonly")]
    [InlineData("password")]
    [InlineData("path")]
    public void SameSettings_AnythingTheSessionUses_MakesItAnother(string change)
    {
        var saved = Prod();
        var edited = saved.Clone();
        switch (change)
        {
            case "readonly": edited.ReadOnly = true; break;
            case "password": edited.Password = "new"; break;
            default: edited.RemotePath = "/srv"; break;
        }

        MountCommands.SameSettings(saved, edited).Should().BeFalse("reconnecting must pick the change up");
    }

    // ── Windows shares ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("nas", "/projects/web", @"\\nas\projects", @"\\nas\projects\web")]
    [InlineData("nas", "/projects", @"\\nas\projects", @"\\nas\projects")]
    [InlineData(@"\\nas", @"\projects\web", @"\\nas\projects", @"\\nas\projects\web")]
    public void SharePaths_TheFirstSegmentIsTheShare(string host, string path, string root, string folder)
    {
        var share = MountCommands.SharePaths(new ConnectionInfo { Protocol = NetworkProtocol.Smb, Host = host, RemotePath = path }, null);

        share.Should().Be((root, folder));
    }

    [Fact]
    public void SharePaths_WithoutAShare_IsNull()
    {
        MountCommands.SharePaths(new ConnectionInfo { Protocol = NetworkProtocol.Smb, Host = "nas", RemotePath = "/" }, null)
            .Should().BeNull();
    }

    // ── The chip on the drive strip ──────────────────────────────────────────

    private static PanelViewModel Panel()
    {
        var local = new InMemoryFileSystem("file");
        local.AddDirectory("/C:/a");
        var drives = new DriveManager();
        var panel = new PanelViewModel("Left", new FileListViewModel(local, NullLogger<FileListViewModel>.Instance),
            new DriveBarViewModel(drives), new StatusBarViewModel(drives), new AppSettings(), new FileWatcher());
        panel.LoadInitialPath("/C:/a");
        return panel;
    }

    [Fact]
    public void Chip_OnTheDisk_IsTheConnectButton()
    {
        var panel = Panel();

        panel.ChipState.Should().Be(ConnectionChipState.None);
        panel.HasConnection.Should().BeFalse();
    }

    [Fact]
    public void Chip_WhileConnecting_SaysSoAndOffersNoDisconnect()
    {
        var panel = Panel();

        panel.ConnectingTo = Prod();

        panel.ChipState.Should().Be(ConnectionChipState.Connecting);
        panel.ChipTitle.Should().Be("prod");
        panel.HasConnection.Should().BeFalse();
        panel.IsConnecting.Should().BeTrue();
    }

    [Fact]
    public void Chip_OnAServer_NamesItAndOffersDisconnect()
    {
        var panel = Panel();
        var server = Prod();

        panel.FileList.Mount(new InMemoryFileSystem("sftp"), "/", server, MountKind.Remote);

        panel.ChipConnection.Should().BeSameAs(server);
        panel.ChipTitle.Should().Be("prod");
        panel.HasConnection.Should().BeTrue();
        panel.ChipToolTip.Should().Contain("sftp://deploy@prod.example/");
    }

    [Fact]
    public async Task Chip_WhenTheListingFails_TurnsRed()
    {
        var panel = Panel();
        var remote = new InMemoryFileSystem("sftp") { FailListingOf = "/" };

        panel.FileList.Mount(remote, "/", Prod(), MountKind.Remote);
        await panel.FileList.LoadAsync("/");

        panel.ChipState.Should().Be(ConnectionChipState.Failed);
    }

    [Fact]
    public void Chip_InAShareThisWindowOpened_NamesTheShare()
    {
        var panel = Panel();
        var share = new ConnectionInfo { Name = "nas", Protocol = NetworkProtocol.Smb, Host = "nas" };
        panel.ShareConnectionFor = path => path.StartsWith(@"\\nas\", StringComparison.OrdinalIgnoreCase) ? share : null;

        panel.CurrentPath = @"\\nas\projects";

        panel.ChipConnection.Should().BeSameAs(share);
        panel.ChipKind.Should().Be("share");
    }
}
