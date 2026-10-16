using Microsoft.Extensions.Logging;
using TCV.Ai.Mcp;
using TCV.App.Settings;

namespace TCV.App.Agents;

/// <summary>
/// Owns the MCP server's lifetime inside the application: starts it when the user asks, stops it on
/// shutdown, and reports what is currently exposed.
/// <para>
/// Separate from <see cref="McpServer"/> so the plugin keeps knowing nothing about
/// <c>settings.json</c>, and separate from the dashboard so "is the server up?" has one answer rather
/// than one per window.
/// </para>
/// </summary>
public sealed class McpServerHost : IAsyncDisposable
{
    /// <summary>Refusal handed to the dashboard when a guest window tries to configure the layer.</summary>
    public const string SecondaryWindowRefusal =
        "This window belongs to a workspace opened from another window; the MCP server and its "
        + "approvals are owned by the main window.";

    private readonly AppSettings _settings;
    private readonly SettingsSerializer _serializer;
    private readonly AgentBridge _bridge;
    private readonly StartupOptions _startup;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<McpServerHost> _logger;

    private McpServer? _server;

    public McpServerHost(
        AppSettings settings,
        SettingsSerializer serializer,
        AgentBridge bridge,
        StartupOptions startup,
        ILoggerFactory loggerFactory,
        ILogger<McpServerHost> logger)
    {
        _settings = settings;
        _serializer = serializer;
        _bridge = bridge;
        _startup = startup;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <summary>
    /// False in a <c>--workspace</c> guest window, where every method that would start a server or
    /// write <c>settings.json</c> refuses instead (TRAP-22).
    /// <para>
    /// The guard lives here rather than in the dashboard because there is more than one way in:
    /// <c>App</c> already declines to auto-start a server in a guest window, and without this the
    /// dashboard's own buttons would do exactly what that refusal exists to prevent — a second
    /// listener overwriting and then, on close, deleting the primary window's
    /// <c>mcp-endpoint.json</c>, and a guest window's approved roots landing in the shared settings.
    /// </para>
    /// </summary>
    public bool CanConfigure => !_startup.IsSecondaryWindow;

    public bool IsRunning => _server?.IsRunning == true;

    public McpEndpointInfo? Endpoint => _server?.Endpoint;

    /// <summary>Why the last start attempt failed, or null.</summary>
    public string? LastError { get; private set; }

    /// <summary>Localization key for <see cref="LastError"/> (English stays for the log), or null.</summary>
    private string? _lastErrorKey;

    /// <summary>
    /// <see cref="LastError"/> in the user's language, for the dashboard. The English sentence is kept
    /// for the log and for the startup warning, where it is read by whoever files the bug.
    /// </summary>
    public string LastErrorText => _lastErrorKey switch
    {
        null => LastError ?? string.Empty,
        "Agent_McpPortTaken" => string.Format(
            Localization.LocalizationManager.Get(_lastErrorKey), _settings.Agent.McpPort),
        _ => Localization.LocalizationManager.Get(_lastErrorKey),
    };

    private void Fail(string english, string key)
    {
        LastError = english;
        _lastErrorKey = key;
    }

    /// <summary>
    /// The approved roots as they are right now, resolved. Read from the live scope rather than from
    /// the running server: the list is a setting the user edits whether the server is up or not, and
    /// the dashboard has to show it either way.
    /// </summary>
    public IReadOnlyList<string> CurrentRoots() => _bridge.CurrentScope().Roots;

    /// <summary>Whether agents may propose mutations (§ 3.6.2).</summary>
    public bool WritesEnabled => _settings.Agent.McpAllowWrites;

    /// <summary>Raised after the server started or stopped, so open windows can re-read the state.</summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// Starts the server if settings allow. Called at startup (when
    /// <see cref="AgentSettings.McpAutoStart"/> is on) and from the dashboard.
    /// </summary>
    public bool Start()
    {
        LastError = null;
        _lastErrorKey = null;

        if (IsRunning) return true;

        if (!CanConfigure)
        {
            Fail(SecondaryWindowRefusal, "Agent_SecondaryWindow");
            _logger.LogInformation("Secondary workspace window — the MCP server is not started here");
            return false;
        }

        if (!_settings.Agent.McpEnabled)
        {
            Fail("The MCP server is disabled in settings.", "Agent_McpDisabled");
            return false;
        }

        // Starting with nothing approved is allowed and deliberate: the server comes up, tools/list
        // works, and every path call returns no_scope with a hint. Refusing to start instead would
        // make "why is nothing connecting?" harder to answer than "why can it not read anything?".
        var protocol = new McpProtocol(
            _bridge,
            serverName: "tcvibe",
            serverVersion: typeof(McpServerHost).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
            logger: _loggerFactory.CreateLogger<McpProtocol>());

        var server = new McpServer(
            protocol,
            () => _bridge.CurrentScope().Roots,
            _loggerFactory.CreateLogger<McpServer>());

        // With no port configured, the port of the previous run is tried first: a client registered
        // with the URL then keeps finding the server after a restart. It is only a preference — if
        // something else has the port meanwhile, a free one is taken as before. The token is a
        // different matter and stays per-run (see McpServer): a token that survived the process would
        // keep granting access to whoever can read the file it was written to.
        var preferred = _settings.Agent.McpPort > 0 ? _settings.Agent.McpPort : _settings.Agent.McpLastPort;
        var endpoint = server.Start(preferred);
        if (endpoint is null && _settings.Agent.McpPort <= 0 && preferred > 0)
            endpoint = server.Start(0);

        if (endpoint is not null && _settings.Agent.McpPort <= 0
            && Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var url) && url.Port != _settings.Agent.McpLastPort)
        {
            _settings.Agent.McpLastPort = url.Port;
            Persist();
        }

        if (endpoint is null)
        {
            if (_settings.Agent.McpPort > 0)
            {
                Fail($"Port {_settings.Agent.McpPort} could not be bound. Set the port to 0 to pick a free one.",
                    "Agent_McpPortTaken");
            }
            else
            {
                Fail("The server could not bind a loopback port.", "Agent_McpNoPort");
            }

            _logger.LogWarning("MCP server did not start: {Error}", LastError);
            return false;
        }

        _server = server;
        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public async Task StopAsync()
    {
        if (_server is null) return;

        await _server.StopAsync();
        _server = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Flips the enabled switch, persists it and applies it. Persisting is the point: a server the
    /// user turned on should still be on after a restart, and one they turned off must stay off.
    /// </summary>
    public async Task<bool> SetEnabledAsync(bool enabled)
    {
        if (!CanConfigure)
        {
            Fail(SecondaryWindowRefusal, "Agent_SecondaryWindow");
            return false;
        }

        _settings.Agent.McpEnabled = enabled;
        Persist();

        if (enabled) return Start();

        await StopAsync();
        return true;
    }

    /// <summary>Adds a directory to the approved roots and persists it.</summary>
    public bool Approve(string directory)
    {
        if (!CanConfigure || string.IsNullOrWhiteSpace(directory)) return false;

        // Not a bare TrimEnd: «C:\» trimmed to «C:» is drive-relative and approved the current
        // directory on that drive instead of the drive.
        var normalized = PathScope.NormalizeConfiguredRoot(directory);
        if (_settings.Agent.ApprovedRoots.Any(root => PathScope.NamesSameRoot(root, normalized)))
            return false;

        _settings.Agent.ApprovedRoots.Add(normalized);
        Persist();
        RepublishEndpoint();
        return true;
    }

    /// <summary>
    /// Revokes a root. Takes effect for the next call — the scope is read per request.
    /// <paramref name="directory"/> is usually the resolved form the dashboard lists, which differs
    /// from the stored one for a junction or symlink root; both spellings are matched.
    /// </summary>
    public bool Revoke(string directory)
    {
        if (!CanConfigure || string.IsNullOrWhiteSpace(directory)) return false;

        var removed = _settings.Agent.ApprovedRoots
            .RemoveAll(root => PathScope.NamesSameRoot(root, directory));

        if (removed == 0) return false;

        Persist();
        RepublishEndpoint();
        return true;
    }

    /// <summary>Turns agent writes on or off and persists the choice.</summary>
    public void SetWritesEnabled(bool enabled)
    {
        if (!CanConfigure)
        {
            Fail(SecondaryWindowRefusal, "Agent_SecondaryWindow");
            StateChanged?.Invoke(this, EventArgs.Empty);   // puts the bound switch back where it was
            return;
        }

        _settings.Agent.McpAllowWrites = enabled;
        Persist();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Rewrites <c>mcp-endpoint.json</c> so its disclosure of the approved roots stays true after the
    /// scope changes — the file is what a user reads to answer "what did I open up?".
    /// </summary>
    private void RepublishEndpoint()
    {
        if (_server?.Endpoint is { } endpoint)
        {
            McpEndpointFile.Write(endpoint with { ApprovedRoots = _bridge.CurrentScope().Roots });
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Persist()
    {
        if (!CanConfigure) return;   // TRAP-22 — a guest window never writes settings.json

        try
        {
            _serializer.Save(_settings);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist the agent-layer settings");
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
