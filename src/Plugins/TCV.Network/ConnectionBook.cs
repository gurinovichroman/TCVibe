using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TCV.Core.Domain;
// ProtectedData is available via the System.Security.Cryptography.ProtectedData NuGet package

namespace TCV.Network;

/// <summary>
/// Stores FTP/SFTP/WebDAV connections on disk. Passwords are encrypted with DPAPI on Windows
/// and with AES-GCM (machine-scoped) on other platforms.
/// </summary>
/// <remarks>
/// DPAPI keys belong to one Windows user on one machine, so a copied portable folder keeps the
/// connections but cannot read their passwords. Such a password is not silently dropped: the
/// connection comes back flagged <see cref="ConnectionInfo.PasswordUnreadable"/>, the UI asks for
/// it again, and the stored blob is left alone until the user types a new one.
/// </remarks>
public sealed class ConnectionBook
{
    private const string FileName = "connections.json";

    /// <summary>Portable location: next to the exe, like every other piece of state (INV-03).</summary>
    private static string DefaultPath => global::TCV.Core.Domain.AppPaths.Data(FileName);

    /// <summary>
    /// Where releases up to 6.26 wrote the book. Read once, on the first load that finds no portable
    /// file, so an existing address book survives the move; never written to again.
    /// </summary>
    private static string LegacyPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TotalCommanderVibe", FileName);

    private readonly string _bookPath;

    /// <summary>Legacy file the migration reads instead of <see cref="LegacyPath"/>; tests only.</summary>
    private readonly string? _legacyOverride;

    private List<StoredConnection> _connections = [];

    /// <summary>True when the file exists but could not be read at all; saving would destroy it.</summary>
    private bool _saveBlocked;

    public IReadOnlyList<ConnectionInfo> Connections =>
        _connections.Select(Deserialize).ToList();

    /// <summary>
    /// Set when the address book could not be loaded normally: what happened and what was kept.
    /// The connection manager shows it — an empty list with no explanation looks like data loss.
    /// </summary>
    public string? LoadError { get; private set; }

    public ConnectionBook() : this(DefaultPath) { }

    /// <summary>
    /// Writes to an explicit file instead of <c>connections.json</c> next to the exe. Used by tests
    /// the same way <c>SettingsSerializer</c> takes a path — production always uses the portable
    /// location (INV-03).
    /// </summary>
    public ConnectionBook(string bookPath) : this(bookPath, legacyPath: null) { }

    /// <summary>
    /// Test seam: names the file the %AppData% migration may carry over, so that path can be
    /// exercised without touching the real profile of the machine running the tests.
    /// </summary>
    internal ConnectionBook(string bookPath, string? legacyPath)
    {
        _bookPath = bookPath;
        _legacyOverride = legacyPath;
        Load();
    }

    /// <summary>Absolute path of the file this book reads and writes.</summary>
    public string FilePath => _bookPath;

    public void Add(ConnectionInfo info)
    {
        EnsureWritable();
        _connections.Add(Serialize(info, null));
        Save();
    }

    /// <summary>Adds several connections with one write: an import from <c>~/.ssh/config</c>.</summary>
    public void AddRange(IEnumerable<ConnectionInfo> infos)
    {
        ArgumentNullException.ThrowIfNull(infos);
        EnsureWritable();
        _connections.AddRange(infos.Select(info => Serialize(info, null)));
        Save();
    }

    /// <summary>
    /// Records that a connection was just opened, for the palette's recent connections (6.82). Written
    /// pointwise, like <c>WorkspaceStore</c>: the file is read again and only this entry's time changes,
    /// so a workspace window (another process, TRAP-22) does not write back a stale list over a
    /// connection the main window added meanwhile. A file that is busy or damaged is left alone — a
    /// time stamp is not worth a fight over it. False when nothing was written.
    /// </summary>
    public bool MarkUsed(Guid id, DateTime whenUtc)
    {
        if (_saveBlocked)
        {
            return false;
        }

        string? json;
        try
        {
            json = AtomicFile.ReadAllTextOrNull(_bookPath);
        }
        catch (IOException)
        {
            return false;
        }

        if (json is not null)
        {
            try
            {
                _connections = JsonSerializer.Deserialize<List<StoredConnection>>(json) ?? [];
            }
            catch (JsonException)
            {
                return false;
            }
        }

        if (_connections.FirstOrDefault(c => c.Id == id) is not { } stored)
        {
            return false;
        }

        stored.LastUsed = DateTime.SpecifyKind(whenUtc, DateTimeKind.Utc);
        Save();
        return true;
    }

    public void Update(ConnectionInfo info)
    {
        EnsureWritable();
        var idx = _connections.FindIndex(c => c.Id == info.Id);
        if (idx >= 0)
        {
            _connections[idx] = Serialize(info, _connections[idx]);
        }
        else
        {
            _connections.Add(Serialize(info, null));
        }

        Save();
    }

