using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using TCV.App.Services;

namespace TCV.App.Tests;

/// <summary>
/// What the public repository and the release carry (ADR-0036): the bug form the program opens,
/// and the third-party notices every shipped package needs.
/// </summary>
public class PublicReleaseTests
{
    [Fact]
    public void BugReportUrl_OpensTheFormWithVersionAndSystemEscaped()
    {
        var url = ProjectLinks.BugReportUrl("0.4.0+1a2b3c4 (win-x64, portable)", "Windows 11 (10.0.26100), x64");

        url.Should().Be("https://github.com/gurinovichroman/TCVibe/issues/new?template=bug_report.yml"
                        + "&version=0.4.0%2B1a2b3c4%20%28win-x64%2C%20portable%29"
                        + "&os=Windows%2011%20%2810.0.26100%29%2C%20x64");
    }

    [Theory]
    [InlineData("0.4.0+1a2b3c4d5e6f7890abcdef", "win-x64", true, "0.4.0+1a2b3c4 (win-x64, portable)")]
    [InlineData("0.4.0", "win-x86", false, "0.4.0 (win-x86, dev build)")]
    [InlineData("0.4.0+abc", "win-x64", true, "0.4.0+abc (win-x64, portable)")]
    public void DescribeBuild_ShortensTheCommitAndSaysHowTheCopyRuns(string version, string runtime, bool portable, string expected)
    {
        ProjectLinks.DescribeBuild(version, runtime, portable).Should().Be(expected);
    }

    [Theory]
    [InlineData(26100, "Windows 11 (10.0.26100), x64")]
    [InlineData(22000, "Windows 11 (10.0.22000), x64")]
    [InlineData(19045, "Windows 10 (10.0.19045), x64")]
    public void DescribeSystem_TellsWindows11ByItsBuild(int build, string expected)
    {
        ProjectLinks.DescribeSystem(new Version(10, 0, build, 0), "x64").Should().Be(expected);
    }

    [Fact]
    public void UpdatesAndFeedback_UseTheSameRepository()
    {
        UpdateService.DefaultRepository.Should().Be(ProjectLinks.Repository);
    }

    [Fact]
    public void BugForm_HasTheFieldsTheProgramFillsIn()
    {
        // GitHub fills a field from the query only when the ids match: a renamed field would leave
        // the version and the system empty without any error.
        var form = File.ReadAllText(Path.Combine(SourceTree.Root, ".github", "ISSUE_TEMPLATE", ProjectLinks.BugTemplate));

        form.Should().MatchRegex(@"(?m)^\s*id:\s*version\s*$");
        form.Should().MatchRegex(@"(?m)^\s*id:\s*os\s*$");
    }

    [Fact]
    public void ThirdPartyNotices_NameEveryShippedPackage()
    {
        // MIT and Apache-2.0 travel with the binaries: a package added without its notice would ship
        // in breach of its licence.
        var notices = File.ReadAllText(Path.Combine(SourceTree.Root, "THIRD-PARTY-NOTICES.txt"));
        var shipped = Directory.EnumerateFiles(Path.Combine(SourceTree.Root, "src"), "*.csproj", SearchOption.AllDirectories)
            .SelectMany(project => Regex.Matches(File.ReadAllText(project), @"<PackageReference\s+Include=""([^""]+)""")
                .Select(m => m.Groups[1].Value))
            .Distinct()
            .ToList();

        shipped.Should().NotBeEmpty();
        // The whole name, not a part of a longer one: "Serilog.Sinks.File" in the list must not count
        // as an entry for "Serilog". A sentence may end right after the name.
        shipped.Where(package => !Regex.IsMatch(notices, $@"(?<![\w.-]){Regex.Escape(package)}(?![\w-]|\.\w)"))
            .Should().BeEmpty("every package the release ships needs its entry in THIRD-PARTY-NOTICES.txt");
    }
}
