namespace TCV.Network;

/// <summary>The connections opened most recently, for the command palette (6.82).</summary>
public static class RecentConnections
{
    /// <summary>A handful: the palette is for commands, the manager has the whole book.</summary>
    public const int PaletteCount = 5;

    /// <summary>
    /// The prefix of a recent connection's palette id, <c>Connection:&lt;id&gt;</c>. Like a user tool's
    /// (TRAP-21) it is resolved where the static command ids end, not registered as a command.
    /// </summary>
    public const string CommandPrefix = "Connection:";

    /// <summary>Up to <paramref name="count"/> connections that were ever opened, the most recent first.</summary>
    public static IReadOnlyList<ConnectionInfo> Pick(IEnumerable<ConnectionInfo> connections, int count = PaletteCount)
    {
        ArgumentNullException.ThrowIfNull(connections);
        return connections
            .Where(c => c.LastUsed is not null)
            .OrderByDescending(c => c.LastUsed)
            .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(Math.Max(0, count))
            .ToList();
    }

    /// <summary>The palette id of a connection.</summary>
    public static string CommandId(ConnectionInfo connection) => CommandPrefix + connection.Id.ToString("D");

    /// <summary>The connection id in a palette id, or null when it is no connection's.</summary>
    public static Guid? ParseCommandId(string commandId) =>
        commandId.StartsWith(CommandPrefix, StringComparison.Ordinal)
        && Guid.TryParse(commandId.AsSpan(CommandPrefix.Length), out var id)
            ? id
            : null;
}
