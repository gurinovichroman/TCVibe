using FluentAssertions;
using TCV.Ai.Llm;

namespace TCV.Ai.Tests;

/// <summary>
/// The model timeout's bounds (6.55): the settings dialog refuses what is outside them, and the client
/// clamps whatever a hand-edited settings.json holds.
/// </summary>
public class LlmTimeoutTests
{
    [Theory]
    [InlineData(5)]
    [InlineData(60)]
    [InlineData(600)]
    public void IsValidTimeout_WithinBounds_IsTrue(int seconds)
    {
        LlmOptions.IsValidTimeout(seconds).Should().BeTrue();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(601)]
    [InlineData(int.MaxValue)]
    public void IsValidTimeout_OutsideBounds_IsFalse(int seconds)
    {
        LlmOptions.IsValidTimeout(seconds).Should().BeFalse();
    }

    [Theory]
    [InlineData(int.MinValue, 5)]
    [InlineData(0, 5)]
    [InlineData(5, 5)]
    [InlineData(90, 90)]
    [InlineData(600, 600)]
    [InlineData(86_400, 600)]
    public void ClampTimeout_AnyNumber_LandsWithinBounds(int seconds, int expected)
    {
        LlmOptions.ClampTimeout(seconds).Should().Be(expected);
    }

    [Fact]
    public void Bounds_DefaultTimeout_IsInside()
    {
        LlmOptions.IsValidTimeout(new LlmOptions("e", "k", "m", "x").TimeoutSeconds).Should().BeTrue();
    }
}
