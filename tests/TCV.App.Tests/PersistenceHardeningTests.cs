using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.Settings;
using TCV.App.ViewModels.Dialogs;
using TCV.App.Workspaces;

namespace TCV.App.Tests;

/// <summary>
/// Settings and workspace persistence: which window may write, what a damaged file falls back to,
/// and that a layout autosave touches only the layout.
/// </summary>
public sealed class PersistenceHardeningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"tcv-ph-{Guid.NewGuid():N}");

    public PersistenceHardeningTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private string SettingsFile => Path.Combine(_dir, "settings.json");
    private string WorkspacesFile => Path.Combine(_dir, "workspaces.json");

    private SettingsSerializer Serializer(Func<bool>? readOnlyWhen = null) =>
        new(NullLogger<SettingsSerializer>.Instance, SettingsFile) { ReadOnlyWhen = readOnlyWhen };

    /// <summary>A serializer whose writes fail: its target path is a directory.</summary>
    private SettingsSerializer FailingSerializer()
    {
        var blocked = Path.Combine(_dir, "blocked", "settings.json");
        Directory.CreateDirectory(blocked);
        return new SettingsSerializer(NullLogger<SettingsSerializer>.Instance, blocked);
    }

    // ── SettingsSerializer ────────────────────────────────────────────────────

    [Fact]
    public void Save_GuestDowngradedAfterConstruction_WritesTheFile()
    {
        var guest = true;
        var serializer = Serializer(() => guest);

        serializer.Save(new AppSettings()).Should().BeFalse();
        File.Exists(SettingsFile).Should().BeFalse();

        guest = false;   // DowngradeToPrimary: the workspace asked for was not found

        serializer.Save(new AppSettings()).Should().BeTrue();
        File.Exists(SettingsFile).Should().BeTrue();
    }

    [Fact]
    public void Load_DamagedFileWithGoodBackup_LoadsTheBackup()
    {
        var good = new AppSettings();
        good.Theme.ThemeName = "Light";
        Serializer().Save(good);
        Serializer().Save(good);            // the second save leaves the first as settings.json.bak
        File.WriteAllText(SettingsFile, "{ not json");

        var serializer = Serializer();
        var loaded = serializer.Load();

        loaded.Theme.ThemeName.Should().Be("Light");
        serializer.RestoredFromBackup.Should().BeTrue();
        serializer.LoadWarning.Should().NotBeNull();
        Directory.GetFiles(_dir, "settings.json.corrupt-*").Should().NotBeEmpty();
    }

    [Fact]
    public void Load_DamagedFileWithoutBackup_FallsBackToDefaults()
    {
        File.WriteAllText(SettingsFile, "{ not json");

        var serializer = Serializer();
        serializer.Load().Theme.ThemeName.Should().Be("Dark");
        serializer.RestoredFromBackup.Should().BeFalse();
    }

    // ── SettingsBundle ────────────────────────────────────────────────────────

    [Fact]
    public void FromJson_NullSections_AreNormalized()
    {
        var bundle = SettingsBundle.FromJson("{\"Settings\":{\"Theme\":null,\"Hotkeys\":null}}");

        bundle!.Settings!.Theme.Should().NotBeNull();
        bundle.Settings.Hotkeys.Should().NotBeNull();
    }

    // ── Settings dialog: a failed save leaves the live settings alone ────────

    [Fact]
    public void SettingsDialogSave_WriteFails_LeavesLiveSettingsUnchanged()
    {
        var settings = new AppSettings();
        var vm = new SettingsDialogViewModel(settings, FailingSerializer())
        {
            McpEnabled = true,
            LlmModel = "changed",
        };

        vm.SaveCommand.Execute(null);

        vm.SaveError.Should().NotBeEmpty();
        settings.Agent.McpEnabled.Should().BeFalse();
        settings.Agent.LlmModel.Should().BeEmpty();
    }

    [Fact]
    public void SettingsDialogSave_WriteSucceeds_AppliesToLiveSettings()
    {
        var settings = new AppSettings();
        var vm = new SettingsDialogViewModel(settings, Serializer()) { LlmModel = "m" };

        vm.SaveCommand.Execute(null);

        vm.SavedSuccessfully.Should().BeTrue();
        settings.Agent.LlmModel.Should().Be("m");
    }

    // ── WorkspaceStore ────────────────────────────────────────────────────────

    [Fact]
    public void UpdateLayout_KeepsIdentityChangedByAnotherWindow()
    {
        var store = new WorkspaceStore(WorkspacesFile);
        store.Save(new Workspace { Name = "api", Color = "#FF0000" });

        // Another window recolours it; this window still holds the old colour in memory.
        var other = store.Get("api")!;
        other.Color = "#00FF00";
        other.Pinned = true;
        store.Save(other);

        var layout = new Workspace { Name = "api", Color = "#FF0000", SplitVertical = true };
        layout.Left.Tabs.Add(new TabState { Path = @"C:\src" });

        store.UpdateLayout("api", layout).Should().BeTrue();

        var stored = store.Get("api")!;
        stored.Color.Should().Be("#00FF00");
        stored.Pinned.Should().BeTrue();
        stored.SplitVertical.Should().BeTrue();
        stored.Left.Tabs.Should().ContainSingle(t => t.Path == @"C:\src");
    }

    [Fact]
    public void UpdateLayout_EntryDeletedElsewhere_DoesNotResurrectIt()
    {
        var store = new WorkspaceStore(WorkspacesFile);
        store.Save(new Workspace { Name = "old" });
        store.Delete("old");

        store.UpdateLayout("old", new Workspace { Name = "old" }).Should().BeFalse();

        store.LoadAll().Should().BeEmpty();
    }

    [Fact]
    public void LoadAll_DamagedFile_KeepsACopyOnceAndReportsIt()
    {
        var store = new WorkspaceStore(WorkspacesFile);
        store.Save(new Workspace { Name = "a" });
        store.Save(new Workspace { Name = "b" });
        File.WriteAllText(WorkspacesFile, "[ broken");

        store.LoadAll().Should().ContainSingle(w => w.Name == "a");   // the backup
        store.LoadAll();

        store.CorruptCopyPath.Should().NotBeNull();
        File.ReadAllText(store.CorruptCopyPath!).Should().Be("[ broken");
        Directory.GetFiles(_dir, "workspaces.json.corrupt-*").Should().ContainSingle();
    }

    [Fact]
    public void Arrange_MruBumpAfterEdit_DoesNotMoveShownEntries()
    {
        var a = new Workspace { Name = "a" };
        var b = new Workspace { Name = "b" };
        var c = new Workspace { Name = "c" };
        var shown = new[] { a, b, c };

        // The reload sorts "c" first because a switch just bumped its MRU stamp.
        var loaded = new[] { new Workspace { Name = "c" }, new Workspace { Name = "a" }, new Workspace { Name = "b" } };

        WorkspaceOrder.Arrange(loaded, shown).Select(w => w.Name)
            .Should().Equal("a", "b", "c");
    }

    [Fact]
    public void Arrange_PinnedMovesAheadAndNewEntriesGoLast()
    {
        var shown = new[] { new Workspace { Name = "a" }, new Workspace { Name = "b" } };
        var loaded = new[]
        {
            new Workspace { Name = "b", Pinned = true },
            new Workspace { Name = "new" },
            new Workspace { Name = "a" },
        };

        WorkspaceOrder.Arrange(loaded, shown).Select(w => w.Name)
            .Should().Equal("b", "a", "new");
    }

    // ── WorkspaceProjectFile.SharesRoot ───────────────────────────────────────

    [Fact]
    public void SharesRoot_SessionOpensTheDefinitionRoot_IsTrue()
    {
        var fromFile = new Workspace();
        fromFile.Left.Tabs.Add(new TabState { Path = @"D:\repo" });
        var session = new Workspace();
        session.Right.Tabs.Add(new TabState { Path = @"d:\repo\" });

        WorkspaceProjectFile.SharesRoot(fromFile, session).Should().BeTrue();
    }

    [Fact]
    public void SharesRoot_UnrelatedWorkspaceWithTheSameName_IsFalse()
    {
        var fromFile = new Workspace();
        fromFile.Left.Tabs.Add(new TabState { Path = @"D:\repoB" });
        var session = new Workspace();
        session.Left.Tabs.Add(new TabState { Path = @"D:\repoA" });

        WorkspaceProjectFile.SharesRoot(fromFile, session).Should().BeFalse();
    }

    // ── Hotkey dialog ─────────────────────────────────────────────────────────

    private HotkeySettingsDialogViewModel HotkeyDialog(AppSettings settings, SettingsSerializer? serializer = null) =>
        new(new HotkeyManager(settings), settings, serializer ?? Serializer());

    private static HotkeyBindingViewModel Row(HotkeySettingsDialogViewModel vm, string id) =>
        vm.Bindings.Single(b => b.CommandId == id);

    [Fact]
    public void HotkeyDialog_ListsCommandsThatShipWithoutAKey_AndSavesTheOneGiven()
    {
        // These were missing from the list, so the UI had no way to give them a key at all.
        var settings = new AppSettings();
        var vm = HotkeyDialog(settings);
        var pin = Row(vm, "WorkspacePin");
        pin.DisplayText.Should().BeEmpty();

        vm.SelectedBinding = pin;
        vm.EditGesture = "Ctrl+Shift+F11";
        vm.SaveCommand.Execute(null);

        settings.Hotkeys.Should().ContainKey("WorkspacePin").WhoseValue.Should().Be("Ctrl+Shift+F11");
    }

    [Fact]
    public void HotkeySave_KeepsAnOverrideSavedUnderAMergedId()
    {
        // Ctrl+Alt+R used to be WorkspaceRename; a key the user gave it moves to WorkspaceProperties.
        // Read as unmodified, the next save in the dialog rebuilt the overrides without it.
        var settings = new AppSettings();
        settings.Hotkeys["WorkspaceRename"] = "Ctrl+Shift+F11";
        var vm = HotkeyDialog(settings);

        Row(vm, "WorkspaceProperties").IsModified.Should().BeTrue();
        vm.SaveCommand.Execute(null);

        settings.Hotkeys.Should().ContainKey("WorkspaceProperties").WhoseValue.Should().Be("Ctrl+Shift+F11");
    }

    [Fact]
    public void ResetHotkey_OnACommandWithoutADefault_ClearsTheKey()
    {
        var vm = HotkeyDialog(new AppSettings());
        vm.SelectedBinding = Row(vm, "DuplicateTab");
        vm.EditGesture = "Ctrl+Shift+F11";
        vm.ApplyHotkeyCommand.Execute(null);

        vm.ResetHotkeyCommand.Execute(null);

        Row(vm, "DuplicateTab").DisplayText.Should().BeEmpty();
        Row(vm, "DuplicateTab").IsModified.Should().BeFalse();
    }

    [Fact]
    public void HotkeySave_GestureTypedButNotApplied_IsSaved()
    {
        var settings = new AppSettings();
        var vm = HotkeyDialog(settings);
        vm.SelectedBinding = Row(vm, "Search");
        vm.EditGesture = "Ctrl+Shift+Y";

        vm.SaveCommand.Execute(null);

        vm.SavedSuccessfully.Should().BeTrue();
        settings.Hotkeys.Should().ContainKey("Search").WhoseValue.Should().Be("Ctrl+Shift+Y");
    }

    [Fact]
    public void HotkeySave_PendingGestureConflicts_StaysOpenAndSavesNothing()
    {
        var settings = new AppSettings();
        var vm = HotkeyDialog(settings);
        vm.SelectedBinding = Row(vm, "Search");
        vm.EditGesture = "Ctrl+C";   // ClipboardCopy

        vm.SaveCommand.Execute(null);

        vm.SavedSuccessfully.Should().BeFalse();
        vm.HasError.Should().BeTrue();
        settings.Hotkeys.Should().BeEmpty();
    }

    [Fact]
    public void HotkeySave_WriteFails_LeavesLiveHotkeysUnchanged()
    {
        var settings = new AppSettings();
        var vm = HotkeyDialog(settings, FailingSerializer());
        vm.SelectedBinding = Row(vm, "Search");
        vm.EditGesture = "Ctrl+Shift+Y";
        vm.ApplyHotkeyCommand.Execute(null);

        vm.SaveCommand.Execute(null);

        vm.HasError.Should().BeTrue();
        settings.Hotkeys.Should().BeEmpty();
    }

    [Fact]
    public void ResetHotkey_DefaultNowTakenByAnotherCommand_IsRefused()
    {
        var vm = HotkeyDialog(new AppSettings());

        vm.SelectedBinding = Row(vm, "ClipboardCut");
        vm.EditGesture = "Ctrl+Shift+Z";
        vm.ApplyHotkeyCommand.Execute(null);

        vm.SelectedBinding = Row(vm, "Search");
        vm.EditGesture = "Ctrl+X";
        vm.ApplyHotkeyCommand.Execute(null);

        vm.SelectedBinding = Row(vm, "ClipboardCut");
        vm.ResetHotkeyCommand.Execute(null);

        vm.HasError.Should().BeTrue();
        Row(vm, "ClipboardCut").DisplayText.Should().Be("Ctrl+Shift+Z");
    }

    [Fact]
    public void ImportFrom_ClashingGesture_IsSkippedButSwapsAreKept()
    {
        var vm = HotkeyDialog(new AppSettings());
        var profile = Path.Combine(_dir, "profile.json");
        File.WriteAllText(profile, """
            {
              "ClipboardCopy": "Ctrl+X",
              "ClipboardCut": "Ctrl+C",
              "Search": "F5"
            }
            """);

        vm.ImportFrom(profile);

        Row(vm, "ClipboardCopy").DisplayText.Should().Be("Ctrl+X");
        Row(vm, "ClipboardCut").DisplayText.Should().Be("Ctrl+C");
        Row(vm, "Search").DisplayText.Should().Be("Ctrl+F");   // F5 belongs to Copy
        vm.HasError.Should().BeTrue();
    }

    // ── Workspace notes dialog ────────────────────────────────────────────────

    [Fact]
    public void NotesDialog_IsDirty_OnlyAfterAnEdit()
    {
        var vm = new WorkspaceNotesDialogViewModel("ws", "notes", "p.md");
        vm.IsDirty.Should().BeFalse();

        vm.Notes = "notes and more";

        vm.IsDirty.Should().BeTrue();
    }

    [Fact]
    public void NotesDialog_Confirm_HandsBackTheTrimmedNotes()
    {
        var vm = new WorkspaceNotesDialogViewModel("ws", string.Empty, "p.md") { Notes = "  todo: ship  " };

        vm.ConfirmCommand.Execute(null);

        vm.Result.Should().Be("todo: ship");
    }
}
