using FluentAssertions;

namespace TCV.Network.Tests;

/// <summary><see cref="AwsSharedProfiles"/>: the AWS CLI's credentials and config files.</summary>
public sealed class AwsSharedProfilesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tcv-aws-" + Guid.NewGuid().ToString("N"));

    public AwsSharedProfilesTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string CredentialsPath => Path.Combine(_folder, "credentials");

    private string ConfigPath => Path.Combine(_folder, "config");

    private AwsProfile Load(string? profile, string credentials, string config = "")
    {
        File.WriteAllText(CredentialsPath, credentials);
        File.WriteAllText(ConfigPath, config);
        return AwsSharedProfiles.Load(profile, CredentialsPath, ConfigPath);
    }

    [Fact]
    public void Load_NamedProfile_ReadsItsKeysTokenAndRegion()
    {
        var profile = Load("dev",
            "[default]\naws_access_key_id = AKIADEFAULT\naws_secret_access_key = defaultsecret\n\n" +
            "[dev]\r\naws_access_key_id=AKIADEV\r\naws_secret_access_key = dev/secret+key\r\naws_session_token = TOKEN==\r\n",
            "[default]\nregion = us-west-2\n[profile dev]\nregion = eu-central-1\ns3 =\n  addressing_style = path\n");

        profile.Credentials.Should().Be(new S3Credentials("AKIADEV", "dev/secret+key", "TOKEN=="));
        profile.Region.Should().Be("eu-central-1");
    }

    [Fact]
    public void Load_EmptyName_IsTheDefaultProfile()
    {
        var profile = Load(null,
            "# comment\n[default]\naws_access_key_id = AKIADEFAULT\naws_secret_access_key = s\n",
            "[default]\nregion = ap-south-1\n");

        profile.Credentials.AccessKeyId.Should().Be("AKIADEFAULT");
        profile.Credentials.SessionToken.Should().BeNull();
        profile.Region.Should().Be("ap-south-1");
    }

    [Fact]
    public void Load_KeysOnlyInTheConfigFile_AreFoundThere()
    {
        var profile = Load("ci", string.Empty,
            "[profile ci]\naws_access_key_id = AKIACI\naws_secret_access_key = s\nregion = eu-north-1\n");

        profile.Credentials.AccessKeyId.Should().Be("AKIACI");
        profile.Region.Should().Be("eu-north-1");
    }

    [Fact]
    public void Load_ConfigSectionWithoutProfilePrefix_IsNotTheNamedProfile()
    {
        // In the config file only [default] may go without "profile "; [ci] there is not the profile ci.
        var act = () => Load("ci", string.Empty, "[ci]\naws_access_key_id = AKIACI\naws_secret_access_key = s\n");

        act.Should().Throw<InvalidOperationException>().WithMessage("*\"ci\" was not found*");
    }

    [Theory]
    [InlineData("sso_session = my-sso\nsso_account_id = 123\n", "*IAM Identity Center (SSO)*")]
    [InlineData("sso_start_url = https://x.awsapps.com/start\n", "*IAM Identity Center (SSO)*")]
    [InlineData("credential_process = /usr/bin/get-creds\n", "*credential_process*")]
    [InlineData("role_arn = arn:aws:iam::123:role/x\nsource_profile = default\n", "*assumed role (role_arn)*")]
    public void Load_ProfileThatSignsInAnotherWay_IsRefusedWithAMessageThatSaysSo(string settings, string expected)
    {
        var act = () => Load("work", "[default]\naws_access_key_id = A\naws_secret_access_key = s\n", "[profile work]\n" + settings);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage(expected)
            .WithMessage("*Only profiles with static keys*");
    }

    [Fact]
    public void Load_StaticKeysWithARoleInTheConfig_IsRefusedBecauseTheCliWouldAssumeTheRole()
    {
        var act = () => Load("work",
            "[work]\naws_access_key_id = A\naws_secret_access_key = s\n",
            "[profile work]\nrole_arn = arn:aws:iam::123:role/x\n");

        act.Should().Throw<InvalidOperationException>().WithMessage("*role_arn*");
    }

    [Fact]
    public void Load_MissingProfile_NamesItAndTheFiles()
    {
        var act = () => Load("nope", "[default]\naws_access_key_id = A\naws_secret_access_key = s\n");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*\"nope\" was not found in {CredentialsPath}*");
    }

    [Fact]
    public void Load_ProfileWithoutSecret_SaysWhatIsMissing()
    {
        var act = () => Load("half", "[half]\naws_access_key_id = A\n");

        act.Should().Throw<InvalidOperationException>().WithMessage("*no aws_access_key_id and aws_secret_access_key*");
    }

    [Fact]
    public void Load_NoFilesAtAll_ReportsTheProfileMissing()
    {
        var act = () => AwsSharedProfiles.Load("default", Path.Combine(_folder, "none"), Path.Combine(_folder, "none2"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*not found*");
    }

    [Fact]
    public void Parse_ProfileNames_KeepTheirCaseAndCollapseSpaces()
    {
        var sections = AwsSharedProfiles.Parse("[profile   Dev]\nRegion = x\n[dev]\nregion = y\n");

        sections.Keys.Should().BeEquivalentTo("profile Dev", "dev");
        sections["profile Dev"]["region"].Should().Be("x");
    }
}
