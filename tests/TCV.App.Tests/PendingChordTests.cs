using System.Windows.Input;
using FluentAssertions;
using TCV.App.Settings;

namespace TCV.App.Tests;

public class PendingChordTests
{
    private static readonly DateTime Now = new(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);
    private static readonly KeyStroke CtrlK = new(Key.K, ModifierKeys.Control);

    [Fact]
    public void Take_WithinTheTimeout_ReturnsTheFirstStrokeOnce()
    {
        var chord = new PendingChord();
        chord.Begin(CtrlK, Now);

        chord.Take(Now.AddSeconds(1)).Should().Be(CtrlK);
        chord.Take(Now.AddSeconds(1)).Should().BeNull("a chord is consumed by its second stroke");
    }

    [Fact]
    public void Take_AfterTheTimeout_ReturnsNothing_SoTheNextKeyIsNotSwallowed()
    {
        var chord = new PendingChord();
        chord.Begin(CtrlK, Now);

        chord.Take(Now + PendingChord.DefaultTimeout + TimeSpan.FromMilliseconds(1)).Should().BeNull();
    }

    [Fact]
    public void Clear_DropsAPendingStroke()
    {
        var chord = new PendingChord();
        chord.Begin(CtrlK, Now);

        chord.Clear();

        chord.Peek(Now).Should().BeNull();
    }

    [Fact]
    public void Peek_DoesNotConsume()
    {
        var chord = new PendingChord();
        chord.Begin(CtrlK, Now);

        chord.Peek(Now).Should().Be(CtrlK);
        chord.Peek(Now).Should().Be(CtrlK);
    }

    [Fact]
    public void Begin_Twice_KeepsOnlyTheLatestStroke()
    {
        var chord = new PendingChord();
        chord.Begin(CtrlK, Now);
        chord.Begin(new KeyStroke(Key.E, ModifierKeys.Control), Now);

        chord.Take(Now)!.Value.Key.Should().Be(Key.E);
    }

    [Fact]
    public void NothingPending_TakeIsNull()
    {
        new PendingChord().Take(Now).Should().BeNull();
    }
}
