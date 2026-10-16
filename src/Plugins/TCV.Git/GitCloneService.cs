using System.IO;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;

namespace TCV.Git;

/// <summary>
/// How far a clone has got. <paramref name="Percent"/> is -1 while the server is still counting
/// objects and no total is known yet — a progress bar that sits at 0% and a bar that is honestly
/// indeterminate say different things.
/// </summary>
public sealed record GitCloneProgress(string Stage, int Percent, string Detail = "");

/// <summary>
/// Credentials for one clone, held in memory only. Nothing here is ever written to settings or
/// logs: for a private https repository the "password" is an access token, and a token on disk in
/// plain text is worse than asking again.
/// </summary>
public sealed record GitCredentials(string UserName, string Secret)
{
    public bool IsEmpty => string.IsNullOrEmpty(Secret);

    /// <summary>The record prints as its type only — the secret must not reach a log through ToString.</summary>
    public override string ToString() => nameof(GitCredentials);
}

/// <summary>Turns libgit2's failure text into something a person can act on.</summary>
public static class GitCloneErrors
{
    /// <summary>
    /// Classifies a clone failure. Hosts answer 401/403 (or "not found") for a private repository the
    /// caller may not see, so the authentication message deliberately covers both "wrong token" and
    /// "no such repository" — git itself cannot tell them apart from outside.
    /// </summary>
    public static GitOperationResult Describe(string url, string rawMessage)
    {
        var message = rawMessage ?? string.Empty;

        if (Contains(message, "authentication") || Contains(message, "401") || Contains(message, "403")
            || Contains(message, "credentials") || Contains(message, "too many redirects"))
            return GitOperationResult.Fail(
                "Authentication failed. A private repository needs a user name and an access token "
                + "(not the account password), or the repository does not exist.",
                GitFailureKind.Authentication);

        if (Contains(message, "certificate") || Contains(message, "ssl") || Contains(message, "tls"))
        {
            return GitOperationResult.Fail(
                "The server certificate was rejected: " + message, GitFailureKind.Certificate, message);
        }

        // GitCloneService refuses SSH links before cloning; this is the net under that check.
        if (url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)
            || (url.Contains('@') && !url.Contains("://", StringComparison.Ordinal)))
        {
            return GitOperationResult.Fail(
                "SSH access failed. SSH keys are not supported here; use the https URL with an access token. "
                + message,
                GitFailureKind.SshUnsupported,
                GitCloneTarget.Resolve(url).Host);
        }

