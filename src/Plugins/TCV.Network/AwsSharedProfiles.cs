using System.IO;

namespace TCV.Network;

/// <summary>A profile of the AWS shared files: its static keys and, when the config names one, its region.</summary>
internal sealed record AwsProfile(S3Credentials Credentials, string? Region);

/// <summary>
/// Reads a profile from the files the AWS CLI keeps — <c>%USERPROFILE%\.aws\credentials</c> and
/// <c>config</c>, or wherever <c>AWS_SHARED_CREDENTIALS_FILE</c> and <c>AWS_CONFIG_FILE</c> point — so a
/// developer who already has <c>aws configure</c> done does not type the keys a second time.
/// </summary>
/// <remarks>
/// Only static keys. A profile that signs in through IAM Identity Center (SSO), an external process
/// or an assumed role would need the STS and SSO protocols; it is refused with a message that says
/// so, rather than ignored in favour of keys the CLI itself would not use.
/// </remarks>
internal static class AwsSharedProfiles
{
    public const string DefaultProfile = "default";

    /// <summary>Settings by which the CLI would sign in some other way than with the profile's keys.</summary>
    private static readonly (string Key, string Way)[] Unsupported =
    [
        ("sso_session", "AWS IAM Identity Center (SSO)"),
        ("sso_start_url", "AWS IAM Identity Center (SSO)"),
        ("sso_account_id", "AWS IAM Identity Center (SSO)"),
        ("credential_process", "an external credential process (credential_process)"),
        ("role_arn", "an assumed role (role_arn)"),
        ("credential_source", "an assumed role (credential_source)"),
        ("web_identity_token_file", "a web identity token (web_identity_token_file)"),
        ("login_session", "an AWS console sign-in (login_session)"),
    ];

    public static string DefaultCredentialsPath() =>
        FromEnvironment("AWS_SHARED_CREDENTIALS_FILE") ?? Path.Combine(AwsFolder(), "credentials");

    public static string DefaultConfigPath() =>
        FromEnvironment("AWS_CONFIG_FILE") ?? Path.Combine(AwsFolder(), "config");

    /// <summary>
    /// Loads <paramref name="profile"/> (<c>default</c> when empty). The credentials file wins over the
    /// config file, as in the CLI; the region comes from the profile's <c>region</c> setting.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The profile is missing, has no keys, or signs in some way other than with static keys.
    /// </exception>
    public static AwsProfile Load(string? profile, string credentialsPath, string configPath)
    {
        var name = string.IsNullOrWhiteSpace(profile) ? DefaultProfile : profile.Trim();
        var credentials = ReadIfPresent(credentialsPath);
        var config = ReadIfPresent(configPath);

        credentials.TryGetValue(name, out var fromCredentials);

        // In the config file a profile is "[profile name]"; only the default may also be plain "[default]".
        if (!config.TryGetValue("profile " + name, out var fromConfig) && name == DefaultProfile)
        {
            config.TryGetValue(DefaultProfile, out fromConfig);
        }

        if (fromCredentials is null && fromConfig is null)
        {
            throw new InvalidOperationException(
                $"The AWS profile \"{name}\" was not found in {credentialsPath} or {configPath}.");
        }

        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in new[] { fromConfig, fromCredentials })
        {
            if (section is null)
            {
                continue;
            }

            foreach (var (key, value) in section)
            {
                settings[key] = value;
            }
        }

        foreach (var (key, way) in Unsupported)
        {
            if (settings.ContainsKey(key))
            {
                throw new InvalidOperationException(
                    $"The AWS profile \"{name}\" signs in through {way}. Only profiles with static keys " +
                    "(aws_access_key_id and aws_secret_access_key) are supported: add keys to the profile, " +
                    "or type them into the connection.");
            }
        }

        var accessKey = settings.GetValueOrDefault("aws_access_key_id");
        var secretKey = settings.GetValueOrDefault("aws_secret_access_key");
        if (string.IsNullOrEmpty(accessKey) || string.IsNullOrEmpty(secretKey))
        {
            throw new InvalidOperationException(
                $"The AWS profile \"{name}\" has no aws_access_key_id and aws_secret_access_key.");
        }

        var token = settings.GetValueOrDefault("aws_session_token");
        var region = settings.GetValueOrDefault("region");
        return new AwsProfile(
            new S3Credentials(accessKey, secretKey, string.IsNullOrEmpty(token) ? null : token),
            string.IsNullOrEmpty(region) ? null : region);
    }

    /// <summary>
    /// The sections of an AWS ini file. Names keep their case (the CLI's profiles are case-sensitive);
    /// keys do not. Indented lines are the nested settings of the key above them
    /// (<c>s3 =</c> / <c>  addressing_style = path</c>) and are skipped.
    /// </summary>
    public static Dictionary<string, Dictionary<string, string>> Parse(string text)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        Dictionary<string, string>? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || char.IsWhiteSpace(line[0]))
            {
                continue;
            }

            if (line.StartsWith('#') || line.StartsWith(';'))
            {
                continue;
            }

            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                var name = string.Join(' ', trimmed[1..^1].Split(' ', StringSplitOptions.RemoveEmptyEntries));
                if (!sections.TryGetValue(name, out current))
                {
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    sections[name] = current;
                }

                continue;
            }

            var equals = trimmed.IndexOf('=');
            if (equals > 0 && current is not null)
            {
                current[trimmed[..equals].Trim()] = trimmed[(equals + 1)..].Trim();
            }
        }

        return sections;
    }

    private static Dictionary<string, Dictionary<string, string>> ReadIfPresent(string path) =>
        File.Exists(path) ? Parse(File.ReadAllText(path)) : new(StringComparer.Ordinal);

    private static string AwsFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aws");

    private static string? FromEnvironment(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value : null;
}
