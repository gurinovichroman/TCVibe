using System.Security.Cryptography;

namespace TCV.Network;

/// <summary>What the user is asked to vouch for: who the server claims to be.</summary>
/// <param name="Kind"><see cref="KnownHostsStore.SshKind"/> or <see cref="KnownHostsStore.TlsKind"/>.</param>
/// <param name="Host"><c>host:port</c>.</param>
/// <param name="Algorithm">Key algorithm (SSH) or certificate subject (TLS).</param>
/// <param name="Fingerprint">SHA-256 fingerprint.</param>
/// <param name="Detail">Why a certificate failed validation (TLS), or empty.</param>
public sealed record HostTrustRequest(string Kind, string Host, string Algorithm, string Fingerprint, string Detail);

/// <summary>
/// Decides whether to talk to a server whose identity is not vouched for by the usual PKI.
/// The decision itself — trust on first use, hard stop on a change — lives here and is testable;
/// the actual question to the user is a delegate the application supplies.
/// </summary>
public sealed class HostTrustPolicy
{
    private readonly KnownHostsStore _store;
    private readonly Func<HostTrustRequest, Task<bool>> _askUser;

    public HostTrustPolicy(KnownHostsStore store, Func<HostTrustRequest, Task<bool>> askUser)
    {
        _store = store;
        _askUser = askUser;
    }

    /// <summary>The store behind this policy (the connection manager lists and forgets from it).</summary>
    public KnownHostsStore Store => _store;

    /// <summary>
    /// Verdict for an SSH host key. A key different from the remembered one is rejected without
    /// asking: that is exactly what a man-in-the-middle looks like, and a one-click "trust anyway"
    /// would make the check theatre. The user removes the old entry deliberately to accept a rekey.
    /// </summary>
    /// <exception cref="HostKeyChangedException">The remembered fingerprint differs.</exception>
    public bool VerifySshHostKey(string host, int port, string algorithm, byte[] hostKey)
    {
        var fingerprint = FingerprintOf(hostKey);
        var key = KnownHostsStore.HostKey(host, port);

        switch (_store.Check(KnownHostsStore.SshKind, key, fingerprint))
        {
            case HostTrustState.Trusted:
                return true;

            case HostTrustState.Changed:
                throw new HostKeyChangedException(key, _store.Lookup(KnownHostsStore.SshKind, key)!, fingerprint);

            default:
                if (!Ask(new HostTrustRequest(KnownHostsStore.SshKind, key, algorithm, fingerprint, string.Empty)))
                {
                    return false;
                }

                _store.Trust(KnownHostsStore.SshKind, key, algorithm, fingerprint);
                return true;
        }
    }

    /// <summary>
    /// Verdict for an FTPS certificate. A certificate that validates normally is accepted as is
    /// (renewals are routine); one that does not — self-signed, expired, wrong name — must be pinned
    /// by the user, and once pinned only that exact certificate is accepted.
    /// </summary>
    /// <param name="chainIsValid">The operating system trusts the certificate.</param>
    /// <param name="problem">Why it does not validate; shown to the user.</param>
    /// <exception cref="HostKeyChangedException">A pinned certificate was replaced by another untrusted one.</exception>
    public bool VerifyCertificate(string host, int port, string subject, byte[] certificateHash, bool chainIsValid, string problem)
    {
        if (chainIsValid)
        {
            return true;
        }

        var fingerprint = FingerprintOf(certificateHash, hashed: true);
        var key = KnownHostsStore.HostKey(host, port);

        switch (_store.Check(KnownHostsStore.TlsKind, key, fingerprint))
        {
            case HostTrustState.Trusted:
                return true;

            case HostTrustState.Changed:
                throw new HostKeyChangedException(key, _store.Lookup(KnownHostsStore.TlsKind, key)!, fingerprint);

            default:
                if (!Ask(new HostTrustRequest(KnownHostsStore.TlsKind, key, subject, fingerprint, problem)))
                {
                    return false;
                }

                _store.Trust(KnownHostsStore.TlsKind, key, subject, fingerprint);
                return true;
        }
    }

    /// <summary>
    /// <c>SHA256:</c> + unpadded base64 of the SHA-256 of the key blob — what OpenSSH prints — or,
    /// with <paramref name="hashed"/>, of an already-computed SHA-256.
    /// </summary>
    public static string FingerprintOf(byte[] data, bool hashed = false)
    {
        var digest = hashed ? data : SHA256.HashData(data);
        return "SHA256:" + Convert.ToBase64String(digest).TrimEnd('=');
    }

    private bool Ask(HostTrustRequest request)
    {
        // Connect runs on a worker thread and the prompt lives on the UI thread; blocking here is
        // what keeps the handshake paused while the person decides.
        return Task.Run(() => _askUser(request)).GetAwaiter().GetResult();
    }
}

/// <summary>
/// The server presents a different identity than the one the user approved. Its message tells
/// them what to do about it, because this is the one error where "just retry" is the wrong answer.
/// </summary>
public sealed class HostKeyChangedException : Exception
{
    public HostKeyChangedException(string host, string expected, string actual)
        : base($"The identity of {host} has CHANGED. Expected {expected} but the server presented {actual}. "
               + "This can mean the server was reinstalled — or that someone is intercepting the connection. "
               + "The connection was not made. If you are sure the change is legitimate, remove the host from the trusted list and connect again.")
    {
        Host = host;
        Expected = expected;
        Actual = actual;
    }

    public string Host { get; }

    public string Expected { get; }

    public string Actual { get; }
}

/// <summary>
/// The application-wide trust policy, set once at startup. A file system built without one refuses
/// unverifiable servers: failing closed is the only safe default for a security check.
/// </summary>
public static class NetworkSecurity
{
    public static HostTrustPolicy? Default { get; set; }
}