    public void Remove(Guid id)
    {
        EnsureWritable();
        _connections.RemoveAll(c => c.Id == id);
        Save();
    }

    private void EnsureWritable()
    {
        if (_saveBlocked)
        {
            throw new IOException(LoadError ?? $"{FileName} could not be read, so it is not being overwritten.");
        }
    }

    // ── Serialization ─────────────────────────────────────────────────────────

    private static StoredConnection Serialize(ConnectionInfo info, StoredConnection? previous)
    {
        var sc = new StoredConnection
        {
            Id = info.Id,
            Name = info.Name,
            Protocol = info.Protocol,
            Host = info.Host,
            Port = info.Port,
            Username = info.Username,
            AuthMethod = info.AuthMethod,
            KeyFilePath = info.KeyFilePath,
            RemotePath = info.RemotePath,
            Region = info.Region,
            Bucket = info.Bucket,
            PathStyle = info.PathStyle,
            UseHttp = info.UseHttp,
            Provider = info.Provider,
            Profile = info.Profile,
            ClientId = info.ClientId,
            Color = info.Color,
            ReadOnly = info.ReadOnly,

            // The editor does not carry it: an edit keeps the time the connection was last opened.
            LastUsed = info.LastUsed ?? previous?.LastUsed,
        };

        if (!string.IsNullOrEmpty(info.Password))
        {
            sc.EncryptedPassword = EncryptString(info.Password);
        }
        else if (info.PasswordUnreadable)
        {
            // The user did not retype it: keep what is on file rather than erase a password that
            // may still work on the machine it was saved on.
            sc.EncryptedPassword = previous?.EncryptedPassword;
        }

        if (!string.IsNullOrEmpty(info.KeyFilePassphrase))
        {
            sc.EncryptedPassphrase = EncryptString(info.KeyFilePassphrase);
        }
        else if (info.PasswordUnreadable)
        {
            sc.EncryptedPassphrase = previous?.EncryptedPassphrase;
        }

        // The browser sign-in's secrets go the same way as a password: encrypted, and an unreadable
        // one is kept rather than erased until the user signs in again.
        sc.EncryptedClientSecret = !string.IsNullOrEmpty(info.ClientSecret)
            ? EncryptString(info.ClientSecret)
            : info.PasswordUnreadable ? previous?.EncryptedClientSecret : null;
        sc.EncryptedRefreshToken = !string.IsNullOrEmpty(info.RefreshToken)
            ? EncryptString(info.RefreshToken)
            : info.PasswordUnreadable ? previous?.EncryptedRefreshToken : null;

        return sc;
    }

