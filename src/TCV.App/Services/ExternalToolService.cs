using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;
using TCV.App.Navigation;
using TCV.App.Settings;
using TCV.Core.Domain;

namespace TCV.App.Services;

/// <summary>
/// Launches editors and agent CLIs for the active panel: resolves which tools exist on this machine,
/// remembers the chosen default in <c>settings.json</c> (INV-03) and starts them — directly, or in a
/// terminal window for the TUI ones.
/// </summary>
/// <remarks>
/// A configured list in settings wins as-is; an empty list means "not customised", and the service
/// then offers what it detects from <see cref="ExternalToolCatalog"/>. That way a fresh portable copy
/// finds VS Code or Claude Code without the user filling in a form first.
/// </remarks>
public sealed class ExternalToolService
{
    private readonly AppSettings _settings;
    private readonly SettingsSerializer _serializer;
    private readonly TerminalService _terminal;
    private readonly ILogger<ExternalToolService> _logger;

    public ExternalToolService(
        AppSettings settings,
        SettingsSerializer serializer,
        TerminalService terminal,
        ILogger<ExternalToolService> logger)
    {
        _settings = settings;
        _serializer = serializer;
        _terminal = terminal;
        _logger = logger;
    }

    // ── Discovery ─────────────────────────────────────────────────────────────

    /// <summary>Editors to offer: the configured list, or everything detected on this machine.</summary>
    public IReadOnlyList<ExternalTool> AvailableEditors() =>
        Resolve(_settings.ExternalTools.Editors, ExternalToolCatalog.Editors);

    /// <summary>Agent CLIs to offer: the configured list, or everything detected on this machine.</summary>
    public IReadOnlyList<ExternalTool> AvailableAgents() =>
        Resolve(_settings.ExternalTools.Agents, ExternalToolCatalog.Agents);

    /// <summary>The catalog entries actually installed — what the settings dialog's «Detect» offers.</summary>
    public IReadOnlyList<ExternalTool> DetectInstalled(IReadOnlyList<ExternalToolTemplate> catalog) =>
        catalog.Where(IsInstalled).Select(t => t.ToSettings()).ToList();

    /// <summary>The editor Ctrl+E uses: the saved default when it is still available, else the first.</summary>
    public ExternalTool? DefaultEditor() =>
        PickDefault(AvailableEditors(), _settings.ExternalTools.DefaultEditor);

    /// <summary>The agent Ctrl+Shift+A uses.</summary>
    public ExternalTool? DefaultAgent() =>
        PickDefault(AvailableAgents(), _settings.ExternalTools.DefaultAgent);

    /// <summary>
    /// The agent to preselect among <paramref name="agents"/> (as returned by
    /// <see cref="AvailableAgents"/>), and whether it is the configured default or a stand-in for one
    /// that is not installed. Probes <c>PATH</c>: call it off the UI thread (INV-06).
    /// </summary>
    public ExternalToolChoice ChooseAgent(IReadOnlyList<ExternalTool> agents) =>
        ExternalToolChoice.Choose(agents, _settings.ExternalTools.DefaultAgent, IsInstalled);

    /// <summary>Remembers the tool the user picked, so the plain gesture uses it next time.</summary>
    public void RememberDefaultEditor(string name) => Remember(name, isEditor: true);

    /// <summary>Remembers the agent the user picked.</summary>
    public void RememberDefaultAgent(string name) => Remember(name, isEditor: false);

    // ── Launching ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts <paramref name="tool"/> against a panel: <paramref name="directory"/> is the working
    /// directory and <c>%P</c>, <paramref name="selected"/> fills <c>%F</c>. Returns false when the
    /// program could not be started — the caller reports that, since a silently dead Ctrl+E is worse
    /// than an error box.
    /// </summary>
    public bool Launch(ExternalTool tool, string directory, IReadOnlyList<string> selected) =>
        Launch(tool, directory, selected, out _);

