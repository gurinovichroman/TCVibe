namespace TCV.Network;

/// <summary>Something the user should know about a host before importing it.</summary>
public enum SshImportNote
{
    /// <summary>No <c>User</c>: the local user name is used, as ssh would.</summary>
    LocalUser,

    /// <summary>No <c>IdentityFile</c>: one of the default keys in <c>~/.ssh</c> is used, as ssh would.</summary>
    DefaultKey,

    /// <summary>The <c>IdentityFile</c> named is not on this computer; the path is kept.</summary>
    KeyFileMissing,

    /// <summary>No key at all: the connection asks for a password.</summary>
    NoKey,

    /// <summary>A <c>ProxyJump</c> or <c>ProxyCommand</c> applies, which is not supported: the connection goes direct.</summary>
    ProxyIgnored,
}

/// <summary>A host of <c>~/.ssh/config</c> as the SFTP connection it would become.</summary>
/// <param name="Host">What the configuration says.</param>
/// <param name="Connection">The connection to add (a new id, nothing saved yet).</param>
/// <param name="DuplicateOf">The name of a saved connection (or of an earlier host here) to the same login, or null.</param>
/// <param name="Notes">What the user should know about it.</param>
public sealed record SshImportCandidate(
    SshConfigHost Host,
    ConnectionInfo Connection,
    string? DuplicateOf,
    IReadOnlyList<SshImportNote> Notes)
{
    public bool IsDuplicate => DuplicateOf is not null;
}

/// <summary>
/// Turns the hosts of <c>~/.ssh/config</c> into SFTP connections (6.82): the alias is the name, the
/// gaps are filled the way ssh fills them (local user name, port 22, a default key), and a host that
/// is already saved — same server, port and login — is flagged so it is not added twice. Pure: the
/// file checks and the user's folder come in as arguments.
/// </summary>
public static class SshConfigImport
{
    /// <summary>The keys ssh tries when the configuration names none, in its order; those SSH.NET can read.</summary>
    public static IReadOnlyList<string> DefaultKeyNames { get; } = ["id_rsa", "id_ecdsa", "id_ed25519"];

    /// <param name="hosts">The hosts of the configuration.</param>
    /// <param name="existing">The saved connections.</param>
    /// <param name="homeDirectory">The user's folder (<c>~</c>, <c>%d</c>).</param>
    /// <param name="localUser">The Windows user name (<c>%u</c>, and the login when none is set).</param>
    /// <param name="fileExists">Whether a key file is there.</param>
    public static IReadOnlyList<SshImportCandidate> Plan(
        IEnumerable<SshConfigHost> hosts,
        IEnumerable<ConnectionInfo> existing,
        string homeDirectory,
        string localUser,
        Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(fileExists);

        var saved = existing.Where(c => c.Protocol == NetworkProtocol.Sftp).ToList();
        var planned = new List<SshImportCandidate>();
        foreach (var host in hosts)
        {
            var notes = new List<SshImportNote>();
            var server = string.IsNullOrWhiteSpace(host.HostName) ? host.Alias : host.HostName.Trim();
            var port = host.Port ?? 22;
            var user = host.User;
            if (string.IsNullOrWhiteSpace(user))
            {
                user = localUser;
                notes.Add(SshImportNote.LocalUser);
            }

            var connection = new ConnectionInfo
            {
                Name = host.Alias,
                Protocol = NetworkProtocol.Sftp,
                Host = server,
                Port = port,
                Username = user,
            };

            var keys = host.IdentityFiles
                .Select(file => ExpandPath(file, homeDirectory, localUser, server, user, port, host.Alias))
                .ToList();
            var key = keys.FirstOrDefault(fileExists);
            if (key is null && keys.Count > 0)
            {
                key = keys[0];
                notes.Add(SshImportNote.KeyFileMissing);
            }
            else if (key is null)
            {
                key = DefaultKeyNames
                    .Select(name => Path.Combine(homeDirectory, ".ssh", name))
                    .FirstOrDefault(fileExists);
                notes.Add(key is null ? SshImportNote.NoKey : SshImportNote.DefaultKey);
            }

            if (key is not null)
            {
                connection.AuthMethod = SshAuthMethod.KeyFile;
                connection.KeyFilePath = key;
            }

            if (host.ProxyJump is not null || host.HasProxyCommand)
            {
                notes.Add(SshImportNote.ProxyIgnored);
            }

            var duplicate = saved.FirstOrDefault(c => SameLogin(c, connection))?.Name
                ?? planned.FirstOrDefault(p => SameLogin(p.Connection, connection))?.Host.Alias;
            planned.Add(new SshImportCandidate(host, connection, duplicate, notes));
        }

        return planned;
    }

    /// <summary>The same server, port and login: connecting to either opens the same session.</summary>
    public static bool SameLogin(ConnectionInfo a, ConnectionInfo b) =>
        string.Equals(a.Host.Trim(), b.Host.Trim(), StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port
        && string.Equals(a.Username, b.Username, StringComparison.Ordinal);

    /// <summary>
    /// A path of the configuration as a Windows path: <c>~</c> and <c>%d</c> are the user's folder,
    /// <c>%u</c> the local user, <c>%h</c> the server, <c>%r</c> the login, <c>%p</c> the port,
    /// <c>%n</c> the alias, <c>%%</c> a percent sign; forward slashes become backslashes.
    /// </summary>
    public static string ExpandPath(string path, string homeDirectory, string localUser, string server, string user, int port, string alias)
    {
        var expanded = path.Trim();
        if (expanded.StartsWith('~'))
        {
            expanded = homeDirectory.TrimEnd('\\', '/') + expanded[1..];
        }

        expanded = expanded
            .Replace("%%", "\0", StringComparison.Ordinal)
            .Replace("%d", homeDirectory.TrimEnd('\\', '/'), StringComparison.Ordinal)
            .Replace("%u", localUser, StringComparison.Ordinal)
            .Replace("%h", server, StringComparison.Ordinal)
            .Replace("%r", user, StringComparison.Ordinal)
            .Replace("%p", port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("%n", alias, StringComparison.Ordinal)
            .Replace('\0', '%');
        return expanded.Replace('/', '\\');
    }
}
