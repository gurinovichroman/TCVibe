using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TCV.App.Settings;
using Velopack;
using Velopack.Sources;

namespace TCV.App.Services;

/// <summary>What an update check found.</summary>
public enum UpdateCheckOutcome
{
    /// <summary>The installed version is the newest one.</summary>
    UpToDate,

    /// <summary>A newer release exists; see <see cref="UpdateCheckResult.Update"/>.</summary>
    Available,

    /// <summary>The check itself failed (no network, rate limit, unreadable answer) — it says nothing about being up to date.</summary>
    Failed,

    /// <summary>GitHub answered, but the repository has no published release (or is not public).</summary>
    NoReleases,
}

/// <summary>Outcome of one check: the update if there is one, or why the check failed.</summary>
public sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, UpdateInfo? Update = null, string? Error = null);

/// <summary>
/// Checks for a newer version of TCVibe and, in the portable layout, installs it
/// (ADR-0031). Runs only when the user asks for it (Help → Check for updates); nothing is contacted
/// in the background.
/// </summary>
/// <remarks>
/// A copy in Velopack's layout asks Velopack: the GitHub Releases of the repository (or a feed folder
/// or URL from settings), a delta download, the swap after exit. A dev build or an unpacked
/// pre-Velopack ZIP cannot install anything, so it reads the latest GitHub release through the REST
/// API and offers the download page.
/// </remarks>
public sealed partial class UpdateService : IDisposable
{
    /// <summary>Repository used when settings.json carries no override.</summary>
    public const string DefaultRepository = ProjectLinks.Repository;

    private readonly HttpClient _httpClient;
    private readonly ILogger<UpdateService> _logger;
    private readonly string _repository;
    private readonly Lazy<UpdateManager?> _manager;

    /// <summary>What the last check found, kept for the download and the hand-over that follow it.</summary>
    private Velopack.UpdateInfo? _pending;

    public UpdateService(ILogger<UpdateService> logger, AppSettings settings)
        : this(logger, settings.Updates.Repository, handler: null, canInstall: PortableLayout.IsVelopack)
    {
    }

    public UpdateService(ILogger<UpdateService> logger, string repository, HttpMessageHandler? handler = null)
        : this(logger, repository, handler, canInstall: false)
    {
    }

