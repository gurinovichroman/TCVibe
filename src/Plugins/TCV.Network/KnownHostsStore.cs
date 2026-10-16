using System.Text.Json;
using TCV.Core.Domain;

namespace TCV.Network;

/// <summary>What the store knows about a server's identity.</summary>
public enum HostTrustState
{
    /// <summary>Never seen: the user has to decide whether to trust it.</summary>
    Unknown,

    /// <summary>Seen before with exactly this fingerprint.</summary>
    Trusted,

    /// <summary>Seen before with a <em>different</em> fingerprint — a changed key, or a man in the middle.</summary>
    Changed,
}

/// <summary>One remembered server identity.</summary>
public sealed class KnownHost
{
    /// <summary><c>ssh</c> (SFTP host key) or <c>tls</c> (FTPS certificate).</summary>
    public string Kind { get; set; } = "ssh";

    /// <summary>Lower-case <c>host:port</c>.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Key algorithm or certificate subject, for display.</summary>
    public string Algorithm { get; set; } = string.Empty;

    /// <summary><c>SHA256:</c> plus the unpadded base64 digest, the format <c>ssh-keygen -l</c> prints.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    public DateTime Added { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// The list of servers the user has vouched for, kept next to the exe (INV-03) as
/// <c>known-hosts.json</c>. This is what makes a host key or certificate mean something: the first
/// connection asks, every later one compares, and a mismatch is a hard stop.
/// </summary>
public sealed class KnownHostsStore
{
    public const string SshKind = "ssh";
    public const string TlsKind = "tls";

    private readonly string _path;
    private readonly object _lock = new();
    private List<KnownHost> _hosts = [];

    public KnownHostsStore() : this(global::TCV.Core.Domain.AppPaths.Data("known-hosts.json"))
    {
    }

    public KnownHostsStore(string path)
    {
        _path = path;
        Load();
    }

    /// <summary>Set when the file existed but could not be read; describes what was done about it.</summary>
    public string? LoadError { get; private set; }

    public IReadOnlyList<KnownHost> Hosts
    {
        get
        {
            lock (_lock)
            {
                return _hosts.ToList();
            }
        }
    }

    public static string HostKey(string host, int port) => $"{host.Trim().ToLowerInvariant()}:{port}";

    public HostTrustState Check(string kind, string hostKey, string fingerprint)
    {
        lock (_lock)
        {
            var known = _hosts.FirstOrDefault(h => h.Kind == kind && h.Host == hostKey);
            if (known is null)
            {
                return HostTrustState.Unknown;
            }

            return string.Equals(known.Fingerprint, fingerprint, StringComparison.Ordinal)
                ? HostTrustState.Trusted
                : HostTrustState.Changed;
        }
    }

    /// <summary>The fingerprint on file for a host, or null.</summary>
    public string? Lookup(string kind, string hostKey)
    {
        lock (_lock)
        {
            return _hosts.FirstOrDefault(h => h.Kind == kind && h.Host == hostKey)?.Fingerprint;
        }
    }

    /// <summary>Remembers (or replaces) the identity of a server the user has just approved.</summary>
    public void Trust(string kind, string hostKey, string algorithm, string fingerprint)
    {
        lock (_lock)
        {
            _hosts.RemoveAll(h => h.Kind == kind && h.Host == hostKey);
            _hosts.Add(new KnownHost { Kind = kind, Host = hostKey, Algorithm = algorithm, Fingerprint = fingerprint });
            Save();
        }
    }

    /// <summary>Forgets a server, so the next connection asks again (the way out after a legitimate key change).</summary>
    public void Forget(string kind, string hostKey)
    {
        lock (_lock)
        {
            if (_hosts.RemoveAll(h => h.Kind == kind && h.Host == hostKey) > 0)
            {
                Save();
            }
        }
    }

    private void Load()
    {
        try
        {
            var json = AtomicFile.ReadAllTextOrNull(_path);
            if (json is null)
            {
                return;
            }

            _hosts = JsonSerializer.Deserialize<List<KnownHost>>(json) ?? [];
        }
        catch (JsonException ex)
        {
            // An unreadable list must not silently become "trust nobody, ask again" without a trace:
            // keep the file, start empty, and say so.
            var copy = AtomicFile.PreserveCorrupt(_path);
            LoadError = $"{Path.GetFileName(_path)} could not be read ({ex.Message}). "
                        + (copy is null ? "" : $"A copy was kept as {Path.GetFileName(copy)}.");
            _hosts = [];
        }
        catch (IOException ex)
        {
            LoadError = $"{Path.GetFileName(_path)} is locked ({ex.Message}); saved hosts are unavailable.";
            _hosts = [];
        }
    }

    private void Save() =>
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_hosts, new JsonSerializerOptions { WriteIndented = true }));
}
