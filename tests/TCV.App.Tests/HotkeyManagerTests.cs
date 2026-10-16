using System.Windows.Input;
using FluentAssertions;
using TCV.App.Settings;

namespace TCV.App.Tests;

public class HotkeyManagerTests
{
    private static HotkeyManager Create(params (string CommandId, string Gesture)[] overrides)
    {
        var settings = new AppSettings();
        foreach (var (commandId, gesture) in overrides)
            settings.Hotkeys[commandId] = gesture;
        return new HotkeyManager(settings);
    }

    [Theory]
    [InlineData(Key.F5, ModifierKeys.None, "Copy")]
    [InlineData(Key.F6, ModifierKeys.None, "Move")]
    [InlineData(Key.F7, ModifierKeys.None, "NewFolder")]
    [InlineData(Key.F8, ModifierKeys.None, "Delete")]
    [InlineData(Key.F2, ModifierKeys.None, "Rename")]
    [InlineData(Key.F3, ModifierKeys.None, "View")]
    [InlineData(Key.F4, ModifierKeys.None, "Edit")]
    public void DefaultFunctionKeys_ResolveToTcCommands(Key key, ModifierKeys mods, string expected)
    {
        Create().GetCommandId(key, mods).Should().Be(expected);
    }

    [Theory]
    [InlineData(Key.F5, "Pack")]
    [InlineData(Key.F9, "Unpack")]
    public void AltFunctionKeys_AreBoundToArchiveCommands(Key key, string expected)
    {
        Create().GetCommandId(key, ModifierKeys.Alt).Should().Be(expected);
    }

    [Fact]
    public void AltF5_DoesNotFallThroughToCopy()
    {
        var mgr = Create();
        mgr.GetCommandId(Key.F5, ModifierKeys.Alt).Should().Be("Pack");
        mgr.GetCommandId(Key.F5, ModifierKeys.None).Should().Be("Copy");
    }

    [Fact]
    public void DeleteKey_IsAliasedToDelete_AndShiftDeleteIsPermanent()
    {
        var mgr = Create();
        mgr.GetCommandId(Key.Delete, ModifierKeys.None).Should().Be("Delete");
        mgr.GetCommandId(Key.Delete, ModifierKeys.Shift).Should().Be("DeletePermanent");
    }

    [Fact]
    public void ShiftF6_IsAliasedToRename()
    {
        Create().GetCommandId(Key.F6, ModifierKeys.Shift).Should().Be("Rename");
    }

    [Fact]
    public void AltEnter_ShowsProperties_AndSettingsMovedToCtrlAltS()
    {
        var mgr = Create();
        mgr.GetCommandId(Key.Return, ModifierKeys.Alt).Should().Be("Properties");
        mgr.GetCommandId(Key.S, ModifierKeys.Control | ModifierKeys.Alt).Should().Be("Settings");
    }

    [Fact]
    public void NetworkCommands_HaveDistinctGestures()
    {
        var mgr = Create();
        mgr.GetCommandId(Key.F, ModifierKeys.Control | ModifierKeys.Shift).Should().Be("ConnectionManager");
        mgr.GetCommandId(Key.F, ModifierKeys.Control | ModifierKeys.Alt).Should().Be("Disconnect");
        mgr.GetCommandId(Key.F5, ModifierKeys.Shift).Should().Be("QueueTransfer");
        mgr.GetCommandId(Key.Q, ModifierKeys.Control | ModifierKeys.Shift).Should().Be("TransferQueue");
    }

    [Fact]
    public void CtrlP_OpensTheFilePicker_AndCopyPathMovedToCtrlShiftC()
    {
        // ADR-0014: Ctrl+P follows the editor world, not Total Commander
        var mgr = Create();
        mgr.GetCommandId(Key.P, ModifierKeys.Control).Should().Be("FindFiles");
        mgr.GetCommandId(Key.C, ModifierKeys.Control | ModifierKeys.Shift).Should().Be("CopyFullPath");
        mgr.GetCommandId(Key.P, ModifierKeys.Control | ModifierKeys.Shift).Should().Be("CopyNames");
    }

    [Fact]
    public void EditorAndAgentGestures_AreBoundAndDistinct()
    {
        var mgr = Create();
        mgr.GetCommandId(Key.E, ModifierKeys.Control).Should().Be("OpenInEditor");
        mgr.GetCommandId(Key.E, ModifierKeys.Control | ModifierKeys.Shift).Should().Be("OpenInEditorPicker");
        mgr.GetCommandId(Key.A, ModifierKeys.Control | ModifierKeys.Shift).Should().Be("RunAgentHere");
        mgr.GetCommandId(Key.A, ModifierKeys.Control | ModifierKeys.Alt).Should().Be("RunAgentPicker");
    }

