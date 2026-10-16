using TCV.Ai.Mcp;

namespace TCV.App.Agents;

/// <summary>What a panel has mounted, as far as the agent's access rule is concerned.</summary>
public enum AgentMountKind
{
    /// <summary>An archive browsed as a directory tree.</summary>
    Archive,

    /// <summary>A server connection (SFTP, FTP, WebDAV) or any other mount that is not an archive.</summary>
    Remote,
}

/// <summary>A panel's mount described for <see cref="PanelMountAccess"/>.</summary>
/// <param name="ArchiveFile">The archive's file on disk; empty when it is not known.</param>
public sealed record AgentMountInfo(AgentMountKind Kind, string ArchiveFile = "");

/// <summary>
/// Whether an agent may read what a panel has mounted through <c>panel://</c> (6.34, ADR-0023).
/// <para>
/// An open mount used to be the permission by itself, so an agent read any archive or server in front
/// of the user even with no directory approved. Now an archive follows the same rule as
/// <c>zip://</c>: its file has to lie inside an approved directory. A server has no disk path to
/// check, so reading one is a separate switch (<c>AgentSettings.McpAllowRemotePanels</c>), off by default.
/// </para>
/// </summary>
public static class PanelMountAccess
{
    private const string ArchiveHint =
        "panel:// reads an archive only when its file lies inside an approved directory; "
        + "the user can approve the folder that holds it.";

    /// <summary>Null when the agent may read the mount; otherwise the refusal to hand back.</summary>
    /// <param name="panel">«active» or «opposite», for the message.</param>
    public static McpError? Check(AgentMountInfo mount, PathScope scope, bool remoteAllowed, string panel)
    {
        if (mount.Kind == AgentMountKind.Remote)
        {
            return remoteAllowed
                ? null
                : new McpError(
                    McpError.OutsideScope,
                    $"Reading the connection open in the {panel} panel is not allowed.",
                    "The user can allow agents to read server connections open in the panels in "
                    + "Settings → Agent layer.");
        }

        // An archive whose file is unknown (one opened from inside another archive lives in a temporary
        // folder) cannot be checked, so it is refused rather than waved through.
        if (mount.ArchiveFile.Length > 0)
        {
            var verdict = scope.Check(mount.ArchiveFile);
            if (verdict.IsAllowed)
            {
                return null;
            }

            if (verdict.Verdict == ScopeVerdict.NoScope)
            {
                return new McpError(
                    McpError.NoScope,
                    $"The archive open in the {panel} panel cannot be read: no directory is approved yet.",
                    ArchiveHint);
            }
        }

        // The archive's own path is not named: the agent is not told where the things it may not read are.
        return new McpError(
            McpError.OutsideScope,
            $"The archive open in the {panel} panel is outside the approved directories.",
            ArchiveHint);
    }
}