    /// <summary>
    /// As <see cref="Launch(ExternalTool, string, IReadOnlyList{string})"/>, with the reason it was
    /// refused: a path holding <c>%</c> or <c>!</c> cannot be handed to a <c>.cmd</c> shim or to
    /// <c>cmd /k</c> without <c>cmd.exe</c> expanding it, so the launch is declined instead.
    /// </summary>
    public bool Launch(ExternalTool tool, string directory, IReadOnlyList<string> selected, out string? failureReason)
    {
        failureReason = null;
        var resolved = ResolveCommand(tool.Command);

        try
        {
            // Terminal tools always go through cmd /k; a GUI editor does when its command is (or
            // resolves to) a batch shim such as code.cmd.
            var forCmd = tool.InTerminal || ShellQuoting.MayRunThroughCmd(resolved ?? tool.Command);
            var arguments = ExternalToolCatalog.ExpandArguments(tool.Arguments, directory, selected, forCmd);

            if (tool.InTerminal)
            {
                var command = ShellQuoting.QuoteIfNeeded(tool.Command);
                var commandLine = string.IsNullOrEmpty(arguments) ? command : $"{command} {arguments}";
                return _terminal.OpenWithCommand(directory, commandLine);
            }

            // The resolved path is used when known: PATH shims work through the shell either way,
            // but starting the resolved file gives a real error instead of "file not found" noise.
            var psi = new ProcessStartInfo
            {
                FileName = resolved ?? tool.Command,
                Arguments = arguments,
                WorkingDirectory = Directory.Exists(directory) ? directory : string.Empty,
                UseShellExecute = true,
            };

            Process.Start(psi);
            _logger.LogInformation("Launched {Tool}: {Command} {Args}", tool.Name, psi.FileName, arguments);
            return true;
        }
        catch (UnsafeMacroValueException ex)
        {
            _logger.LogWarning("Refused to launch {Tool}: {Value} is not safe for cmd.exe", tool.Name, ex.Value);
            failureReason = ex.Value;
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to launch {Tool} ({Command})", tool.Name, tool.Command);
            return false;
        }
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    /// <summary>True when the tool's command resolves to something runnable on this machine.</summary>
    public bool IsInstalled(ExternalTool tool) =>
        ResolveCommand(tool.Command) is not null;

    private bool IsInstalled(ExternalToolTemplate template) =>
        ResolveCommand(template.Command) is not null ||
        template.ProbePaths.Any(p => File.Exists(Environment.ExpandEnvironmentVariables(p)));

    /// <summary>
    /// Resolves a command through <c>PATH</c>/<c>PATHEXT</c>, then through the catalog's extra
    /// install locations for that command (VS Code's <c>bin</c>, JetBrains Toolbox scripts …).
    /// </summary>
    private string? ResolveCommand(string command)
    {
        // Every launch resolves its command again, and a PATH entry on a dead share makes each
        // File.Exists hang for seconds: remember answers as long as detection results (INV-06).
        var now = Environment.TickCount64;
        lock (_detectedGate)
        {
            if (_resolved.TryGetValue(command, out var hit) && now - hit.Stamp < DetectionTtl.TotalMilliseconds)
            {
                return hit.Path;
            }
        }

        var path = ResolveCommandUncached(command);
        lock (_detectedGate)
        {
            _resolved[command] = (now, path);
        }

        return path;
    }

    private string? ResolveCommandUncached(string command)
    {
        var direct = CommandResolver.Resolve(command, PathDirectories(), PathExtensions(), File.Exists);
        if (direct is not null)
        {
            return direct;
        }

        foreach (var template in ExternalToolCatalog.Editors.Concat(ExternalToolCatalog.Agents))
        {
            if (!string.Equals(template.Command, command, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var probe in template.ProbePaths)
            {
                var expanded = Environment.ExpandEnvironmentVariables(probe);
                if (File.Exists(expanded))
                {
                    return expanded;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> PathDirectories() =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

    private static IEnumerable<string> PathExtensions() =>
        (Environment.GetEnvironmentVariable("PATHEXT") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

    // Detection probes PATH × PATHEXT for every catalog entry — a couple of thousand File.Exists
    // calls, and PATH may hold a dead network share. It ran on the UI thread on every Ctrl+E, so the
    // result is kept for a short while; the settings dialog's «Detect» button still calls
    // DetectInstalled directly and always sees the disk as it is now.
    private static readonly TimeSpan DetectionTtl = TimeSpan.FromSeconds(60);
    private readonly Dictionary<IReadOnlyList<ExternalToolTemplate>, (long Stamp, IReadOnlyList<ExternalTool> Tools)> _detected = new();
    private readonly object _detectedGate = new();

    /// <summary>Forgets cached detection results, e.g. after the user installed an editor.</summary>
    public void InvalidateDetection()
    {
        lock (_detectedGate)
        {
            _detected.Clear();
            _resolved.Clear();
        }
    }

    private readonly Dictionary<string, (long Stamp, string? Path)> _resolved = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<ExternalTool> DetectCached(IReadOnlyList<ExternalToolTemplate> catalog)
    {
        var now = Environment.TickCount64;
        lock (_detectedGate)
        {
            if (_detected.TryGetValue(catalog, out var hit) && now - hit.Stamp < DetectionTtl.TotalMilliseconds)
            {
                return hit.Tools;
            }
        }

        var tools = DetectInstalled(catalog);
        lock (_detectedGate)
        {
            _detected[catalog] = (now, tools);
        }

        return tools;
    }

    private IReadOnlyList<ExternalTool> Resolve(
        List<ExternalTool> configured, IReadOnlyList<ExternalToolTemplate> catalog) =>
        configured.Count > 0 ? configured : DetectCached(catalog);

    private static ExternalTool? PickDefault(IReadOnlyList<ExternalTool> tools, string preferredName)
    {
        if (tools.Count == 0)
        {
            return null;
        }

        return tools.FirstOrDefault(
            t => string.Equals(t.Name, preferredName, StringComparison.OrdinalIgnoreCase)) ?? tools[0];
    }

    private void Remember(string name, bool isEditor)
    {
        if (isEditor)
        {
            if (_settings.ExternalTools.DefaultEditor == name)
            {
                return;
            }

            _settings.ExternalTools.DefaultEditor = name;
        }
        else
        {
            if (_settings.ExternalTools.DefaultAgent == name)
            {
                return;
            }

            _settings.ExternalTools.DefaultAgent = name;
        }

        try
        {
            _serializer.Save(_settings);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist the default external tool");
        }
    }
}
