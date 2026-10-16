using System.Buffers.Text;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TCV.Network.Cloud;

/// <summary>
/// The authorization code flow with PKCE (RFC 7636) for one connection: the browser sign-in, the
/// code exchange, and an access token kept in memory and refreshed shortly before it expires.
/// </summary>
/// <remarks>
/// Only <see cref="SignInAsync"/> opens a browser. Every other path — a listing, a transfer the queue
/// runs on its own — either has a usable token or fails with <see cref="SignInRequiredException"/>.
/// The refresh token lives in the connection (the book encrypts it); the access token never leaves
/// this object.
/// </remarks>
internal sealed class OAuthClient : IDisposable
{
    /// <summary>A token this close to its expiry is refreshed first: the request must not die in flight.</summary>
    internal static readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(1);

    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    // Swapped whole, never mutated: a reader sees either the old token or the new one.
    private TokenState? _token;
    private volatile bool _refreshRejected;

    public OAuthClient(OAuthProvider provider, ConnectionInfo connection, HttpClient http, TimeProvider? time = null)
    {
        Provider = provider;
        Connection = connection;
        _http = http;
        _time = time ?? TimeProvider.System;
    }

    public OAuthProvider Provider { get; }

    public ConnectionInfo Connection { get; }

    /// <summary>How long a token request may take.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the browser sign-in may take before it is given up.</summary>
    public TimeSpan SignInTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Opens the authorization page; the default hands it to the user's default browser.</summary>
    public Func<Uri, CancellationToken, Task> OpenBrowser { get; set; } = OpenInDefaultBrowser;

    /// <summary>
    /// True when the next request would need the browser: no client id, or neither a live access
    /// token nor a refresh token the provider still accepts.
    /// </summary>
    public bool NeedsSignIn =>
        MissingConfiguration() is not null
        || (!IsFresh(_token) && (string.IsNullOrEmpty(Connection.RefreshToken) || _refreshRejected));

    /// <summary>
    /// A live access token: the one in memory, or a new one from the refresh token. Concurrent
    /// callers share one refresh.
    /// </summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (_token is { } current && IsFresh(current))
        {
            return current.AccessToken;
        }

