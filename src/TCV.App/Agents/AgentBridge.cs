using System.IO;
using Microsoft.Extensions.Logging;
using TCV.Ai.Agents;
using TCV.Ai.Mcp;
using TCV.App.FileSystem;
using TCV.App.Services;
using TCV.App.Settings;
using TCV.Core.Domain;
using TCV.Core.Interfaces;
using TCV.Core.PluginSdk;

namespace TCV.App.Agents;

/// <summary>
/// Answers an agent's questions against the running application (6.13). The MCP protocol layer
/// validates and scopes; this class does the actual reaching — into the disk, into an archive, into
/// whatever a panel has mounted (INV-02).
/// </summary>
/// <remarks>
/// Reading goes straight through the VFS; writing does not go through here at all. Mutations are
/// handed to <see cref="IAgentWriteGate"/>, which asks the user and then uses the operation queue, so
/// there is no code path in the agent layer that touches an <see cref="IVirtualFileSystem"/> write
/// method directly (INV-10, ADR-0012). That is enforced by there being no such call in this file.
/// </remarks>
public sealed class AgentBridge : IAgentBridge
{
    private readonly WindowsFileSystem _localFs;
    private readonly PluginHost.PluginHost _plugins;
    private readonly OperationLog _operationLog;
    private readonly AppSettings _settings;
    private readonly IAgentWriteGate _gate;
    private readonly ILogger<AgentBridge> _logger;

    public AgentBridge(
        WindowsFileSystem localFs,
        PluginHost.PluginHost plugins,
        OperationLog operationLog,
        AppSettings settings,
        IAgentWriteGate gate,
        ILogger<AgentBridge> logger)
    {
        _localFs = localFs;
        _plugins = plugins;
        _operationLog = operationLog;
        _settings = settings;
        _gate = gate;
        _logger = logger;
    }

    /// <summary>
    /// Set by <c>MainViewModel</c> on construction. Null until the window exists, and every method
    /// that needs it says so rather than dereferencing — the server can be asked something during
    /// startup or after the window closed.
    /// </summary>
    public IAgentHost? Host { get; set; }

    public bool WritesEnabled => _settings.Agent.McpAllowWrites;

    public PathScope CurrentScope() => new(_settings.Agent.ApprovedRoots);

    /// <summary>Whether agents may read a server connection open in a panel (ADR-0023).</summary>
    public bool RemotePanelsAllowed => _settings.Agent.McpAllowRemotePanels;

    // ─────────────────────────────── Context ───────────────────────────────

    public Task<AgentPanelContext> GetContextAsync(CancellationToken ct = default)
    {
        var host = RequireHost();
        var panels = host.SnapshotPanels();
        var scope = CurrentScope();

        // A mounted panel's path is meaningless to an agent as a disk path, so it is handed over in
        // the addressable form instead (see AgentPathRef) — and so are the selected entries of a
        // mounted panel: a raw "/logs/app.log" would be read as a Windows path on the current drive.
        // Either kind goes through the scope first: the panels show wherever the user is browsing,
        // and the names of folders and files the agent may not read are not the agent's (ADR-0023).
        var activeReadable = MountReadable(panels.ActiveMount, scope, AgentPathRef.ActivePanel);
        var oppositeReadable = MountReadable(panels.OppositeMount, scope, AgentPathRef.OppositePanel);

        var active = !panels.ActiveIsMounted ? InScopeOrEmpty(scope, panels.ActivePath)
            : activeReadable ? PanelForm(AgentPathRef.ActivePanel, panels.ActivePath)
            : string.Empty;

        var opposite = !panels.OppositeIsMounted ? InScopeOrEmpty(scope, panels.OppositePath)
            : oppositeReadable ? PanelForm(AgentPathRef.OppositePanel, panels.OppositePath)
            : string.Empty;

        List<string> selection = !panels.ActiveIsMounted
            ? panels.Selection.Where(path => InScopeOrEmpty(scope, path).Length > 0).ToList()
            : activeReadable ? panels.Selection.Select(path => PanelForm(AgentPathRef.ActivePanel, path)).ToList()
            : [];

        // The branch belongs to the active directory; with that withheld, so is its branch name.
        var branch = active.Length > 0 ? panels.Branch : string.Empty;

        return Task.FromResult(new AgentPanelContext(
            active,
            opposite,
            selection,
            panels.Workspace,
            branch,
            panels.ActiveScheme,
            panels.ActiveIsMounted,
            scope.Roots,
            WritesEnabled));
    }

