using System.Windows.Input;
using FluentAssertions;
using TCV.App.Settings;

namespace TCV.App.Tests;

/// <summary>Covers the two-stroke chord gestures added in task 6.20.</summary>
public class HotkeyChordTests
{
    private static HotkeyManager Manager(params (string Command, string Gesture)[] overrides)
    {
        var settings = new AppSettings();
        foreach (var (command, gesture) in overrides) settings.Hotkeys[command] = gesture;
        return new HotkeyManager(settings);
    }

    // ── Parsing ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Ctrl+S", Key.S, ModifierKeys.Control)]
    [InlineData("ctrl+shift+K", Key.K, ModifierKeys.Control | ModifierKeys.Shift)]
    [InlineData("F5", Key.F5, ModifierKeys.None)]
    public void KeyStroke_TryParse_ReadsModifiersAndKey(string text, Key key, ModifierKeys modifiers)
    {
        KeyStroke.TryParse(text, out var stroke).Should().BeTrue();
        stroke.Should().Be(new KeyStroke(key, modifiers));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+NotAKey")]
    public void KeyStroke_TryParse_RejectsNonsense(string text) =>
        KeyStroke.TryParse(text, out _).Should().BeFalse();

    [Fact]
    public void KeyStroke_ToString_RoundTrips()
    {
        var stroke = new KeyStroke(Key.K, ModifierKeys.Control | ModifierKeys.Shift);
        KeyStroke.TryParse(stroke.ToString(), out var parsed).Should().BeTrue();
        parsed.Should().Be(stroke);
    }

    [Fact]
    public void Binding_ToString_SpellsAChordWithASpace()
    {
        var binding = new HotkeyBinding("X",
            new KeyStroke(Key.K, ModifierKeys.Control), new KeyStroke(Key.S, ModifierKeys.Control));

        binding.ToString().Should().Be("Ctrl+K Ctrl+S");
        binding.IsChord.Should().BeTrue();
    }

    // ── Built-in chords ───────────────────────────────────────────────────────

    [Fact]
    public void Defaults_ColumnSettings_IsAChordUnderCtrlK()
    {
        var binding = Manager().GetBinding("ColumnSettings");

        binding.Should().NotBeNull();
        binding!.IsChord.Should().BeTrue();
        binding.First.Should().Be(new KeyStroke(Key.K, ModifierKeys.Control));
    }

    [Fact]
    public void IsChordPrefix_CtrlK_OpensASequence()
    {
        Manager().IsChordPrefix(Key.K, ModifierKeys.Control).Should().BeTrue();
        Manager().IsChordPrefix(Key.J, ModifierKeys.Control).Should().BeFalse();
    }

    [Fact]
    public void GetChordCommandId_ResolvesTheFullSequence()
    {
        Manager().GetChordCommandId(Key.K, ModifierKeys.Control, Key.C, ModifierKeys.Control)
            .Should().Be("ColumnSettings");
    }

    [Fact]
    public void GetChordCommandId_UnboundSecondStroke_IsNull()
    {
        Manager().GetChordCommandId(Key.K, ModifierKeys.Control, Key.Z, ModifierKeys.Control)
            .Should().BeNull();
    }

    [Fact]
    public void GetCommandId_ChordPrefix_ResolvesToNothingOnItsOwn()
    {
        // Otherwise the first press would run a command and the second stroke would arrive too late.
        Manager().GetCommandId(Key.K, ModifierKeys.Control).Should().BeNull();
    }

    // ── User overrides ────────────────────────────────────────────────────────

    [Fact]
    public void Override_CanTurnAPlainGestureIntoAChord()
    {
        var manager = Manager(("Search", "Ctrl+K Ctrl+F"));

        manager.GetBinding("Search")!.IsChord.Should().BeTrue();
        manager.GetChordCommandId(Key.K, ModifierKeys.Control, Key.F, ModifierKeys.Control)
            .Should().Be("Search");
        // Its old single gesture is gone with it.
        manager.GetCommandId(Key.F, ModifierKeys.Control).Should().NotBe("Search");
    }

    [Fact]
    public void Override_CanTurnAChordBackIntoAPlainGesture()
    {
        var manager = Manager(("ColumnSettings", "Ctrl+Shift+K"));

        manager.GetBinding("ColumnSettings")!.IsChord.Should().BeFalse();
        manager.GetCommandId(Key.K, ModifierKeys.Control | ModifierKeys.Shift).Should().Be("ColumnSettings");
    }

    [Fact]
    public void Override_ThreeStrokes_IsRejectedAndTheDefaultSurvives()
    {
        // The dispatcher holds one pending stroke; a three-key gesture could never fire.
        var manager = Manager(("ColumnSettings", "Ctrl+K Ctrl+C Ctrl+X"));

        manager.GetBinding("ColumnSettings")!.ToString().Should().Be("Ctrl+K Ctrl+C");
    }

    [Fact]
    public void Override_MalformedChord_IsRejected()
    {
        var manager = Manager(("ColumnSettings", "Ctrl+K Ctrl+Nonsense"));

        manager.GetBinding("ColumnSettings")!.ToString().Should().Be("Ctrl+K Ctrl+C");
    }

    [Fact]
    public void Defaults_NoTwoChordsShareTheSameSequence()
    {
        var manager = Manager();
        var chords = manager.AllBindings.Values.Where(b => b.IsChord).ToList();

        chords.Select(b => b.ToString()).Should().OnlyHaveUniqueItems();
        // Every one of them resolves back to its own command.
        foreach (var chord in chords)
        {
            manager.GetChordCommandId(
                chord.First.Key, chord.First.Modifiers,
                chord.Second!.Value.Key, chord.Second.Value.Modifiers)
                .Should().Be(chord.CommandId);
        }
    }
}
