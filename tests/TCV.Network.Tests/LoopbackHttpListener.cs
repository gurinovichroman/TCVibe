using System.Net;
using System.Net.Sockets;

namespace TCV.Network.Tests;

/// <summary>
/// How a stub server in these tests claims a loopback port for <see cref="HttpListener"/> (TRAP-112).
/// </summary>
/// <remarks>
/// A port from a probe — bind port 0, read the number, let go — is only a likely candidate. Between
/// the probe and <see cref="HttpListener.Start"/>, which takes tens of milliseconds on a busy machine,
/// the next probe anywhere on the machine can be handed the same number: in a parallel test host, or
/// as the local end of an outgoing connection. The loser used to fail its test in the constructor
/// with 183 «conflicts with an existing registration» (the same prefix) or 32 «being used by another
/// process» (a socket holds the port). Only <c>Start</c> claims the port, so a failed start is how a
/// taken port is found out, and the answer is another candidate, whatever the error code: a machine
/// where nothing works still fails loudly once the attempts run out.
/// </remarks>
internal static class LoopbackHttpListener
{
    private const int DefaultAttempts = 16;

    /// <summary>Starts a listener on the root of a free loopback port.</summary>
    public static (HttpListener Listener, int Port) Start() => Start(ProbeFreePort, DefaultAttempts);

    /// <param name="nextPort">Gives the next port to try; a test seam for ports known to be taken.</param>
    /// <param name="attempts">How many ports to try before the last failure is let through.</param>
    internal static (HttpListener Listener, int Port) Start(Func<int> nextPort, int attempts)
    {
        for (var attempt = 1; ; attempt++)
        {
            var port = nextPort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return (listener, port);
            }
            catch (HttpListenerException)
            {
                listener.Close();
                if (attempt >= attempts)
                {
                    throw;
                }
            }
        }
    }

    private static int ProbeFreePort()
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