        EnsureConfigured();
        await _refreshGate.WaitAsync(ct);
        try
        {
            // Another caller may have refreshed while this one waited at the gate.
            if (_token is { } again && IsFresh(again))
            {
                return again.AccessToken;
            }

            if (string.IsNullOrEmpty(Connection.RefreshToken) || _refreshRejected)
            {
                throw new SignInRequiredException(Provider.Name);
            }

            await RefreshAsync(ct);
            return _token!.AccessToken;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>
    /// Forgets <paramref name="accessToken"/> after the provider rejected it, so the next call refreshes.
    /// Only that token: a request that failed with an older one must not discard the newer one another
    /// caller has just obtained.
    /// </summary>
    public void Invalidate(string accessToken)
    {
        var current = _token;
        if (current is not null && current.AccessToken == accessToken)
        {
            Interlocked.CompareExchange(ref _token, null, current);
        }
    }

    /// <summary>
    /// The provider refused even a freshly refreshed token: the grant is no longer good (revoked,
    /// scopes withdrawn), and only a new sign-in helps.
    /// </summary>
    public void MarkSignInRequired()
    {
        _refreshRejected = true;
        _token = null;
    }

    /// <summary>
    /// Opens the sign-in page, waits for the browser to come back to the loopback listener, and trades
    /// the code for tokens. The refresh token goes into the connection and
    /// <see cref="ConnectionInfo.SecretsChanged"/> is raised.
    /// </summary>
    public async Task SignInAsync(CancellationToken ct)
    {
        EnsureConfigured();

        var verifier = Pkce.CreateVerifier();
        var state = Pkce.CreateState();
        using var listener = LoopbackListener.Start(Provider.RedirectHost, Provider.RedirectPort);
        var redirectUri = listener.RedirectUri;
        var authorizationUri = BuildAuthorizationUri(redirectUri, state, Pkce.ChallengeFor(verifier));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SignInTimeout);
        try
        {
            await OpenBrowser(authorizationUri, timeout.Token);
            using var callback = await listener.WaitForCallbackAsync(timeout.Token);
            try
            {
                var code = ReadAuthorizationCode(callback.Query, state, Provider.Name);
                await ExchangeCodeAsync(code, redirectUri, verifier, timeout.Token);
                await callback.RespondAsync(true, $"Signed in to {Provider.Name}",
                    "You can close this tab and return to TCVibe.");
            }
            catch (Exception ex)
            {
                await callback.RespondAsync(false, $"{Provider.Name} sign-in failed",
                    ex.Message + " You can close this tab.");
                throw;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The {Provider.Name} sign-in did not finish within {SignInTimeout.TotalMinutes:0} minutes.");
        }
    }

    /// <summary>The page the browser is sent to.</summary>
    internal Uri BuildAuthorizationUri(string redirectUri, string state, string codeChallenge)
    {
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("response_type", "code"),
            new("client_id", Connection.ClientId!.Trim()),
            new("redirect_uri", redirectUri),
            new("scope", Provider.Scope),
            new("state", state),
            new("code_challenge", codeChallenge),
            new("code_challenge_method", "S256"),
        };
        parameters.AddRange(Provider.ExtraAuthorizationParameters);

        var query = string.Join('&',
            parameters.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        return new Uri(Provider.AuthorizationEndpoint + "?" + query);
    }

    /// <summary>
    /// The code from the browser's return trip, after checking that it answers this sign-in: a
    /// <c>state</c> that differs is a reply to some other request (or a forged one) and is refused.
    /// </summary>
    internal static string ReadAuthorizationCode(NameValueCollection query, string expectedState, string provider)
    {
        if (!string.Equals(query["state"], expectedState, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The {provider} sign-in reply does not match the request (state mismatch). Sign in again.");
        }

        if (query["error"] is { } error)
        {
            throw new InvalidOperationException(error == "access_denied"
                ? $"The {provider} sign-in was cancelled in the browser."
                : $"{provider} refused the sign-in: {query["error_description"] ?? error}");
        }

        return query["code"] is { Length: > 0 } code
            ? code
            : throw new InvalidOperationException($"{provider} sent no authorization code. Sign in again.");
    }

    /// <summary>Trades the authorization code for tokens.</summary>
    internal async Task ExchangeCodeAsync(string code, string redirectUri, string verifier, CancellationToken ct)
    {
        var form = BaseForm("authorization_code");
        form.Add(new("code", code));
        form.Add(new("redirect_uri", redirectUri));
        form.Add(new("code_verifier", verifier));

        var reply = await PostTokenRequestAsync(form, ct);
        if (reply.Error is not null)
        {
            throw TokenError(reply);
        }

        _refreshRejected = false;
        Accept(reply);
        if (!string.IsNullOrEmpty(reply.RefreshToken))
        {
            Connection.RefreshToken = reply.RefreshToken;
        }

        RaiseSecretsChanged();
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var form = BaseForm("refresh_token");
        form.Add(new("refresh_token", Connection.RefreshToken!));
        if (Provider.SendsScopeOnRefresh)
        {
            form.Add(new("scope", Provider.Scope));
        }

        var reply = await PostTokenRequestAsync(form, ct);
        if (reply.Error is "invalid_grant")
        {
            // Revoked, expired or issued to another client: no retry can fix it, only a new sign-in.
            // The dead token is dropped from the connection too (and from the book, via SecretsChanged):
            // kept, the next connect — a new file system, with no memory of this refusal — would trust it,
            // find no reason to open the browser and fail the same way again.
            _refreshRejected = true;
            Connection.RefreshToken = null;
            RaiseSecretsChanged();
            throw new SignInRequiredException(Provider.Name);
        }

        if (reply.Error is not null)
        {
            throw TokenError(reply);
        }

        Accept(reply);

        // Microsoft hands out a new refresh token with every refresh; the old one must not be the one
        // the book keeps.
        if (!string.IsNullOrEmpty(reply.RefreshToken) && reply.RefreshToken != Connection.RefreshToken)
        {
            Connection.RefreshToken = reply.RefreshToken;
            RaiseSecretsChanged();
        }
    }

    private List<KeyValuePair<string, string>> BaseForm(string grantType)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", grantType),
            new("client_id", Connection.ClientId!.Trim()),
        };
        if (!string.IsNullOrWhiteSpace(Connection.ClientSecret))
        {
            form.Add(new("client_secret", Connection.ClientSecret.Trim()));
        }

        return form;
    }

