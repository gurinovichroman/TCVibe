using System.Net;
using System.Net.Sockets;
using FluentAssertions;

namespace TCV.Network.Tests;

/// <summary>TRAP-112: a port that turns out to be taken costs another attempt, not the test that wanted a server.</summary>
public sealed class LoopbackHttpListenerTests
{
    [Fact]
    public void Start_PortHeldBySocket_MovesOnToAFreePort()
    {
        // HttpListener answers 32 «being used by another process» for this one.
        using var socket = HoldPortWithSocket(out var held);
        var tried = new List<int>();

        var (listener, port) = LoopbackHttpListener.Start(TakenThenFree(held, tried), attempts: 16);

        using (listener)
        {
            tried[0].Should().Be(held);
            port.Should().NotBe(held);
            listener.IsListening.Should().BeTrue();
        }
    }

    [Fact]
    public void Start_PrefixRegisteredByAnotherListener_MovesOnToAFreePort()
    {
        // What two test hosts probing the same number run into: 183 «conflicts with an existing registration».
        var (other, taken) = LoopbackHttpListener.Start();
        using (other)
        {
            var tried = new List<int>();

            var (listener, port) = LoopbackHttpListener.Start(TakenThenFree(taken, tried), attempts: 16);

            using (listener)
            {
                tried[0].Should().Be(taken);
                port.Should().NotBe(taken);
                listener.IsListening.Should().BeTrue();
            }
        }
    }

    [Fact]
    public void Start_EveryPortTaken_ReportsTheFailureOnceAttemptsRunOut()
    {
        using var socket = HoldPortWithSocket(out var held);
        var tried = 0;

        var act = () => LoopbackHttpListener.Start(
            () =>
            {
                tried++;
                return held;
            },
            attempts: 3);

        act.Should().Throw<HttpListenerException>();
        tried.Should().Be(3);
    }

    [Fact]
    public async Task Start_ManyAtOnce_EachGetsItsOwnWorkingPort()
    {
        // The probe of one start can return the number another start is about to claim: this is that
        // race on purpose. Each listener must really own its port, so each answers its own request.
        var starts = Enumerable.Range(0, 16).Select(_ => Task.Run(() => LoopbackHttpListener.Start())).ToList();
        var started = await Task.WhenAll(starts);
        try
        {
            started.Select(s => s.Port).Should().OnlyHaveUniqueItems();
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            foreach (var (listener, port) in started)
            {
                var request = client.GetAsync($"http://127.0.0.1:{port}/ping");
                var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(30));
                context.Response.StatusCode = 204;
                context.Response.Close();
                (await request.WaitAsync(TimeSpan.FromSeconds(30))).StatusCode.Should().Be(HttpStatusCode.NoContent);
            }
        }
        finally
        {
            foreach (var (listener, _) in started)
            {
                listener.Close();
            }
        }
    }

    private static TcpListener HoldPortWithSocket(out int port)
    {
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        port = ((IPEndPoint)socket.LocalEndpoint).Port;
        return socket;
    }

    /// <summary>Offers the taken port first, then what the OS gives out, recording every offer.</summary>
    private static Func<int> TakenThenFree(int taken, List<int> tried) => () =>
    {
        int port;
        if (tried.Count == 0)
        {
            port = taken;
        }
        else
        {
            using var probe = HoldPortWithSocket(out port);
        }

        tried.Add(port);
        return port;
    };
}