    /// <summary>
    /// <paramref name="path"/> when it lies inside an approved directory, empty otherwise. Public for
    /// the test that pins what <c>tcv_selection</c> may disclose.
    /// </summary>
    public static string InScopeOrEmpty(PathScope scope, string path) =>
        path.Length > 0 && scope.Check(path).IsAllowed ? path : string.Empty;

    /// <summary>
    /// Whether <c>panel://</c> may read <paramref name="mount"/> under <paramref name="scope"/>. The one
    /// place the rule meets its inputs, so the dashboard's «what is exposed» line cannot drift from it.
    /// </summary>
    public bool MountReadable(AgentMountInfo? mount, PathScope scope, string panel) =>
        mount is not null && PanelMountAccess.Check(mount, scope, RemotePanelsAllowed, panel) is null;

    // ─────────────────────────────── Reading ───────────────────────────────

    public async Task<IReadOnlyList<AgentEntry>> ListAsync(string path, CancellationToken ct = default)
    {
        var reference = AgentPathRef.Parse(path);
        var target = await OpenAsync(reference, ct);

        try
        {
            var entries = await target.Vfs.ListAsync(new VfsPath(target.Path), ct);
            return entries
                .Select(entry => new AgentEntry(
                    Describe(reference, entry),
                    entry.Name,
                    entry.IsDirectory,
                    entry.Size,
                    ToUtc(entry.LastModified)))
                .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Translate(ex, reference);
        }
        finally
        {
            target.Dispose();
        }
    }

