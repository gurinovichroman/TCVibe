namespace TCV.Network.Cloud;

/// <summary>
/// What one provider's browser sign-in needs: where the user is sent, where the code is traded for
/// tokens, what is asked for and which loopback address the browser comes back to.
/// </summary>
/// <remarks>
/// No application is registered on the program's behalf: the owner registers one with the provider
/// and types its client id into the connection. <see cref="RedirectUriToRegister"/> is what that
/// registration has to list, and <see cref="RegistrationHint"/> where to do it.
/// </remarks>
public sealed class OAuthProvider
{
    /// <summary>The port the Dropbox sign-in comes back to: Dropbox matches redirect URIs exactly, port included.</summary>
    public const int DropboxRedirectPort = 53119;

    /// <summary>The provider as the user knows it ("Google Drive"); also the default display name of the drive.</summary>
    public required string Name { get; init; }

    public required Uri AuthorizationEndpoint { get; init; }

    public required Uri TokenEndpoint { get; init; }

    /// <summary>Space-separated scopes asked for in the authorization request.</summary>
    public required string Scope { get; init; }

    /// <summary>Provider-specific parameters of the authorization request (offline access, consent prompt).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> ExtraAuthorizationParameters { get; init; } = [];

    /// <summary><c>127.0.0.1</c> or <c>localhost</c>: whichever host the provider accepts for a loopback redirect.</summary>
    public required string RedirectHost { get; init; }

    /// <summary>The loopback port; 0 picks any free one, for providers that ignore the port of a loopback redirect.</summary>
    public int RedirectPort { get; init; }

    /// <summary>True when the token endpoint wants the client secret even with PKCE (Google's desktop clients).</summary>
    public bool RequiresClientSecret { get; init; }

    /// <summary>True when a refresh names the scopes again (Microsoft issues the token per resource).</summary>
    public bool SendsScopeOnRefresh { get; init; }

    /// <summary>Where and how to register the application, in one English sentence for an error message.</summary>
    public required string RegistrationHint { get; init; }

    /// <summary>The redirect URI to enter in the provider's app registration.</summary>
    public string RedirectUriToRegister =>
        RedirectPort == 0 ? $"http://{RedirectHost}" : $"http://{RedirectHost}:{RedirectPort}";

    /// <summary>
    /// Google Drive: a "Desktop app" OAuth client. Desktop clients accept a loopback redirect on any
    /// port without registering it, but their token requests still carry the client secret.
    /// <c>prompt=consent</c> makes a repeated sign-in hand out a refresh token again — Google gives
    /// one only with the consent screen.
    /// </summary>
    public static OAuthProvider GoogleDrive { get; } = new()
    {
        Name = "Google Drive",
        AuthorizationEndpoint = new Uri("https://accounts.google.com/o/oauth2/v2/auth"),
        TokenEndpoint = new Uri("https://oauth2.googleapis.com/token"),
        Scope = "https://www.googleapis.com/auth/drive",
        ExtraAuthorizationParameters = [new("access_type", "offline"), new("prompt", "consent")],
        RedirectHost = "127.0.0.1",
        RequiresClientSecret = true,
        RegistrationHint =
            "Create an OAuth client of type \"Desktop app\" in the Google Cloud Console (APIs & Services, " +
            "Credentials), enable the Google Drive API, and enter the client id and client secret in the connection.",
    };

    /// <summary>
    /// Dropbox: a scoped app with PKCE and no secret. Dropbox matches the redirect URI exactly, so the
    /// sign-in listens on a fixed port that the app registration lists.
    /// </summary>
    public static OAuthProvider Dropbox { get; } = new()
    {
        Name = "Dropbox",
        AuthorizationEndpoint = new Uri("https://www.dropbox.com/oauth2/authorize"),
        TokenEndpoint = new Uri("https://api.dropboxapi.com/oauth2/token"),
        Scope = "account_info.read files.metadata.read files.metadata.write files.content.read files.content.write",
        ExtraAuthorizationParameters = [new("token_access_type", "offline")],
        RedirectHost = "localhost",
        RedirectPort = DropboxRedirectPort,
        RegistrationHint =
            $"Create a scoped app in the Dropbox App Console, add the redirect URI http://localhost:{DropboxRedirectPort}, " +
            "allow the files.* and account_info.read permissions, and enter its app key as the client id in the connection.",
    };

    /// <summary>
    /// OneDrive: a public client on the Microsoft identity platform's <c>common</c> endpoint, so both
    /// personal and work accounts sign in. A registered <c>http://localhost</c> accepts any port.
    /// Microsoft rotates the refresh token on every use.
    /// </summary>
    public static OAuthProvider OneDrive { get; } = new()
    {
        Name = "OneDrive",
        AuthorizationEndpoint = new Uri("https://login.microsoftonline.com/common/oauth2/v2.0/authorize"),
        TokenEndpoint = new Uri("https://login.microsoftonline.com/common/oauth2/v2.0/token"),
        Scope = "offline_access Files.ReadWrite User.Read",
        ExtraAuthorizationParameters = [new("prompt", "select_account")],
        RedirectHost = "localhost",
        SendsScopeOnRefresh = true,
        RegistrationHint =
            "Register an application in the Microsoft Entra admin center (App registrations) for personal and " +
            "work accounts, add the platform \"Mobile and desktop applications\" with the redirect URI " +
            "http://localhost, and enter its application (client) id in the connection.",
    };
}
