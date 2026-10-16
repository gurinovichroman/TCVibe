using TCV.App.Settings;

namespace TCV.App.Navigation;

/// <summary>How <see cref="ExternalToolChoice.Choose"/> arrived at its tool.</summary>
public enum ExternalToolChoiceKind
{
    /// <summary>The configured default, which is installed.</summary>
    Default,
    /// <summary>No default is configured; the first installed tool is taken, as documented.</summary>
    FirstDetected,
    /// <summary>A default is configured but is not installed or no longer listed; another tool stands
    /// in for it, and the user should be told so.</summary>
    DefaultUnavailable,
}

/// <summary>Which external tool a launch preselects, and why.</summary>
/// <param name="Tool">The tool; null only when there is none to offer.</param>
/// <param name="Kind">Whether it is the configured default or a stand-in.</param>
/// <param name="PreferredName">The configured default's name as stored in settings, trimmed.</param>
public sealed record ExternalToolChoice(ExternalTool? Tool, ExternalToolChoiceKind Kind, string PreferredName)
{
    /// <summary>
    /// Picks the tool a launch should start with: the configured default when it is listed and
    /// installed, otherwise the first installed tool. Pure — the installation probe is injected,
    /// because the real one walks <c>PATH</c> (and must not run on the UI thread, INV-06).
    /// <para>
    /// The agent launcher used to take the first tool in the list whatever the default said
    /// (ROADMAP 6.53). A configured default that cannot be used is reported as
    /// <see cref="ExternalToolChoiceKind.DefaultUnavailable"/> rather than replaced silently: the user
    /// asked for a specific agent and should know they are getting another one.
    /// </para>
    /// </summary>
    /// <param name="tools">Tools on offer, in the order they are shown.</param>
    /// <param name="preferredName">The configured default's name; empty means none is configured.</param>
    /// <param name="isInstalled">Whether a tool's command resolves on this machine.</param>
    public static ExternalToolChoice Choose(
        IReadOnlyList<ExternalTool> tools, string? preferredName, Func<ExternalTool, bool> isInstalled)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(isInstalled);

        var name = preferredName?.Trim() ?? string.Empty;
        if (tools.Count == 0)
        {
            return new ExternalToolChoice(
                null, name.Length == 0 ? ExternalToolChoiceKind.FirstDetected : ExternalToolChoiceKind.DefaultUnavailable, name);
        }

        var preferred = name.Length == 0
            ? null
            : tools.FirstOrDefault(t => string.Equals(t.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));

        if (preferred is not null && isInstalled(preferred))
        {
            return new ExternalToolChoice(preferred, ExternalToolChoiceKind.Default, name);
        }

        var firstInstalled = tools.FirstOrDefault(t => !ReferenceEquals(t, preferred) && isInstalled(t));

        if (name.Length == 0)
        {
            return new ExternalToolChoice(firstInstalled ?? tools[0], ExternalToolChoiceKind.FirstDetected, name);
        }

        if (firstInstalled is not null)
        {
            return new ExternalToolChoice(firstInstalled, ExternalToolChoiceKind.DefaultUnavailable, name);
        }

        // Nothing at all resolves. Keep what the user asked for: starting it reports a real failure
        // naming the command, which says more than quietly offering another tool that fails too.
        return preferred is not null
            ? new ExternalToolChoice(preferred, ExternalToolChoiceKind.Default, name)
            : new ExternalToolChoice(tools[0], ExternalToolChoiceKind.DefaultUnavailable, name);
    }
}
