using FluentAssertions;
using TCV.App.Navigation;
using TCV.App.Settings;
using Xunit;

namespace TCV.App.Tests;

/// <summary>
/// ROADMAP 6.53: «run an agent on a task» preselected the first agent found, whatever the default
/// said. The configured default now wins; a stand-in is chosen only when there is no default or it
/// cannot be started, and then the choice says so.
/// </summary>
public class ExternalToolChoiceTests
{
    private static readonly ExternalTool Claude = new() { Name = "Claude Code", Command = "claude", InTerminal = true };
    private static readonly ExternalTool Codex = new() { Name = "Codex", Command = "codex", InTerminal = true };
    private static readonly ExternalTool Aider = new() { Name = "Aider", Command = "aider", InTerminal = true };

    private static Func<ExternalTool, bool> Installed(params ExternalTool[] tools) => tools.Contains;

    [Fact]
    public void Choose_ConfiguredDefaultIsInstalled_TakesItEvenWhenItIsNotFirst()
    {
        var choice = ExternalToolChoice.Choose([Claude, Codex], "Codex", Installed(Claude, Codex));

        choice.Tool.Should().BeSameAs(Codex);
        choice.Kind.Should().Be(ExternalToolChoiceKind.Default);
    }

    [Fact]
    public void Choose_DefaultMatchesIgnoringCaseAndSpaces()
    {
        var choice = ExternalToolChoice.Choose([Claude, Codex], "  codex ", Installed(Claude, Codex));

        choice.Tool.Should().BeSameAs(Codex);
    }

    [Fact]
    public void Choose_NoDefaultConfigured_TakesTheFirstInstalled()
    {
        var choice = ExternalToolChoice.Choose([Claude, Codex], "", Installed(Codex));

        choice.Tool.Should().BeSameAs(Codex);
        choice.Kind.Should().Be(ExternalToolChoiceKind.FirstDetected);
    }

    [Fact]
    public void Choose_DefaultNotInstalled_FallsBackToTheFirstInstalledAndSaysSo()
    {
        var choice = ExternalToolChoice.Choose([Claude, Codex, Aider], "Claude Code", Installed(Aider));

        choice.Tool.Should().BeSameAs(Aider);
        choice.Kind.Should().Be(ExternalToolChoiceKind.DefaultUnavailable);
        choice.PreferredName.Should().Be("Claude Code");
    }

    [Fact]
    public void Choose_DefaultNoLongerListed_FallsBackAndSaysSo()
    {
        var choice = ExternalToolChoice.Choose([Claude, Codex], "Gemini CLI", Installed(Claude, Codex));

        choice.Tool.Should().BeSameAs(Claude);
        choice.Kind.Should().Be(ExternalToolChoiceKind.DefaultUnavailable);
    }

    [Fact]
    public void Choose_NothingInstalled_KeepsTheConfiguredDefaultSoTheLaunchReportsIt()
    {
        var choice = ExternalToolChoice.Choose([Claude, Codex], "Codex", Installed());

        choice.Tool.Should().BeSameAs(Codex);
        choice.Kind.Should().Be(ExternalToolChoiceKind.Default);
    }

    [Fact]
    public void Choose_NoTools_ChoosesNothing()
    {
        ExternalToolChoice.Choose([], "Codex", Installed()).Tool.Should().BeNull();
    }
}
