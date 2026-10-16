using System.Collections.Specialized;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TCV.Network.Cloud;

/// <summary>
/// The local end of a browser sign-in: listens on a loopback address until the provider sends the
/// browser back with a code (RFC 8252, section 7.3).
/// </summary>
/// <remarks>
/// <see cref="HttpListener"/> on <c>127.0.0.1</c> or <c>localhost</c> needs no URL reservation and
/// no elevation. Stopping it is the only way to end a pending <see cref="HttpListener.GetContextAsync"/>,
/// so cancellation and the sign-in timeout stop the listener.
/// </remarks>
internal sealed class LoopbackListener : IDisposable
{
    private readonly HttpListener _listener;

    private LoopbackListener(HttpListener listener, string redirectUri)
    {
        _listener = listener;
        RedirectUri = redirectUri;
    }

    /// <summary>The address the browser is sent back to, with no path: <c>http://127.0.0.1:50123</c>.</summary>
    public string RedirectUri { get; }

    /// <summary>
    /// Starts listening on <paramref name="host"/>. A fixed <paramref name="port"/> is the one the
    /// provider has registered; 0 takes any free port.
    /// </summary>
    public static LoopbackListener Start(string host, int port)
    {
        for (var attempt = 0; ; attempt++)
        {
            var chosen = port != 0 ? port : FreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://{host}:{chosen}/");
            try
            {
                listener.Start();
                return new LoopbackListener(listener, $"http://{host}:{chosen}");
            }
            catch (HttpListenerException) when (port == 0 && attempt < 4)
            {
                // Another program took the free port between the probe and the start: pick again.
                listener.Close();
            }
            catch (HttpListenerException ex)
            {
                listener.Close();
                throw new IOException(
                    $"Cannot wait for the sign-in on {host}:{chosen}: {ex.Message}" +
                    (port != 0 ? " Another program may be using that port; close it and sign in again." : string.Empty),
                    ex);
            }
        }
    }

    /// <summary>
    /// Waits for the request that carries the provider's answer (a <c>code</c> or an <c>error</c>).
    /// Anything else the browser asks for meanwhile — a favicon — gets a 404 and the wait goes on.
    /// </summary>
    public async Task<LoopbackCallback> WaitForCallbackAsync(CancellationToken ct)
    {
        using var stop = ct.Register(() =>
        {
            try
            {
                _listener.Stop();
            }
            catch (ObjectDisposedException)
            {
                // Already closed: nothing left to wake.
            }
        });

        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ct.IsCancellationRequested
                                       && ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                throw new OperationCanceledException(ct);
            }

            var query = context.Request.QueryString;
            if (query["code"] is null && query["error"] is null)
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                context.Response.Close();
                continue;
            }

            return new LoopbackCallback(context);
        }
    }

    public void Dispose() => _listener.Close();

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }
}

/// <summary>
/// The browser's return trip, held open until the sign-in has finished so that the page it shows
/// tells the truth: a code exchange that fails says so in the tab, not only in the program.
/// </summary>
internal sealed class LoopbackCallback(HttpListenerContext context) : IDisposable
{
    private bool _responded;

    public NameValueCollection Query => context.Request.QueryString;

    /// <summary>Answers the browser with a small page; a tab that has already gone away is ignored.</summary>
    public async Task RespondAsync(bool success, string title, string message)
    {
        if (_responded)
        {
            return;
        }

        _responded = true;
        var html =
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>" + WebUtility.HtmlEncode(title) + "</title>" +
            "<style>body{font-family:Segoe UI,system-ui,sans-serif;margin:3em auto;max-width:36em;padding:0 1em;" +
            "color:#1f2328;background:#fff}@media(prefers-color-scheme:dark){body{color:#e6edf3;background:#0d1117}}" +
            "h1{font-size:1.4em}</style></head><body><h1>" + WebUtility.HtmlEncode(title) + "</h1><p>" +
            WebUtility.HtmlEncode(message) + "</p></body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        try
        {
            context.Response.StatusCode = success ? (int)HttpStatusCode.OK : (int)HttpStatusCode.BadRequest;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
        {
            // The browser closed the tab first: the sign-in itself is unaffected.
        }
    }

    public void Dispose()
    {
        if (!_responded)
        {
            _responded = true;
            try
            {
                context.Response.Abort();
            }
            catch (ObjectDisposedException)
            {
                // The listener went first.
            }
        }
    }
}