    public async Task<AgentFileText> ReadAsync(
        string path, int maxBytes, CancellationToken ct = default)
    {
        var reference = AgentPathRef.Parse(path);
        var target = await OpenAsync(reference, ct);

        try
        {
            var vfsPath = new VfsPath(target.Path);
            var entry = await target.Vfs.GetEntryAsync(vfsPath, ct);
            if (entry is { IsDirectory: true })
            {
                throw new McpToolException(McpError.Invalid(
                    $"{path} is a directory.", "Use tcv_list for a directory."));
            }

            await using var stream = await target.Vfs.OpenReadAsync(vfsPath, ct);

            // Read one byte past the cap: that is how "was it truncated?" is answered without
            // trusting a reported size, which a remote file system may not know.
            var buffer = new byte[maxBytes + 1];
            var read = await ReadFullyAsync(stream, buffer, ct);

            var truncated = read > maxBytes;
            var payload = buffer.AsSpan(0, Math.Min(read, maxBytes)).ToArray();

            // Trim to a code-point boundary BEFORE anything looks at the bytes. Order matters:
            // DetectEncoding validates UTF-8 strictly, so a buffer cut mid-sequence fails that check
            // and falls back to Latin-1 — the file comes back as mojibake with the wrong encoding
            // reported, which is worse than a short read.
            if (truncated)
            {
                var safeLength = TextProbe.Utf8SafeLength(payload);
                if (safeLength < payload.Length) payload = payload[..safeLength];
            }

            if (TextProbe.LooksBinary(payload))
            {
                throw new McpToolException(new McpError(
                    McpError.Unsupported,
                    $"{path} is not a text file.",
                    "Binary content is not returned; read a text file instead."));
            }

            var (encoding, _) = TextProbe.DetectEncoding(payload);

            // DecodeText skips the BOM itself, so the whole trimmed payload goes in.
            var text = TextProbe.DecodeText(payload, encoding);

            return new AgentFileText(
                reference.ToString(),
                text,
                truncated,
                entry?.Size ?? read,
                encoding.WebName);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (McpToolException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Translate(ex, reference);
        }
        finally
        {
            target.Dispose();
        }
    }

    public Task<IReadOnlyList<AgentSearchHit>> SearchAsync(
        AgentSearchRequest request, CancellationToken ct = default) =>
        RequireHost().SearchForAgentAsync(request, ct);

    public Task<IReadOnlyList<AgentOperationRecord>> RecentOperationsAsync(
        int limit, CancellationToken ct = default)
    {
        var records = _operationLog.LoadAll()
            .Take(limit)
            .Select(entry => new AgentOperationRecord(
                ToUtc(entry.Timestamp),
                entry.Kind.ToString(),
                entry.Source,
                entry.Destination,
                entry.ItemCount,
                entry.Outcome.ToString(),
                entry.Origin ?? string.Empty))
            .ToList();

        return Task.FromResult<IReadOnlyList<AgentOperationRecord>>(records);
    }

    // ─────────────────────────────── Writing ───────────────────────────────

    public Task<AgentWriteDecision> ProposeWriteAsync(
        AgentWriteProposal proposal, CancellationToken ct = default)
    {
        if (!WritesEnabled)
            return Task.FromResult(AgentWriteDecision.Reject(McpError.WritesDisabled().Message));

        return _gate.SubmitAsync(proposal, ct);
    }

    // ─────────────────────────────── Resolution ───────────────────────────────

    /// <summary>
    /// A file system to act on plus the path inside it. <see cref="Dispose"/> releases only what this
    /// resolution created: a panel's mount and the local disk are borrowed and outlive the call
    /// (TRAP-05 — disposing a panel's file system would take the panel down with it). A borrowed panel
    /// mount is held by a lease instead, released the same way (INV-09).
    /// </summary>
    private readonly struct Target : IDisposable
    {
        public Target(IVirtualFileSystem vfs, string path, bool owned, IDisposable? lease = null)
        {
            Vfs = vfs;
            Path = path;
            Owned = owned;
            Lease = lease;
        }

        public IVirtualFileSystem Vfs { get; }
        public string Path { get; }
        private bool Owned { get; }
        private IDisposable? Lease { get; }

        public void Dispose()
        {
            if (Owned) Vfs.Dispose();
            Lease?.Dispose();
        }
    }

    private Task<Target> OpenAsync(AgentPathRef reference, CancellationToken ct) =>
        reference.Kind switch
        {
            AgentPathKind.Local => Task.FromResult(new Target(_localFs, reference.Container, owned: false)),
            AgentPathKind.Archive => Task.FromResult(OpenArchive(reference)),
            _ => Task.FromResult(OpenPanel(reference)),
        };

    /// <summary>
    /// Opens the archive on demand. Owned, therefore disposed after the call: keeping archive handles
    /// alive per agent request would pin files the user cannot then delete or move.
    /// </summary>
    private Target OpenArchive(AgentPathRef reference)
    {
        var plugin = _plugins.Registry.FileSystems.OfType<IArchivePlugin>().FirstOrDefault();
        if (plugin is null)
        {
            throw new McpToolException(new McpError(
                McpError.Unsupported, "The archive plugin is not loaded.", "Restart the application."));
        }

        if (!File.Exists(reference.Container))
            throw new McpToolException(McpError.NotFoundAt(reference.Container));

        try
        {
            return new Target(plugin.Create(reference.Container), reference.Inner, owned: true);
        }
        catch (McpToolException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A password-protected archive lands here; the hint is the actionable part.
            throw new McpToolException(new McpError(
                McpError.Unsupported,
                $"Could not open {reference.Container}: {ex.Message}",
                "An encrypted archive has to be opened in a panel first, where the password is asked for."));
        }
    }

    private Target OpenPanel(AgentPathRef reference)
    {
        var mount = RequireHost().PanelMount(reference.IsActivePanel);
        if (mount is null)
        {
            throw new McpToolException(new McpError(
                McpError.NotFound,
                $"The {reference.Container} panel has nothing mounted.",
                "panel:// addresses an open archive or remote connection. For an ordinary directory "
                + "use the Windows path from tcv_selection."));
        }

        string combined;
        try
        {
            // Checked here, not in the protocol layer: only the panel knows what it has mounted, and
            // the rule is read per request, so a revoked root or a switched-off setting applies at once.
            if (PanelMountAccess.Check(mount.Info, CurrentScope(), RemotePanelsAllowed, reference.Container) is { } refusal)
            {
                throw new McpToolException(refusal);
            }

            combined = PanelTargetPath(reference, mount.Vfs);
        }
        catch
        {
            mount.Lease.Dispose();
            throw;
        }

        // The mount stays borrowed, but the lease keeps it alive until this request is finished: an
        // archive closed or a connection dropped mid-read would otherwise be disposed underneath it.
        return new Target(mount.Vfs, combined, owned: false, lease: mount.Lease);
    }

    /// <summary>
    /// The path inside <paramref name="vfs"/> that a <c>panel://</c> reference names — or a refusal.
    /// <para>
    /// Refused: a panel that is a view onto the local disk (search results forward every call to it).
    /// Nothing there passes through <see cref="PathScope"/>, and a path like <c>/Users/me/.ssh/id_rsa</c>
    /// would be read from the current drive. <c>panel://</c> exists for archives and remote
    /// connections, and even those only under <see cref="PanelMountAccess"/>.
    /// </para>
    /// <para>
    /// Paths are absolute inside the mount, as <c>tcv_selection</c> and <c>tcv_list</c> print them,
    /// whichever directory the panel is standing in. (Resolving them against the panel's directory
    /// made the server's own output unusable: <c>/logs/app.log</c> became <c>/logs/logs/app.log</c>.)
    /// </para>
    /// </summary>
    public static string PanelTargetPath(AgentPathRef reference, IVirtualFileSystem vfs)
    {
        if (vfs is WindowsFileSystem or SearchResultsFileSystem
            || string.Equals(vfs.Scheme, "file", StringComparison.OrdinalIgnoreCase))
        {
            throw new McpToolException(new McpError(
                McpError.Unsupported,
                $"The {reference.Container} panel shows local files, not an archive or remote connection.",
                "panel:// addresses an open archive or remote connection. For files on disk use their "
                + "Windows paths (see tcv_selection)."));
        }

        return AgentPathRef.MountPath(reference.Inner);
    }

    /// <summary>Path of a listed entry, in the same form the caller used to ask.</summary>
    private static string Describe(AgentPathRef reference, IFileEntry entry) =>
        reference.Kind switch
        {
            AgentPathKind.Local => WindowsFileSystem.ToWindowsPath(entry.Path),
            AgentPathKind.Archive => reference.WithInner(entry.Path.Value).ToString(),
            _ => $"{AgentPathRef.PanelScheme}://{reference.Container}{Normalize(entry.Path.Value)}",
        };

    private IAgentHost RequireHost() =>
        Host ?? throw new McpToolException(new McpError(
            McpError.Internal,
            "The main window is not available.",
            "The application is still starting up or shutting down; retry shortly."));

    /// <summary>
    /// Turns a file-system failure into a structured error. Distinguishing "not there" from
    /// everything else matters: the first is something the agent can correct, the second is not.
    /// </summary>
    private McpToolException Translate(Exception ex, AgentPathRef reference)
    {
        if (ex is FileNotFoundException or DirectoryNotFoundException)
            return new McpToolException(McpError.NotFoundAt(reference.ToString()));

        if (ex is UnauthorizedAccessException)
        {
            return new McpToolException(new McpError(
                McpError.OutsideScope,
                $"Windows refused access to {reference}.",
                "The file may be locked by another program or need different permissions."));
        }

        _logger.LogWarning(ex, "Agent request for {Path} failed", reference);
        return new McpToolException(new McpError(McpError.Internal, ex.Message, string.Empty));
    }

    private static async Task<int> ReadFullyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0) break;
            total += read;
        }

        return total;
    }

    /// <summary>
    /// File systems report local times (and some report unspecified ones). The agent contract is
    /// UTC, so an unspecified kind is treated as local rather than silently relabelled as UTC.
    /// </summary>
    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime(),
    };

    /// <summary>A path inside a panel's mount in the form an agent passes back (mount-absolute).</summary>
    private static string PanelForm(string panel, string mountPath) =>
        $"{AgentPathRef.PanelScheme}://{panel}{AgentPathRef.MountPath(mountPath)}";

    private static string Normalize(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (!normalized.StartsWith('/')) normalized = "/" + normalized;
        return normalized;
    }
}
