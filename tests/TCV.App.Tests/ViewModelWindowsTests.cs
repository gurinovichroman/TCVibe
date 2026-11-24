using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TCV.App.FileSystem;
using TCV.App.Services;
using TCV.App.Settings;
using TCV.App.ViewModels.Dialogs;
using TCV.Core.Domain;
using TCV.Core.Interfaces;
using TCV.Network;
using TCV.Tools;

namespace TCV.App.Tests;

/// <summary>
/// A view model never makes a window: it asks <see cref="IDialogService"/> (6.70). The first test keeps
/// the rule by reading the source; the others run what used to need a real file picker on screen.
/// </summary>
public sealed class ViewModelWindowsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tcv-vmw-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IDialogService> _dialogs = new();

    public ViewModelWindowsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    // ── The rule ──────────────────────────────────────────────────────────────

    [Fact]
    public void ViewModels_InTheSource_CreateNoWindows()
    {
        // Names that end in Dialog or Window: CopyDialogResult and FooDialogViewModel do not match.
        var forbidden = new Regex(
            @"new\s+(\w+\.)*\w*(Dialog|Window)\b|MessageBox\.Show|Views\.Dialogs|ConfirmDialog\.");

        var offenders = Directory
            .EnumerateFiles(Path.Combine(SourceTree.AppDirectory, "ViewModels"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => File.ReadLines(file)
                .Select((line, index) => (file, number: index + 1, line))
                .Where(row => forbidden.IsMatch(row.line)))
            .Select(row => $"{Path.GetFileName(row.file)}:{row.number}: {row.line.Trim()}")
            .ToList();

        offenders.Should().BeEmpty("a view model goes through IDialogService, or the commands cannot be run without a desktop");
    }

    // ── Checksums ─────────────────────────────────────────────────────────────

    private ChecksumDialogViewModel Checksums(string file) =>
        new(
            [new FileEntry(new VfsPath(file), Path.GetFileName(file), false, 10, DateTime.Now, FileAttributes.Normal)],
            _dir,
            new ChecksumEngine(),
            _dialogs.Object);

    private string DataFile()
    {
        var data = Path.Combine(_dir, "data.bin");
        File.WriteAllText(data, "payload");
        return data;
    }

    [Fact]
    public async Task SaveChecksumFile_APathIsPicked_WritesThereAndStartsInThePanelsFolder()
    {
        var sums = Path.Combine(_dir, "picked.md5");
        _dialogs.Setup(d => d.PickSaveFile(It.IsAny<string>(), It.IsAny<string>(), "checksums.md5", _dir, null))
            .Returns(sums);
        var vm = Checksums(DataFile());
        await vm.CalculateCommand.ExecuteAsync(null);

        await vm.SaveChecksumFileCommand.ExecuteAsync(null);

        File.ReadAllText(sums).Should().Contain("data.bin");
    }

    [Fact]
    public async Task SaveChecksumFile_ThePickerIsCancelled_WritesNothing()
    {
        var vm = Checksums(DataFile());
        await vm.CalculateCommand.ExecuteAsync(null);
        var status = vm.StatusText;

        await vm.SaveChecksumFileCommand.ExecuteAsync(null);

        _dialogs.Verify(
            d => d.PickSaveFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Once);
        vm.StatusText.Should().Be(status);
        Directory.GetFiles(_dir).Should().ContainSingle();
    }

    [Fact]
    public async Task LoadChecksumForVerify_AFileIsPicked_VerifiesIt()
    {
        var sums = Path.Combine(_dir, "data.md5");
        File.WriteAllText(sums, "ffffffffffffffffffffffffffffffff  gone.bin\n");
        _dialogs.Setup(d => d.PickOpenFile(It.IsAny<string>(), It.IsAny<string>(), _dir)).Returns(sums);
        var vm = Checksums(DataFile());

        await vm.LoadChecksumForVerifyCommand.ExecuteAsync(null);

        vm.VerifyFilePath.Should().Be(sums);
        vm.VerifyResults.Should().ContainSingle(r => r.FileName == "gone.bin");
    }

    [Fact]
    public async Task LoadChecksumForVerify_ThePickerIsCancelled_StaysOnTheCalculateTab()
    {
        var vm = Checksums(DataFile());

        await vm.LoadChecksumForVerifyCommand.ExecuteAsync(null);

        vm.VerifyMode.Should().BeFalse();
        vm.VerifyFilePath.Should().BeEmpty();
    }

    // ── Hotkeys ───────────────────────────────────────────────────────────────

    private HotkeySettingsDialogViewModel Hotkeys()
    {
        var settings = new AppSettings();
        return new HotkeySettingsDialogViewModel(
            new HotkeyManager(settings),
            settings,
            new SettingsSerializer(NullLogger<SettingsSerializer>.Instance, Path.Combine(_dir, "settings.json")),
            _dialogs.Object);
    }

    [Fact]
    public void ExportProfile_APathIsPicked_WritesTheProfile()
    {
        var profile = Path.Combine(_dir, "picked.json");
        _dialogs.Setup(d => d.PickSaveFile(It.IsAny<string>(), It.IsAny<string>(), "hotkeys-profile.json", null, null))
            .Returns(profile);

        Hotkeys().ExportProfileCommand.Execute(null);

        File.ReadAllText(profile).Should().Contain("Copy");
    }

    [Fact]
    public void ExportProfile_ThePickerIsCancelled_WritesNothing()
    {
        var vm = Hotkeys();

        vm.ExportProfileCommand.Execute(null);

        vm.HasError.Should().BeFalse();
        Directory.GetFiles(_dir).Should().BeEmpty();
    }

    [Fact]
    public void ImportProfile_AFileIsPicked_TakesItsGestures()
    {
        var profile = Path.Combine(_dir, "picked.json");
        File.WriteAllText(profile, """{ "WorkspacePin": "Ctrl+Alt+F11" }""");
        _dialogs.Setup(d => d.PickOpenFile(It.IsAny<string>(), It.IsAny<string>(), null)).Returns(profile);
        var vm = Hotkeys();

        vm.ImportProfileCommand.Execute(null);

        vm.Bindings.Single(b => b.CommandId == "WorkspacePin").DisplayText.Should().Be("Ctrl+Alt+F11");
    }

    // ── Plugins ───────────────────────────────────────────────────────────────

    private PluginManagerDialogViewModel Plugins()
    {
        var loader = new PluginHost.PluginLoader(NullLogger<PluginHost.PluginLoader>.Instance);
        var host = new PluginHost.PluginHost(
            NullLogger<PluginHost.PluginHost>.Instance,
            loader,
            new WindowsFileSystem(NullLogger<WindowsFileSystem>.Instance));
        return new PluginManagerDialogViewModel(host, loader, _dialogs.Object, Path.Combine(_dir, "plugins"));
    }

    private void AnswerLoadWarning(bool answer) =>
        _dialogs.Setup(d => d.Confirm(It.IsAny<string>(), It.IsAny<string>(), null, null, true)).Returns(answer);

    [Fact]
    public void LoadPlugin_TheWarningIsDeclined_DoesNotAskForAFile()
    {
        AnswerLoadWarning(false);

        Plugins().LoadPluginCommand.Execute(null);

        _dialogs.Verify(d => d.PickOpenFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public void LoadPlugin_ThePickerIsCancelled_LoadsNothing()
    {
        AnswerLoadWarning(true);
        var vm = Plugins();

        vm.LoadPluginCommand.Execute(null);

        _dialogs.Verify(d => d.PickOpenFile(It.IsAny<string>(), It.IsAny<string>(), null), Times.Once);
        vm.StatusMessage.Should().BeEmpty();
    }

    [Fact]
    public void LoadPlugin_AFileWithoutPluginsIsPicked_SaysSo()
    {
        AnswerLoadWarning(true);
        var notAPlugin = Path.Combine(_dir, "notes.dll");
        File.WriteAllText(notAPlugin, "not an assembly");
        _dialogs.Setup(d => d.PickOpenFile(It.IsAny<string>(), It.IsAny<string>(), null)).Returns(notAPlugin);
        var vm = Plugins();

        vm.LoadPluginCommand.Execute(null);

        vm.StatusMessage.Should().NotBeEmpty("the file the service picked went to the host");
    }

    // ── Connection manager ────────────────────────────────────────────────────

    [Fact]
    public void Delete_NobodyToAsk_KeepsTheConnection()
    {
        var book = new ConnectionBook(Path.Combine(_dir, "connections.json"));
        var saved = new ConnectionInfo { Name = "prod", Protocol = NetworkProtocol.Sftp, Host = "prod.example" };
        book.Add(saved);

        var vm = new ConnectionManagerDialogViewModel(book, select: saved.Id);
        vm.DeleteCommand.Execute(null);

        book.Connections.Should().ContainSingle("no question was answered, so nothing is deleted (INV-11)");
    }
}