        // Unclassified, so the UI wraps libgit2's text in a sentence of its own instead of showing it bare.
        return GitOperationResult.Fail(message, GitFailureKind.Unexpected, message);
    }

    private static bool Contains(string text, string value) =>
        text.Contains(value, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Clone-from-URL: paste a link, get a checkout, with progress. The last mile of "set up a new
/// project" that otherwise sends you to a terminal — and the natural partner of the
/// <c>*.tcvproj</c> file, which is meant to be opened right after the clone lands.
/// </summary>
public sealed class GitCloneService
{
    private readonly ILogger<GitCloneService> _logger;

    public GitCloneService(ILogger<GitCloneService> logger) => _logger = logger;

    /// <summary>
    /// Clones <paramref name="url"/> into <paramref name="targetDirectory"/>, reporting transfer and
    /// checkout progress. On success the message is the path of the new working directory.
    /// </summary>
    /// <remarks>
    /// Cancellation runs through the native transfer callback — returning false from it aborts the
    /// clone — because libgit2 does not poll a token. Two honest limits follow: the checkout phase
    /// reports progress but cannot be interrupted, and a clone from a local path may never call the
    /// transfer callback at all (libgit2 copies the objects directly), so it effectively runs to
    /// completion. A cancelled or failed clone leaves a partial directory behind, which is removed
    /// here: the next attempt would otherwise fail with "directory is not empty" and blame the user.
    /// </remarks>
    public Task<GitOperationResult> CloneAsync(
        string url,
        string targetDirectory,
        IProgress<GitCloneProgress>? progress = null,
        CancellationToken ct = default,
        GitCredentials? credentials = null) =>
        Task.Run(() =>
        {
            // The callbacks below are the only cancellation the native side offers, and a clone that
            // never reaches them (a local source) would ignore an already-cancelled token entirely.
            if (ct.IsCancellationRequested) return GitOperationResult.Fail("Cancelled.", GitFailureKind.Cancelled);

            // An SSH link never works here (no SSH transport): a well-known host's is cloned over its
            // https twin, any other is refused before a round trip that can only fail.
            var resolved = GitCloneTarget.Resolve(url);
            if (resolved.Status == GitCloneUrlStatus.Invalid)
            {
                return GitOperationResult.Fail($"Not a repository URL: {url}", GitFailureKind.InvalidUrl, url);
            }

            if (resolved.Status == GitCloneUrlStatus.SshUnsupported)
            {
                return GitOperationResult.Fail(
                    $"SSH is not supported here, and no https address is known for {resolved.Host}.",
                    GitFailureKind.SshUnsupported, resolved.Host);
            }

            var cloneUrl = resolved.Url;   // trimmed; the https twin of a rewritten SSH link

            if (string.IsNullOrWhiteSpace(targetDirectory))
                return GitOperationResult.Fail("No target directory.", GitFailureKind.NoTargetDirectory);

            var target = Path.GetFullPath(targetDirectory);
            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
                return GitOperationResult.Fail($"Directory is not empty: {target}", GitFailureKind.DirectoryNotEmpty, target);

            var createdHere = !Directory.Exists(target);

            try
            {
                var options = new CloneOptions();
                options.FetchOptions.OnTransferProgress = p =>
                {
                    // TotalObjects is 0 until the server finishes enumerating; dividing then would
                    // report a steady 0% for the part of the clone that often takes longest.
                    var percent = p.TotalObjects > 0
                        ? (int)(100L * p.ReceivedObjects / p.TotalObjects)
                        : -1;
                    progress?.Report(new GitCloneProgress(
                        "receiving", percent, $"{p.ReceivedObjects}/{p.TotalObjects}"));
                    return !ct.IsCancellationRequested;
                };
                options.OnCheckoutProgress = (_, completed, total) =>
                {
                    var percent = total > 0 ? (int)(100L * completed / total) : -1;
                    progress?.Report(new GitCloneProgress("checkout", percent, $"{completed}/{total}"));
                };

                if (credentials is { IsEmpty: false })
                {
                    // Offered once: libgit2 calls the provider again after every rejection, and
                    // answering with the same wrong token forever is a hang, not a retry.
                    var offered = false;
                    options.FetchOptions.CredentialsProvider = (_, _, _) =>
                    {
                        if (offered) return null;
                        offered = true;
                        return new UsernamePasswordCredentials
                        {
                            Username = credentials.UserName,
                            Password = credentials.Secret,
                        };
                    };
                }

                progress?.Report(new GitCloneProgress("connecting", -1));
                var repoPath = Repository.Clone(cloneUrl, target, options);

                _logger.LogInformation("Cloned {Url} into {Path}", RedactUrl(cloneUrl), target);
                progress?.Report(new GitCloneProgress("done", 100));

                // Repository.Clone returns the .git directory; the panel wants the checkout.
                return GitOperationResult.Ok(WorkingDirectoryOf(repoPath, target));
            }
            catch (Exception ex)
            {
                CleanUpPartialClone(target, createdHere);

                if (ct.IsCancellationRequested)
                {
                    _logger.LogInformation("Clone of {Url} was cancelled", RedactUrl(cloneUrl));
                    return GitOperationResult.Fail("Cancelled.", GitFailureKind.Cancelled);
                }

                _logger.LogWarning(ex, "Clone of {Url} failed", RedactUrl(cloneUrl));
                return GitCloneErrors.Describe(cloneUrl, ex.Message);
            }
        }, CancellationToken.None);   // the token drives the callbacks, not the Task itself

    /// <summary>Drops a <c>user:token@</c> part so an inline credential never reaches the log.</summary>
    public static string RedactUrl(string url)
    {
        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0) return url;

        var authorityStart = scheme + 3;
        var at = url.IndexOf('@', authorityStart);
        var slash = url.IndexOf('/', authorityStart);
        return at >= 0 && (slash < 0 || at < slash) ? url[..authorityStart] + "***@" + url[(at + 1)..] : url;
    }

    private static string WorkingDirectoryOf(string? repositoryPath, string fallback)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath)) return fallback;

        var trimmed = Path.GetFullPath(repositoryPath).TrimEnd('\\', '/');
        return trimmed.EndsWith($"{Path.DirectorySeparatorChar}.git", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(trimmed) ?? fallback
            : fallback;
    }

    /// <summary>
    /// Removes the half-written checkout so a retry starts from a clean slate. A directory the user
    /// picked is emptied rather than deleted — it was verified empty before the clone started, so
    /// everything in it now is this attempt's debris, and leaving it there is exactly what makes the
    /// obvious next move ("try again") fail with "directory is not empty".
    /// </summary>
    private void CleanUpPartialClone(string target, bool createdHere)
    {
        if (!Directory.Exists(target)) return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
            {
                var attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }

            if (createdHere)
            {
                Directory.Delete(target, recursive: true);
            }
            else
            {
                foreach (var directory in Directory.EnumerateDirectories(target))
                    Directory.Delete(directory, recursive: true);
                foreach (var file in Directory.EnumerateFiles(target))
                    File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not clean up partial clone at {Path}", target);
        }
    }
}