    [Fact]
    public void EveryPaletteCommand_HasAUniqueId()
    {
        // The palette dispatches by id, and HotkeyManager keys bindings by id: a duplicate would
        // make one of the two entries unreachable.
        var ids = TCV.App.Commands.CommandCatalog.All.Select(c => c.Id).ToList();

        ids.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void UnboundGesture_ResolvesToNull()
    {
        Create().GetCommandId(Key.F12, ModifierKeys.Control | ModifierKeys.Shift).Should().BeNull();
    }

    [Fact]
    public void UserOverride_ReplacesDefaultGesture()
    {
        var mgr = Create(("Copy", "Ctrl+Shift+C"));

        mgr.GetCommandId(Key.C, ModifierKeys.Control | ModifierKeys.Shift).Should().Be("Copy");
        mgr.GetBinding("Copy")!.Key.Should().Be(Key.C);
        mgr.GetCommandId(Key.F5, ModifierKeys.None).Should().BeNull();
    }

    [Fact]
    public void UserOverride_WinsOverACollidingDefault()
    {
        // Ctrl+G is GitDiff by default; remapping Search onto it must take precedence
        var mgr = Create(("Search", "Ctrl+G"));

        mgr.GetCommandId(Key.G, ModifierKeys.Control).Should().Be("Search");
    }

    [Fact]
    public void MalformedOverride_IsIgnored_AndDefaultSurvives()
    {
        var mgr = Create(("Copy", "Ctrl+NotAKey"));

        mgr.GetCommandId(Key.F5, ModifierKeys.None).Should().Be("Copy");
    }

    [Fact]
    public void MalformedOverride_DoesNotOutrankAValidOneOnTheSameGesture()
    {
        // Copy's override does not parse, so Copy keeps its default F5 — but a command that
        // really was remapped onto F5 must still win it, whatever order settings.json lists.
        var mgr = Create(("Copy", "Ctrl+NotAKey"), ("Search", "F5"));

        mgr.GetCommandId(Key.F5, ModifierKeys.None).Should().Be("Search");
    }

    [Fact]
    public void ShiftEnter_StillShowsProperties()
    {
        // Properties moved to Alt+Enter; without this alias Shift+Enter fell through to the
        // file list and launched the file instead
        Create().GetCommandId(Key.Return, ModifierKeys.Shift).Should().Be("Properties");
    }

    [Fact]
    public void PlainSlash_IsUnbound_QuickSearchStartsByTyping()
    {
        Create().GetCommandId(Key.OemQuestion, ModifierKeys.None).Should().BeNull(
            "an inert binding in the dialog is worse than none");
    }

    [Fact]
    public void Reload_PicksUpSettingsChangedAfterConstruction()
    {
        var settings = new AppSettings();
        var mgr = new HotkeyManager(settings);
        mgr.GetCommandId(Key.F5, ModifierKeys.None).Should().Be("Copy");

        settings.Hotkeys["Copy"] = "Ctrl+F5";
        mgr.Reload();

        mgr.GetCommandId(Key.F5, ModifierKeys.Control).Should().Be("Copy");
        mgr.GetCommandId(Key.F5, ModifierKeys.None).Should().BeNull();
    }

    [Fact]
    public void EveryDefaultBinding_IsUniquelyResolvable()
    {
        var mgr = Create();

        foreach (var (commandId, binding) in mgr.AllBindings)
        {
            // A chord resolves on its second stroke; its first is only a prefix (6.20).
            if (binding.Second is { } second)
            {
                mgr.GetChordCommandId(
                    binding.First.Key, binding.First.Modifiers, second.Key, second.Modifiers)
                    .Should().Be(commandId,
                        "default chord {0} must not be shadowed by another command", binding);
                continue;
            }

            mgr.GetCommandId(binding.Key, binding.Modifiers).Should().Be(commandId,
                "default gesture {0} must not be shadowed by another command", binding);
        }
    }

    [Fact]
    public void SelectionKeys_MatchTheKeysFileListControlHandles()
    {
        var mgr = Create();
        mgr.GetCommandId(Key.Insert, ModifierKeys.None).Should().Be("SelectToggle");
        mgr.GetCommandId(Key.Space, ModifierKeys.None).Should().Be("SelectToggleSize");
        mgr.GetCommandId(Key.Add, ModifierKeys.None).Should().Be("SelectByMask");
        mgr.GetCommandId(Key.Subtract, ModifierKeys.None).Should().Be("UnselectByMask");
        mgr.GetCommandId(Key.Multiply, ModifierKeys.None).Should().Be("InvertSelection");
        mgr.GetCommandId(Key.Add, ModifierKeys.Control).Should().Be("SelectAll");
        mgr.GetCommandId(Key.A, ModifierKeys.Control).Should().Be("SelectAll");
        mgr.GetCommandId(Key.Subtract, ModifierKeys.Control).Should().Be("DeselectAll");
        mgr.GetCommandId(Key.Divide, ModifierKeys.None).Should().Be("DeselectAll");
    }

    [Fact]
    public void BindingToString_RendersModifiersInCanonicalOrder()
    {
        new HotkeyBinding("X", Key.Tab, ModifierKeys.Control | ModifierKeys.Shift)
            .ToString().Should().Be("Ctrl+Shift+Tab");
    }
}