    private UpdateService(ILogger<UpdateService> logger, string repository, HttpMessageHandler? handler, bool canInstall)
    {
        _logger = logger;
        _repository = string.IsNullOrWhiteSpace(repository) ? DefaultRepository : repository.Trim().Trim('/');
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "TCVibe");
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
        _manager = new Lazy<UpdateManager?>(() => canInstall ? CreateManager() : null);
    }

    /// <summary>GitHub REST endpoint for the latest release of the configured repository.</summary>
    public string LatestReleaseApiUrl =>
        $"https://api.github.com/repos/{_repository}/releases/latest";

    /// <summary>Human-facing releases page, used when a release carries no portable asset.</summary>
    public string ReleasesPageUrl => $"https://github.com/{_repository}/releases/latest";

    public Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 0);

    /// <summary>True when this copy installs an update itself rather than offering the download page.</summary>
    public bool CanInstall => _manager.Value is not null;

    /// <summary>
    /// "owner/name" is a GitHub repository. Anything else in <c>Updates.Repository</c> is a Velopack
    /// feed — a folder or a URL holding <c>releases.*.json</c> — for self-hosting and for testing an
    /// update without publishing one.
    /// </summary>
    public static bool IsGitHubRepository(string repository) => GitHubRepositoryPattern().IsMatch(repository);

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex GitHubRepositoryPattern();

    private UpdateManager? CreateManager()
    {
        try
        {
            var manager = IsGitHubRepository(_repository)
                ? new UpdateManager(new GithubSource($"https://github.com/{_repository}", accessToken: null, prerelease: false))
                : new UpdateManager(_repository);
            return manager.IsInstalled ? manager : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Velopack is unavailable; updates fall back to the download page");
            return null;
        }
    }

    /// <summary>
    /// Check for a newer release. A failed check is reported as such — "no answer" must never
    /// be shown to the user as "you have the latest version".
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        if (_manager.Value is { } manager)
        {
            return await CheckInstallableAsync(manager, ct);
        }

        if (!IsGitHubRepository(_repository))
        {
            return new UpdateCheckResult(UpdateCheckOutcome.Failed,
                Error: Localization.LocalizationManager.Get("Update_FeedNeedsPortable"));
        }

        try
        {
            _logger.LogInformation("Checking for updates in {Repository}...", _repository);
            var release = await _httpClient.GetFromJsonAsync<GitHubRelease>(LatestReleaseApiUrl, ct);

            if (release is null || string.IsNullOrEmpty(release.TagName))
            {
                return new UpdateCheckResult(UpdateCheckOutcome.Failed, Error: "The release information was empty.");
            }

            if (!TryParseTag(release.TagName, out var latestVersion))
            {
                return new UpdateCheckResult(UpdateCheckOutcome.Failed, Error: $"Unrecognised release tag \"{release.TagName}\".");
            }

            if (latestVersion <= CurrentVersion)
            {
                _logger.LogInformation("Already up to date ({Version})", CurrentVersion);
                return new UpdateCheckResult(UpdateCheckOutcome.UpToDate);
            }

            _logger.LogInformation("Update available: {Latest}", latestVersion);

            // One release carries a ZIP per architecture: offer the one this copy runs as.
            var portableZips = (release.Assets ?? [])
                .Where(a => a.Name.Contains("portable", StringComparison.OrdinalIgnoreCase) &&
                            a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var downloadUrl = (portableZips.FirstOrDefault(a =>
                                   a.Name.Contains(RuntimeInformation.RuntimeIdentifier, StringComparison.OrdinalIgnoreCase))
                               ?? portableZips.FirstOrDefault())
                ?.BrowserDownloadUrl
                ?? release.HtmlUrl
                ?? ReleasesPageUrl;

            return new UpdateCheckResult(UpdateCheckOutcome.Available, new UpdateInfo(
                LatestVersion: latestVersion,
                ReleaseNotes: release.Body ?? string.Empty,
                DownloadUrl: downloadUrl,
                PublishedAt: release.PublishedAt));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // "Not Found" is GitHub's answer for a repository without releases — and for a private
            // one. Either way there is nothing to update to, which is not a broken connection.
            _logger.LogInformation("No published release in {Repository}", _repository);
            return new UpdateCheckResult(UpdateCheckOutcome.NoReleases);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _logger.LogWarning("Update check failed: {Msg}", ex.Message);
            return new UpdateCheckResult(UpdateCheckOutcome.Failed, Error: ex.Message);
        }
    }

    private async Task<UpdateCheckResult> CheckInstallableAsync(UpdateManager manager, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Checking for updates in {Feed} (installed {Version})...", _repository, manager.CurrentVersion);
            // Off the UI thread: Velopack reads the packages folder before it goes online (INV-06).
            var info = await Task.Run(() => manager.CheckForUpdatesAsync(), ct);
            _pending = info;
            if (info is null)
            {
                return new UpdateCheckResult(UpdateCheckOutcome.UpToDate);
            }

            var target = info.TargetFullRelease;
            _logger.LogInformation("Update available: {Latest}", target.Version);
            return new UpdateCheckResult(UpdateCheckOutcome.Available, new UpdateInfo(
                LatestVersion: new Version(target.Version.Major, target.Version.Minor, target.Version.Patch),
                ReleaseNotes: target.NotesMarkdown ?? string.Empty,
                DownloadUrl: ReleasesPageUrl,
                PublishedAt: default,
                CanInstall: true));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Velopack reports no network, a rate limit and an unreadable feed alike: the check did not work.
            _logger.LogWarning(ex, "Update check failed");
            return new UpdateCheckResult(UpdateCheckOutcome.Failed, Error: ex.Message);
        }
    }

    /// <summary>Downloads what the last check found: a delta when this copy holds the base package.</summary>
    public Task DownloadAsync(Action<int>? progress, CancellationToken ct = default)
    {
        var (manager, pending) = Pending();
        return Task.Run(() => manager.DownloadUpdatesAsync(pending, progress, ct), ct);
    }

    /// <summary>
    /// Hands over to Velopack's updater: it waits for this process to exit, swaps <c>current\</c> and
    /// starts the new version. The caller closes the window right after. Before calling, the session
    /// must be saved and the workspace windows closed — the updater kills what runs from
    /// <c>current\</c> and does not always manage to wait (TRAP-72).
    /// </summary>
    public void ApplyAfterExit()
    {
        var (manager, pending) = Pending();
        // Silent: the updater's own windows are English only and cannot follow the UI language.
        // The new version has to come back on the state folder this one was started with.
        string[]? restartArgs = PortableLayout.CustomDataDirectory is { } data ? [$"--data={data}"] : null;
        manager.WaitExitThenApplyUpdates(pending.TargetFullRelease, silent: true, restart: true, restartArgs);
    }

    /// <summary>
    /// The version that was downloaded but is not installed: the install stopped before the hand-over,
    /// or another program held files in <c>current\</c> and the updater gave up and started the old
    /// version again. Null when nothing is pending. Reads the packages folder: call it off the UI thread.
    /// </summary>
    public string? NotInstalledVersion()
    {
        try
        {
            return _manager.Value?.UpdatePendingRestart?.Version.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read Velopack's packages folder");
            return null;
        }
    }

    private (UpdateManager Manager, Velopack.UpdateInfo Pending) Pending() =>
        (_manager.Value ?? throw new InvalidOperationException("This copy cannot install updates."),
         _pending ?? throw new InvalidOperationException("No update was found to install."));

    /// <summary>Compatibility wrapper: the update if there is one, null for both "up to date" and "failed".</summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default) =>
        (await CheckAsync(ct)).Update;

    /// <summary>
    /// "v1.2.3", "1.2.3", "v1.2.3-beta.1" or "1.2.3+build" → 1.2.3. A pre-release suffix is dropped
    /// rather than making the whole tag unparseable.
    /// </summary>
    public static bool TryParseTag(string tag, out Version version)
    {
        var text = tag.Trim().TrimStart('v', 'V');
        var cut = text.IndexOfAny(['-', '+']);
        if (cut >= 0)
        {
            text = text[..cut];
        }

        return Version.TryParse(text, out version!);
    }

    /// <summary>
    /// True for an absolute http(s) URL. The address comes out of the network (a release's JSON), and
    /// handing anything else to the shell — a <c>file:</c> path, a custom protocol handler — would
    /// let a hostile or hijacked answer start a program.
    /// </summary>
    public static bool IsSafeDownloadUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    /// <summary>
    /// Opens a page in the default browser: a release's download page, or the project's pages from
    /// Help (<see cref="ProjectLinks"/>). Anything but http(s) is refused.
    /// </summary>
    public bool OpenInBrowser(string url)
    {
        if (!IsSafeDownloadUrl(url))
        {
            _logger.LogWarning("Refused to open a non-http(s) URL: {Url}", url);
            return false;
        }

        try
        {
            using var browser = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Cannot open URL: {Url}", url);
            return false;
        }
    }

    public void Dispose() => _httpClient.Dispose();

    // ──────────────────────────────────────────────────────────────────────
    // GitHub API DTOs

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }

        [JsonPropertyName("body")]
        public string? Body { get; init; }

        [JsonPropertyName("published_at")]
        public DateTime PublishedAt { get; init; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; init; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; init; }
    }
}

/// <summary>Information about an available update.</summary>
/// <param name="CanInstall">True when this copy installs it itself; false means "offer the download page".</param>
public sealed record UpdateInfo(
    Version LatestVersion,
    string ReleaseNotes,
    string DownloadUrl,
    DateTime PublishedAt,
    bool CanInstall = false);