    private static ConnectionInfo Deserialize(StoredConnection sc)
    {
        var info = new ConnectionInfo
        {
            Id = sc.Id,
            Name = sc.Name,
            Protocol = sc.Protocol,
            Host = sc.Host,
            Port = sc.Port,
            Username = sc.Username,
            AuthMethod = sc.AuthMethod,
            KeyFilePath = sc.KeyFilePath,
            RemotePath = sc.RemotePath,
            Region = sc.Region,
            Bucket = sc.Bucket,
            PathStyle = sc.PathStyle,
            UseHttp = sc.UseHttp,
            Provider = sc.Provider,
            Profile = sc.Profile,
            ClientId = sc.ClientId,
            Color = sc.Color,
            ReadOnly = sc.ReadOnly,
            LastUsed = sc.LastUsed,
        };

        if (!string.IsNullOrEmpty(sc.EncryptedPassword))
        {
            info.Password = TryDecryptString(sc.EncryptedPassword);
            info.PasswordUnreadable |= info.Password is null;
        }

        if (!string.IsNullOrEmpty(sc.EncryptedPassphrase))
        {
            info.KeyFilePassphrase = TryDecryptString(sc.EncryptedPassphrase);
            info.PasswordUnreadable |= info.KeyFilePassphrase is null;
        }

        if (!string.IsNullOrEmpty(sc.EncryptedClientSecret))
        {
            info.ClientSecret = TryDecryptString(sc.EncryptedClientSecret);
            info.PasswordUnreadable |= info.ClientSecret is null;
        }

        if (!string.IsNullOrEmpty(sc.EncryptedRefreshToken))
        {
            // A token that cannot be read is not worth a prompt: the next connect signs in again.
            info.RefreshToken = TryDecryptString(sc.EncryptedRefreshToken);
        }

        return info;
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    private void Load()
    {
        string? source = null;
        string? json;
        try
        {
            json = AtomicFile.ReadAllTextOrNull(_bookPath);
            if (json is not null)
            {
                source = _bookPath;
            }
            else if (TryFindLegacyBook() is { } legacy)
            {
                json = AtomicFile.ReadAllTextOrNull(legacy);
                source = legacy;
            }
        }
        catch (IOException ex)
        {
            // Locked by another process or unreadable: do not guess. Saving now would replace a
            // perfectly good file with an empty list.
            LoadError = $"{FileName} could not be read ({ex.Message}). It has not been changed, and saving is disabled until it can be read.";
            _saveBlocked = true;
            return;
        }

        if (json is null || source is null)
        {
            return;
        }

        try
        {
            _connections = JsonSerializer.Deserialize<List<StoredConnection>>(json) ?? [];
        }
        catch (JsonException ex)
        {
            var copy = AtomicFile.PreserveCorrupt(source);
            LoadError = $"{FileName} is damaged ({ex.Message}). "
                        + (copy is null ? "No copy could be made." : $"The damaged file was kept as {Path.GetFileName(copy)}.");
            _connections = [];
            return;
        }

        // Carried over from %AppData%: write it out next to the exe right away, so the portable
        // copy is authoritative from now on even if the user never edits a connection. The write is
        // caught separately — an exe directory that cannot be written to (Program Files, read-only
        // stick, a second instance holding the file) must not cost the user the book that was just
        // read: it stays in memory, the legacy file stays intact, and the next Save() tries again.
        if (source != _bookPath && _connections.Count > 0)
        {
            try
            {
                Save();
            }
            catch (IOException)
            {
                // Migration deferred; the connections are already loaded.
            }
            catch (UnauthorizedAccessException)
            {
                // Same: read-only location.
            }
        }
    }

    /// <summary>
    /// Only consulted for the default (portable) location: a book explicitly pointed at some other
    /// file — a test, a second profile — must not silently inherit the machine's old %AppData% one.
    /// </summary>
    private string? TryFindLegacyBook()
    {
        var legacy = _legacyOverride;
        if (legacy is null)
        {
            if (!string.Equals(_bookPath, DefaultPath, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            legacy = LegacyPath;
        }

        return File.Exists(legacy) ? legacy : null;
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_connections,
            new JsonSerializerOptions { WriteIndented = true });
        AtomicFile.WriteAllText(_bookPath, json);
    }

    // ── Encryption (DPAPI on Windows, AES-GCM elsewhere) ─────────────────────

    private static string EncryptString(string plainText)
    {
        var bytes = Encoding.UTF8.GetBytes(plainText);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }
        // Non-Windows fallback: AES-GCM with machine-derived key
        return AesGcmEncrypt(bytes);
    }

    private static string? TryDecryptString(string cipherText)
    {
        try
        {
            var bytes = Convert.FromBase64String(cipherText);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var decrypted = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decrypted);
            }
            return AesGcmDecrypt(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    private static readonly byte[] MachineKey = DeriveKey();

    private static byte[] DeriveKey()
    {
        var seed = Encoding.UTF8.GetBytes(Environment.MachineName + "TCV");
        return SHA256.HashData(seed);
    }

    private static string AesGcmEncrypt(byte[] plaintext)
    {
        var nonce = new byte[AesGcm.NonceByteSizes.MaxSize];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];

        using var aes = new AesGcm(MachineKey, AesGcm.TagByteSizes.MaxSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        // Format: nonce + tag + ciphertext
        var result = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, result, nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, result, nonce.Length + tag.Length, ciphertext.Length);
        return Convert.ToBase64String(result);
    }

    private static string AesGcmDecrypt(byte[] data)
    {
        int nonceLen = AesGcm.NonceByteSizes.MaxSize;
        int tagLen = AesGcm.TagByteSizes.MaxSize;
        var nonce = data[..nonceLen];
        var tag = data[nonceLen..(nonceLen + tagLen)];
        var ciphertext = data[(nonceLen + tagLen)..];
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(MachineKey, tagLen);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }

    // ── DTO ───────────────────────────────────────────────────────────────────

    private sealed class StoredConnection
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public NetworkProtocol Protocol { get; set; }
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; }
        public string Username { get; set; } = string.Empty;
        public string? EncryptedPassword { get; set; }
        public SshAuthMethod AuthMethod { get; set; }
        public string? KeyFilePath { get; set; }
        public string? EncryptedPassphrase { get; set; }
        public string RemotePath { get; set; } = "/";

        // Added with object storage and the browser-signed drives; an older file reads them as unset.
        public string? Region { get; set; }
        public string? Bucket { get; set; }
        public bool PathStyle { get; set; }
        public bool UseHttp { get; set; }
        public string? Provider { get; set; }
        public string? Profile { get; set; }
        public string? ClientId { get; set; }
        public string? EncryptedClientSecret { get; set; }
        public string? EncryptedRefreshToken { get; set; }
        public string? Color { get; set; }
        public bool ReadOnly { get; set; }

        // Added with the palette's recent connections (6.82); an older file reads it as never opened.
        public DateTime? LastUsed { get; set; }
    }
}
