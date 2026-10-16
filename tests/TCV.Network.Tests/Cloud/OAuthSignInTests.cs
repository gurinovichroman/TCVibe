using System.Collections.Specialized;
using System.Net;
using System.Web;
using FluentAssertions;
using TCV.Core.Domain;
using TCV.Network.Cloud;

namespace TCV.Network.Tests.Cloud;

/// <summary>The browser sign-in (PKCE, loopback) and the token's life afterwards.</summary>
public sealed class OAuthSignInTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    // ── PKCE and the authorization request ───────────────────────────────────

    [Fact]
    public void ChallengeFor_Rfc7636Example_MatchesTheSpecification()
    {
        // RFC 7636, appendix B.
        Pkce.ChallengeFor("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk")
            .Should().Be("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM");
    }

    [Fact]
    public void CreateVerifier_Always_IsUrlSafeAndLongEnough()
    {
        var verifier = Pkce.CreateVerifier();

        verifier.Should().HaveLength(43).And.MatchRegex("^[A-Za-z0-9_-]+$");
        Pkce.CreateVerifier().Should().NotBe(verifier);
    }

    [Fact]
    public void BuildAuthorizationUri_Google_CarriesEverythingTheProviderNeeds()
    {
        using var auth = new OAuthClient(OAuthProvider.GoogleDrive, Kit.Connection(NetworkProtocol.GoogleDrive), new HttpClient());

        var uri = auth.BuildAuthorizationUri("http://127.0.0.1:50123", "the-state", "the-challenge");
        var query = HttpUtility.ParseQueryString(uri.Query);

        uri.GetLeftPart(UriPartial.Path).Should().Be("https://accounts.google.com/o/oauth2/v2/auth");
        query["response_type"].Should().Be("code");
        query["client_id"].Should().Be("client-id");
        query["redirect_uri"].Should().Be("http://127.0.0.1:50123");
        query["scope"].Should().Be("https://www.googleapis.com/auth/drive");
        query["state"].Should().Be("the-state");
        query["code_challenge"].Should().Be("the-challenge");
        query["code_challenge_method"].Should().Be("S256");
        query["access_type"].Should().Be("offline", "without it Google hands out no refresh token");
    }

    [Fact]
    public void BuildAuthorizationUri_DropboxAndOneDrive_AskForOfflineAccess()
    {
        using var dropbox = new OAuthClient(OAuthProvider.Dropbox, Kit.Connection(NetworkProtocol.Dropbox), new HttpClient());
        using var oneDrive = new OAuthClient(OAuthProvider.OneDrive, Kit.Connection(NetworkProtocol.OneDrive), new HttpClient());

        var dropboxQuery = HttpUtility.ParseQueryString(dropbox.BuildAuthorizationUri("http://localhost:53119", "s", "c").Query);
        var oneDriveQuery = HttpUtility.ParseQueryString(oneDrive.BuildAuthorizationUri("http://localhost:50000", "s", "c").Query);

        dropboxQuery["token_access_type"].Should().Be("offline");
        dropboxQuery["scope"].Should().Contain("files.content.write");
        oneDriveQuery["scope"].Should().Be("offline_access Files.ReadWrite User.Read");
        OAuthProvider.Dropbox.RedirectUriToRegister.Should().Be("http://localhost:53119");
        OAuthProvider.OneDrive.RedirectUriToRegister.Should().Be("http://localhost");
    }

    [Fact]
    public void ReadAuthorizationCode_StateMismatch_IsRefused()
    {
        var reply = new NameValueCollection { ["code"] = "c", ["state"] = "forged" };

        var act = () => OAuthClient.ReadAuthorizationCode(reply, "expected", "Dropbox");

        act.Should().Throw<InvalidOperationException>().WithMessage("*state mismatch*");
    }

    [Fact]
    public void ReadAuthorizationCode_UserDeclined_SaysTheSignInWasCancelled()
    {
        var reply = new NameValueCollection { ["error"] = "access_denied", ["state"] = "s" };

        var act = () => OAuthClient.ReadAuthorizationCode(reply, "s", "OneDrive");

        act.Should().Throw<InvalidOperationException>().WithMessage("The OneDrive sign-in was cancelled in the browser.");
    }

    // ── The loopback round trip ──────────────────────────────────────────────

    [Fact]
    public async Task SignInAsync_BrowserComesBackWithCode_ExchangesItAndStoresTheRefreshToken()
    {
        var http = new CloudHttp(r => r.Uri.AbsolutePath.EndsWith("/about", StringComparison.Ordinal)
            ? Kit.Json("""{"user":{"emailAddress":"dev@example.com"}}""")
            : Kit.Unexpected(r));
        http.Token = _ => Kit.Json("""{"access_token":"access-new","expires_in":3599,"refresh_token":"refresh-new"}""");
        var connection = Kit.Connection(NetworkProtocol.GoogleDrive, refreshToken: null);
        var saved = 0;
        connection.SecretsChanged = _ => Interlocked.Increment(ref saved);

        Uri? authorization = null;
        Task<HttpResponseMessage>? page = null;
        using var browser = new HttpClient(new HttpClientHandler { UseProxy = false });
        using var fs = new GoogleDriveFileSystem(connection, new HttpClient(http))
        {
            OpenBrowser = (uri, _) =>
            {
                authorization = uri;
                var query = HttpUtility.ParseQueryString(uri.Query);
                page = Task.Run(async () =>
                {
                    // A browser asks for a favicon too; the listener must keep waiting for the code.
                    (await browser.GetAsync(query["redirect_uri"] + "/favicon.ico")).StatusCode.Should().Be(HttpStatusCode.NotFound);
                    return await browser.GetAsync($"{query["redirect_uri"]}/?state={Uri.EscapeDataString(query["state"]!)}&code=the-code");
                });
                return Task.CompletedTask;
            },
        };

        fs.NeedsSignIn.Should().BeTrue();
        await fs.SignInAsync(CancellationToken.None);

        var sent = HttpUtility.ParseQueryString(authorization!.Query);
        var exchange = http.TokenRequests.Should().ContainSingle().Subject.Form;
        exchange["grant_type"].Should().Be("authorization_code");
        exchange["code"].Should().Be("the-code");
        exchange["redirect_uri"].Should().Be(sent["redirect_uri"]);
        exchange["client_secret"].Should().Be("client-secret");
        Pkce.ChallengeFor(exchange["code_verifier"]!).Should().Be(sent["code_challenge"], "the verifier must be the one the challenge was made from");

        connection.RefreshToken.Should().Be("refresh-new");
        connection.Username.Should().Be("dev@example.com");
        fs.DisplayName.Should().Be("dev@example.com");
        saved.Should().BeGreaterThan(0);
        fs.NeedsSignIn.Should().BeFalse();

        using var shown = await page!;
        shown.StatusCode.Should().Be(HttpStatusCode.OK);
        (await shown.Content.ReadAsStringAsync()).Should().Contain("You can close this tab");
    }

    [Fact]
    public async Task SignInAsync_StateMismatch_FailsAndTellsTheBrowser()
    {
        var http = new CloudHttp(Kit.Unexpected);
        Task<HttpResponseMessage>? page = null;
        using var browser = new HttpClient(new HttpClientHandler { UseProxy = false });
        using var fs = new OneDriveFileSystem(Kit.Connection(NetworkProtocol.OneDrive, refreshToken: null), new HttpClient(http))
        {
            OpenBrowser = (uri, _) =>
            {
                var redirect = HttpUtility.ParseQueryString(uri.Query)["redirect_uri"];
                page = browser.GetAsync($"{redirect}/?state=someone-else&code=stolen");
                return Task.CompletedTask;
            },
        };

        var act = () => fs.SignInAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*state mismatch*");
        http.TokenRequests.Should().BeEmpty("a code from another request is never exchanged");
        using var shown = await page!;
        shown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SignInAsync_NoAnswerInTime_TimesOutAndStopsListening()
    {
        string? redirect = null;
        using var fs = new GoogleDriveFileSystem(Kit.Connection(NetworkProtocol.GoogleDrive, refreshToken: null), new HttpClient(new CloudHttp(Kit.Unexpected)))
        {
            SignInTimeout = TimeSpan.FromMilliseconds(300),
            OpenBrowser = (uri, _) =>
            {
                redirect = HttpUtility.ParseQueryString(uri.Query)["redirect_uri"];
                return Task.CompletedTask;
            },
        };

        var act = () => fs.SignInAsync(CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
        using var browser = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var late = () => browser.GetAsync($"{redirect}/?state=x&code=y");
        await late.Should().ThrowAsync<HttpRequestException>("the listener is gone once the sign-in gave up");
    }

    [Fact]
    public async Task SignInAsync_NoClientId_ExplainsHowToRegisterAnApp()
    {
        var connection = Kit.Connection(NetworkProtocol.OneDrive);
        connection.ClientId = " ";
        var opened = false;
        using var fs = new OneDriveFileSystem(connection, new HttpClient(new CloudHttp(Kit.Unexpected)))
        {
            OpenBrowser = (_, _) =>
            {
                opened = true;
                return Task.CompletedTask;
            },
        };

        fs.NeedsSignIn.Should().BeTrue();
        var signIn = () => fs.SignInAsync(CancellationToken.None);
        var list = () => fs.ListAsync(VfsPath.Root);

        await signIn.Should().ThrowAsync<InvalidOperationException>().WithMessage("*client id*http://localhost*");
        await list.Should().ThrowAsync<InvalidOperationException>().WithMessage("*client id*");
        opened.Should().BeFalse();
    }

    [Fact]
    public async Task SignInAsync_GoogleWithoutClientSecret_SaysTheSecretIsNeeded()
    {
        var connection = Kit.Connection(NetworkProtocol.GoogleDrive);
        connection.ClientSecret = null;
        using var fs = new GoogleDriveFileSystem(connection, new HttpClient(new CloudHttp(Kit.Unexpected)));

        var act = () => fs.SignInAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*client secret*");
    }

    [Fact]
    public async Task ExchangeCodeAsync_ClientRejected_PointsAtTheRegistration()
    {
        var http = new CloudHttp(Kit.Unexpected)
        {
            Token = _ => Kit.Json("""{"error":"invalid_client","error_description":"Unauthorized"}""", HttpStatusCode.Unauthorized),
        };
        using var auth = new OAuthClient(OAuthProvider.Dropbox, Kit.Connection(NetworkProtocol.Dropbox, null), new HttpClient(http));

        var act = () => auth.ExchangeCodeAsync("code", "http://localhost:53119", "verifier", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*does not accept the client id*App Console*");
    }

    // ── Tokens after the sign-in ─────────────────────────────────────────────

    [Fact]
    public async Task GetAccessTokenAsync_FreshToken_IsReusedAndRefreshedOnlyNearItsExpiry()
    {
        var time = new FakeTime(Start);
        var http = new CloudHttp(Kit.Unexpected);
        using var auth = new OAuthClient(OAuthProvider.Dropbox, Kit.Connection(NetworkProtocol.Dropbox), new HttpClient(http), time);

        var first = await auth.GetAccessTokenAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(50));
        var reused = await auth.GetAccessTokenAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(9.5));
        var renewed = await auth.GetAccessTokenAsync(CancellationToken.None);

        first.Should().Be("access-1");
        reused.Should().Be("access-1");
        renewed.Should().Be("access-2", "a token within a minute of its expiry is refreshed before the request, not after it fails");
        var refresh = http.TokenRequests[0].Form;
        refresh["grant_type"].Should().Be("refresh_token");
        refresh["refresh_token"].Should().Be("refresh-1");
        refresh["client_id"].Should().Be("client-id");
        refresh["client_secret"].Should().BeNull("Dropbox's PKCE apps have no secret");
    }

    [Fact]
    public async Task GetAccessTokenAsync_RefreshTokenRotated_StoresTheNewOneAndRaisesSecretsChanged()
    {
        var http = new CloudHttp(Kit.Unexpected)
        {
            Token = _ => Kit.Json("""{"access_token":"a","expires_in":3600,"refresh_token":"refresh-2"}"""),
        };
        var connection = Kit.Connection(NetworkProtocol.OneDrive);
        var raised = new List<ConnectionInfo>();
        connection.SecretsChanged = raised.Add;
        using var auth = new OAuthClient(OAuthProvider.OneDrive, connection, new HttpClient(http));

        await auth.GetAccessTokenAsync(CancellationToken.None);

        connection.RefreshToken.Should().Be("refresh-2");
        raised.Should().ContainSingle().Which.Should().BeSameAs(connection);
        http.TokenRequests.Single().Form["scope"].Should().Be("offline_access Files.ReadWrite User.Read");
    }

    [Fact]
    public async Task GetAccessTokenAsync_ManyCallersAtOnce_ShareOneRefresh()
    {
        var gate = new TaskCompletionSource();
        var http = new CloudHttp(Kit.Unexpected);
        using var auth = new OAuthClient(OAuthProvider.Dropbox, Kit.Connection(NetworkProtocol.Dropbox), new HttpClient(new DelayingHandler(http, gate.Task)));

        var callers = Enumerable.Range(0, 5).Select(_ => auth.GetAccessTokenAsync(CancellationToken.None)).ToList();
        gate.SetResult();
        var tokens = await Task.WhenAll(callers);

        tokens.Should().AllBe("access-1");
        http.TokenRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task GetAccessTokenAsync_NoRefreshToken_FailsWithoutOpeningABrowser()
    {
        var opened = false;
        using var fs = new DropboxFileSystem(Kit.Connection(NetworkProtocol.Dropbox, refreshToken: null), new HttpClient(new CloudHttp(Kit.Unexpected)))
        {
            OpenBrowser = (_, _) =>
            {
                opened = true;
                return Task.CompletedTask;
            },
        };

        fs.NeedsSignIn.Should().BeTrue();
        var act = () => fs.ListAsync(VfsPath.Root);

        (await act.Should().ThrowAsync<SignInRequiredException>()).Which.Provider.Should().Be("Dropbox");
        opened.Should().BeFalse("only an explicit sign-in may open a browser");
    }

    [Fact]
    public async Task GetAccessTokenAsync_RefreshTokenRevoked_RequiresANewSignIn()
    {
        var http = new CloudHttp(Kit.Unexpected)
        {
            Token = _ => Kit.Json("""{"error":"invalid_grant","error_description":"Token has been expired or revoked."}""", HttpStatusCode.BadRequest),
        };
        var connection = Kit.Connection(NetworkProtocol.GoogleDrive);
        var raised = new List<ConnectionInfo>();
        connection.SecretsChanged = raised.Add;
        using var fs = Kit.Google(http, connection);

        fs.NeedsSignIn.Should().BeFalse();
        var act = () => fs.GetEntryAsync(new VfsPath("/a"));

        await act.Should().ThrowAsync<SignInRequiredException>();
        fs.NeedsSignIn.Should().BeTrue();

        // The next connect makes a new file system that remembers nothing of this refusal: it must see
        // that the stored token is dead, or it would trust it and never open the browser.
        connection.RefreshToken.Should().BeNull();
        raised.Should().ContainSingle().Which.Should().BeSameAs(connection);
        using var next = Kit.Google(new CloudHttp(Kit.Unexpected), connection);
        next.NeedsSignIn.Should().BeTrue();
    }

    /// <summary>Holds every request until the gate opens, so concurrent callers really overlap.</summary>
    private sealed class DelayingHandler(HttpMessageHandler inner, Task gate) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await gate;
            return await base.SendAsync(request, ct);
        }
    }
}
