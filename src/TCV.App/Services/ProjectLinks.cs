namespace TCV.App.Services;

/// <summary>
/// Where the program sends people (ADR-0036): the public repository, its bug form and its
/// discussions. Releases come from the same repository (<see cref="UpdateService"/>). Nothing here
/// goes online — it only builds the addresses a browser opens, so the program sends no telemetry.
/// </summary>
public static class ProjectLinks
{
    /// <summary>The public repository: releases, issues and discussions.</summary>
    public const string Repository = "gurinovichroman/TCVibe";

    /// <summary>The bug form in <c>.github/ISSUE_TEMPLATE</c>; its field ids are the query keys of <see cref="BugReportUrl"/>.</summary>
    public const string BugTemplate = "bug_report.yml";

    public const string RepositoryUrl = "https://github.com/" + Repository;

    /// <summary>Questions and ideas go here, so that issues stay bug reports.</summary>
    public const string DiscussionsUrl = RepositoryUrl + "/discussions";

    /// <summary>
    /// The bug form with the version and the system already filled in: GitHub fills an issue form's
    /// fields from query parameters named after their ids.
    /// </summary>
    public static string BugReportUrl(string version, string system) =>
        $"{RepositoryUrl}/issues/new?template={BugTemplate}" +
        $"&version={Uri.EscapeDataString(version)}&os={Uri.EscapeDataString(system)}";

    /// <summary>
    /// "0.4.0+1a2b3c4 (win-x64, portable)": the version with its commit cut to the short hash, and
    /// how this copy runs — a portable copy updates itself, a dev build does not.
    /// </summary>
    public static string DescribeBuild(string informationalVersion, string runtime, bool portable)
    {
        var version = informationalVersion;
        var plus = version.IndexOf('+');
        if (plus >= 0 && version.Length - plus - 1 > 7)
        {
            version = version[..(plus + 8)];
        }

        return $"{version} ({runtime}, {(portable ? "portable" : "dev build")})";
    }

    /// <summary>
    /// "Windows 11 (10.0.26300), x64". Windows 11 still reports 10.0, its builds start at 22000; the
    /// program needs Windows 10 or later, so nothing older is named.
    /// </summary>
    public static string DescribeSystem(Version osVersion, string architecture)
    {
        var name = osVersion.Build >= 22000 ? "Windows 11" : "Windows 10";
        return $"{name} ({osVersion.Major}.{osVersion.Minor}.{osVersion.Build}), {architecture}";
    }
}
