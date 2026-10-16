using System.IO;
using TCV.Core.Domain;

namespace TCV.App.Settings;

/// <summary>
/// Command-line options for one run. Mostly "open this workspace", which is how a
/// second window is opened (6.6): a separate process, not a second <c>MainWindow</c>, because
/// dialog ownership goes through <c>Application.Current.MainWindow</c> — two windows in one process
/// would hand every dialog to whichever opened first.
/// </summary>
public sealed class StartupOptions
{
    /// <summary>Workspace to restore instead of the last session, or null for a normal launch.</summary>
    public string? WorkspaceName { get; init; }

    /// <summary>
    /// True when this process was launched to show one workspace. Such a window must not write
    /// <c>settings.json</c> or <c>workspaces.json</c>: the primary window owns the session, and a
    /// whole-file rewrite from a stale in-memory list would drop workspaces saved meanwhile.
    /// </summary>
    public bool IsSecondaryWindow => WorkspaceName is not null && !_downgraded;

    private bool _downgraded;

    /// <summary>
    /// Called when the requested workspace turned out not to exist: the process then runs an
    /// ordinary session, so everything that asks "is this a guest window?" (settings writes, the
    /// MCP server) has to hear the same answer.
    /// </summary>
    public void DowngradeToPrimary() => _downgraded = true;

    /// <summary>
    /// Full path of the state folder given with <c>--data</c>, or null to let the layout choose it
    /// (ADR-0031). Lets a debug build run against a portable copy's <c>data\</c> or a sandbox.
    /// </summary>
    public string? DataDirectory { get; init; }

    public static StartupOptions Default => new();

    /// <summary>The one short flag; every other flag starts with <c>--</c>.</summary>
    private const string WorkspaceShortFlag = "-w";

    /// <summary>
    /// Parses <c>--workspace &lt;name&gt;</c> (also <c>-w</c> and <c>--workspace=name</c>) and
    /// <c>--data &lt;folder&gt;</c> (also <c>--data=folder</c>); the first occurrence of each wins.
    /// Anything unrecognised is ignored — a file manager launched by the shell with stray
    /// arguments has to start, not fail.
    /// </summary>
    public static StartupOptions Parse(string[]? args)
    {
        if (args is null)
        {
            return Default;
        }

        string? workspace = null;
        string? data = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (TryReadValue(args, ref i, "--workspace", WorkspaceShortFlag) is { } name)
            {
                workspace ??= name;
            }
            else if (TryReadValue(args, ref i, "--data", null) is { } folder)
            {
                data ??= FullPath(folder);
            }
        }

        return new StartupOptions { WorkspaceName = workspace, DataDirectory = data };
    }

    /// <summary>
    /// The arguments of a guest window for <paramref name="workspace"/>. It must read the state folder
    /// this window reads, or a debug run with <c>--data</c> would open its guests on other state.
    /// </summary>
    public static string GuestArguments(string workspace, string? dataDirectory)
    {
        // The inline form keeps a name that starts with a dash from being read as a flag.
        var arguments = $"--workspace={ShellQuoting.QuoteArgument(workspace)}";
        return dataDirectory is null ? arguments : $"--data={ShellQuoting.QuoteArgument(dataDirectory)} {arguments}";
    }

    /// <summary>
    /// The value of <paramref name="flag"/> when <c>args[i]</c> is it: inline (<c>--flag=value</c>) or
    /// the next argument, which is then consumed. Null when <c>args[i]</c> is another argument or the
    /// flag has no usable value.
    /// </summary>
    private static string? TryReadValue(string[] args, ref int i, string flag, string? shortFlag)
    {
        var arg = args[i];
        if (arg.StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase))
        {
            var inline = arg[(flag.Length + 1)..].Trim().Trim('"');
            return inline.Length == 0 ? null : inline;
        }

        if (!IsFlag(arg, flag, shortFlag))
        {
            return null;
        }

        // "--workspace" with nothing after it (or followed by another long flag) is a typo, not a
        // request to open a workspace called "--theme". A single dash is allowed: "-x" is a
        // legitimate workspace name (the app itself launches guests in the inline form) — but not
        // "-w", which is a flag of its own: "--data -w api" must not store state in a folder "-w".
        if (i + 1 >= args.Length)
        {
            return null;
        }

        var value = args[i + 1];
        if (value.StartsWith("--", StringComparison.Ordinal)
            || value.Equals(WorkspaceShortFlag, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        i++;
        return value.Trim();
    }

    private static bool IsFlag(string arg, string flag, string? shortFlag) =>
        arg.Equals(flag, StringComparison.OrdinalIgnoreCase)
        || (shortFlag is not null && arg.Equals(shortFlag, StringComparison.OrdinalIgnoreCase));

    /// <summary>A relative folder is taken from the working directory; a malformed one is ignored, like any stray argument.</summary>
    private static string? FullPath(string folder)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