    private async Task<TokenReply> PostTokenRequestAsync(List<KeyValuePair<string, string>> form, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        string body;
        System.Net.HttpStatusCode status;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Provider.TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(form),
            };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            status = response.StatusCode;
            body = await response.Content.ReadAsStringAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"{Provider.Name} did not answer the sign-in request within {RequestTimeout.TotalSeconds:0} seconds.");
        }

        return TokenReply.Parse(body, status);
    }

    private void Accept(TokenReply reply)
    {
        if (string.IsNullOrEmpty(reply.AccessToken))
        {
            throw new InvalidOperationException($"{Provider.Name} answered the sign-in without an access token.");
        }

        // A provider that names no lifetime gets the usual hour.
        var lifetime = reply.ExpiresIn is > 0 ? TimeSpan.FromSeconds(reply.ExpiresIn.Value) : TimeSpan.FromHours(1);
        _token = new TokenState(reply.AccessToken, _time.GetUtcNow() + lifetime);
    }

    private Exception TokenError(TokenReply reply) => reply.Error switch
    {
        "invalid_client" or "unauthorized_client" => new InvalidOperationException(
            $"{Provider.Name} does not accept the client id{(Provider.RequiresClientSecret ? " or secret" : string.Empty)} " +
            $"of this connection ({reply.Error}: {reply.Description ?? "no details"}). {Provider.RegistrationHint}"),
        _ => new InvalidOperationException(
            $"{Provider.Name} refused the sign-in ({reply.Error}): {reply.Description ?? "no details"}"),
    };

    internal void RaiseSecretsChanged()
    {
        try
        {
            Connection.SecretsChanged?.Invoke(Connection);
        }
        catch (Exception)
        {
            // Storing the token is the owner's business: its failure must not fail the request that
            // has just obtained one. The token stays usable in memory.
        }
    }

    private bool IsFresh(TokenState? token) => token is not null && token.ExpiresAt - ExpirySkew > _time.GetUtcNow();

    private string? MissingConfiguration()
    {
        if (string.IsNullOrWhiteSpace(Connection.ClientId))
        {
            return $"{Provider.Name} needs the client id of an application you register yourself. {Provider.RegistrationHint}";
        }

        if (Provider.RequiresClientSecret && string.IsNullOrWhiteSpace(Connection.ClientSecret))
        {
            return $"{Provider.Name} also needs the client secret of the registered application. {Provider.RegistrationHint}";
        }

        return null;
    }

    private void EnsureConfigured()
    {
        if (MissingConfiguration() is { } problem)
        {
            throw new InvalidOperationException(problem);
        }
    }

    private static Task OpenInDefaultBrowser(Uri uri, CancellationToken ct)
    {
        using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    public void Dispose() => _refreshGate.Dispose();

    private sealed record TokenState(string AccessToken, DateTimeOffset ExpiresAt);

    /// <summary>The fields of a token endpoint's answer that matter here, success or error.</summary>
    internal sealed record TokenReply(
        string? AccessToken, string? RefreshToken, long? ExpiresIn, string? Error, string? Description)
    {
        public static TokenReply Parse(string body, System.Net.HttpStatusCode status)
        {
            try
            {
                using var json = JsonDocument.Parse(body);
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return Failed(body, status);
                }

                var reply = new TokenReply(
                    CloudJson.String(root, "access_token"),
                    CloudJson.String(root, "refresh_token"),
                    CloudJson.Int64(root, "expires_in"),
                    CloudJson.String(root, "error"),
                    CloudJson.String(root, "error_description"));
                return reply.Error is null && reply.AccessToken is null ? Failed(body, status) : reply;
            }
            catch (JsonException)
            {
                return Failed(body, status);
            }
        }

        private static TokenReply Failed(string body, System.Net.HttpStatusCode status) =>
            new(null, null, null, $"http_{(int)status}", CloudJson.Excerpt(body));
    }
}

/// <summary>PKCE values (RFC 7636): a random verifier, its S256 challenge, and the anti-forgery state.</summary>
internal static class Pkce
{
    /// <summary>32 random bytes, base64url: 43 characters, the minimum length the RFC allows and plenty of entropy.</summary>
    public static string CreateVerifier() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static string ChallengeFor(string verifier) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string CreateState() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
}
