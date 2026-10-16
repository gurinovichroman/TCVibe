using System.Security.Cryptography;
using System.Text;

namespace TCV.App.Settings;

/// <summary>
/// Keeps a secret out of <c>settings.json</c> in the clear: Windows DPAPI, current user — the same
/// protection the connection book gives passwords. A copy of the portable folder on another PC or
/// account cannot read it (the key is then simply empty and has to be entered again), which is the
/// point.
/// </summary>
public static class SecretProtector
{
    private const string Prefix = "dpapi:";

    /// <summary>The stored form of <paramref name="plain"/>; empty stays empty.</summary>
    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain))
        {
            return string.Empty;
        }

        var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(blob);
    }

    /// <summary>
    /// The secret behind a stored value. A value without the prefix is a secret from a version that
    /// stored it plainly and is returned as is (the next save protects it); one that cannot be
    /// decrypted here comes back empty.
    /// </summary>
    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return string.Empty;
        }

        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return stored;
        }

        try
        {
            var blob = Convert.FromBase64String(stored[Prefix.Length..]);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return string.Empty;
        }
    }

    /// <summary>True for a value in the protected form (whether or not this account can decrypt it).</summary>
    public static bool IsProtected(string? stored) =>
        !string.IsNullOrEmpty(stored) && stored.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>True for a stored value that this account could not decrypt.</summary>
    public static bool IsUnreadable(string? stored) =>
        !string.IsNullOrEmpty(stored)
        && stored.StartsWith(Prefix, StringComparison.Ordinal)
        && Unprotect(stored).Length == 0;
}
