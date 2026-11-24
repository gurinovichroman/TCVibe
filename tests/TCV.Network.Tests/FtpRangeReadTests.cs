using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using TCV.Core.Domain;

namespace TCV.Network.Tests;

/// <summary>
/// The viewer's jump on FTP against a tiny real server on a socket (TRAP-106): the offset reaches the
/// server as REST, the bytes start there, and a server that refuses REST falls back to the whole file.
/// </summary>
public sealed class FtpRangeReadTests : IDisposable
{
    private const int Size = 1_000_000;

    private static readonly byte[] Data = Enumerable.Range(0, Size).Select(i => (byte)(i % 251)).ToArray();

    private readonly FtpRangeStubServer _server = new();

    public FtpRangeReadTests() => _server.Files["/big.log"] = Data;

    public void Dispose() => _server.Dispose();

    private FtpFileSystem Connect() => new(new ConnectionInfo
    {
        Protocol = NetworkProtocol.Ftp,
        Host = "127.0.0.1",
        Port = _server.Port,
        Username = "tester",
        Password = "secret",
    });

    private static async Task<byte[]> ReadAsync(Stream stream, int count)
    {
        var buffer = new byte[count];
        var total = 0;
        while (total < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, count - total));
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return buffer[..total];
    }

    [Fact]
    public async Task OpenReadFromAsync_ServerTakesRest_TheOffsetReachesItAndTheBytesStartThere()
    {
        using var fs = Connect();

        var (stream, offset, _, validator) = await fs.OpenReadFromAsync(new VfsPath("/big.log"), 900_000, null, default);
        await using (stream)
        {
            offset.Should().Be(900_000);
            validator.Should().BeNull("FTP has no validator");
            (await ReadAsync(stream, 4096)).Should().Equal(Data.Skip(900_000).Take(4096));
        }

        _server.Commands.Should().Contain("REST 900000");
        _server.Commands.Should().Contain(c => c == "RETR /big.log");
        _server.BytesSent.Should().BeLessThan(Size, "End on a big file must not bring what lies before it");
    }

    [Fact]
    public async Task OpenReadFromAsync_ServerRefusesRest_TheWholeFileComesAndSaysSo()
    {
        _server.RefuseRest = true;
        using var fs = Connect();

        var (stream, offset, _, _) = await fs.OpenReadFromAsync(new VfsPath("/big.log"), 900_000, null, default);
        await using (stream)
        {
            offset.Should().Be(0, "the caller then reads up to the offset itself, as it always did");
            (await ReadAsync(stream, 4096)).Should().Equal(Data.Take(4096));
        }

        _server.Commands.Should().Contain("REST 900000");
    }

    [Fact]
    public async Task OpenReadFromAsync_ServerRefusedRestOnce_IsNotAskedAgain()
    {
        _server.RefuseRest = true;
        using var fs = Connect();

        var (first, _, _, _) = await fs.OpenReadFromAsync(new VfsPath("/big.log"), 900_000, null, default);
        await first.DisposeAsync();
        var refused = _server.Commands.Count(c => c.StartsWith("REST ", StringComparison.Ordinal));

        var (second, offset, _, _) = await fs.OpenReadFromAsync(new VfsPath("/big.log"), 800_000, null, default);
        await second.DisposeAsync();

        offset.Should().Be(0);
        _server.Commands.Count(c => c.StartsWith("REST ", StringComparison.Ordinal)).Should().Be(refused);
    }

    [Fact]
    public async Task OpenReadFromAsync_NoSuchFile_FailsAsAPlainOpenWouldAndKeepsTryingRest()
    {
        using var fs = Connect();

        var missing = () => fs.OpenReadFromAsync(new VfsPath("/gone.log"), 100, null, default);

        await missing.Should().ThrowAsync<Exception>();

        // The refusal was the file's, not REST's: the next jump still asks.
        var (stream, offset, _, _) = await fs.OpenReadFromAsync(new VfsPath("/big.log"), 900_000, null, default);
        await stream.DisposeAsync();
        offset.Should().Be(900_000);
    }

    [Fact]
    public async Task OpenReadFromAsync_ZeroOffset_SendsNoRest()
    {
        using var fs = Connect();

        var (stream, offset, _, _) = await fs.OpenReadFromAsync(new VfsPath("/big.log"), 0, null, default);
        await stream.DisposeAsync();

        offset.Should().Be(0);
        _server.Commands.Should().NotContain(c => c.StartsWith("REST ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpenReadFromAsync_NameTheServerWouldGetWrong_IsRefusedBeforeAnythingIsSent()
    {
        using var fs = Connect();

        // TRAP-94: «notes » would reach the server as «notes».
        var refused = () => fs.OpenReadFromAsync(new VfsPath("/notes "), 100, null, default);

        await refused.Should().ThrowAsync<NotSupportedException>();
        _server.Commands.Should().NotContain(c => c.StartsWith("RETR ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpenReadFromAsync_ThroughAReadOnlyConnection_TheOffsetStillReachesTheServer()
    {
        using var fs = new ReadOnlyFileSystem(Connect());

        var (stream, offset, _, _) = await fs.OpenReadFromAsync(new VfsPath("/big.log"), 900_000, null, default);
        await stream.DisposeAsync();

        offset.Should().Be(900_000);
        _server.Commands.Should().Contain("REST 900000");
    }
}

/// <summary>
/// An FTP server just big enough for a download: login, passive data connections, SIZE, REST and RETR.
/// Answers the way a real one does when the client stops reading: the data socket breaks, the reply says 426.
/// </summary>
internal sealed class FtpRangeStubServer : IDisposable
{
    private readonly TcpListener _control = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<string> _commands = [];
    private long _bytesSent;

    public FtpRangeStubServer()
    {
        _control.Start();
        Port = ((IPEndPoint)_control.LocalEndpoint).Port;
        _ = AcceptAsync();
    }

    public int Port { get; }

    public Dictionary<string, byte[]> Files { get; } = [];

    /// <summary>Answers REST with 502, as a server without resume does.</summary>
    public bool RefuseRest { get; set; }

    public long BytesSent => Interlocked.Read(ref _bytesSent);

    /// <summary>Everything received, across all connections.</summary>
    public IReadOnlyList<string> Commands
    {
        get
        {
            lock (_commands)
            {
                return [.. _commands];
            }
        }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _control.AcceptTcpClientAsync(_stop.Token);
                _ = ServeAsync(client);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Stopped.
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
                await writer.WriteLineAsync("220 stub ready");

                TcpListener? passive = null;
                long restart = 0;
                while (await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    lock (_commands)
                    {
                        _commands.Add(line);
                    }

                    var space = line.IndexOf(' ');
                    var verb = (space < 0 ? line : line[..space]).ToUpperInvariant();
                    var argument = space < 0 ? string.Empty : line[(space + 1)..];
                    switch (verb)
                    {
                        case "USER":
                            await writer.WriteLineAsync("331 password please");
                            break;
                        case "PASS":
                            await writer.WriteLineAsync("230 in");
                            break;
                        case "FEAT":
                            await writer.WriteLineAsync("211-Features:\r\n SIZE\r\n REST STREAM\r\n EPSV\r\n211 End");
                            break;
                        case "SYST":
                            await writer.WriteLineAsync("215 UNIX Type: L8");
                            break;
                        case "PWD":
                            await writer.WriteLineAsync("257 \"/\"");
                            break;
                        case "TYPE":
                        case "OPTS":
                            await writer.WriteLineAsync("200 ok");
                            break;
                        case "NOOP":
                            await writer.WriteLineAsync("200 ok");
                            break;
                        case "EPSV":
                            passive = new TcpListener(IPAddress.Loopback, 0);
                            passive.Start();
                            await writer.WriteLineAsync($"229 Entering Extended Passive Mode (|||{((IPEndPoint)passive.LocalEndpoint).Port}|)");
                            break;
                        case "SIZE":
                            await writer.WriteLineAsync(Files.TryGetValue(argument, out var sized) ? $"213 {sized.Length}" : "550 no such file");
                            break;
                        case "REST":
                            if (RefuseRest)
                            {
                                await writer.WriteLineAsync("502 REST not implemented");
                            }
                            else
                            {
                                restart = long.Parse(argument, System.Globalization.CultureInfo.InvariantCulture);
                                await writer.WriteLineAsync($"350 Restarting at {restart}");
                            }

                            break;
                        case "RETR":
                            if (!Files.TryGetValue(argument, out var data) || passive is null)
                            {
                                await writer.WriteLineAsync("550 no such file");
                                passive?.Stop();
                                passive = null;
                                break;
                            }

                            await writer.WriteLineAsync("150 here it comes");
                            var sent = await SendAsync(passive, data, restart);
                            await writer.WriteLineAsync(sent ? "226 done" : "426 connection closed; transfer aborted");
                            passive.Stop();
                            passive = null;
                            restart = 0;
                            break;
                        case "QUIT":
                            await writer.WriteLineAsync("221 bye");
                            return;
                        default:
                            await writer.WriteLineAsync("502 not here");
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // The client went away.
            }
        }
    }

    /// <summary>True when everything went out; false when the client closed the data connection first.</summary>
    private async Task<bool> SendAsync(TcpListener passive, byte[] data, long from)
    {
        using var socket = await passive.AcceptTcpClientAsync(_stop.Token);
        var stream = socket.GetStream();
        try
        {
            const int Chunk = 32 * 1024;
            for (var position = (int)from; position < data.Length; position += Chunk)
            {
                var count = Math.Min(Chunk, data.Length - position);
                await stream.WriteAsync(data.AsMemory(position, count), _stop.Token);
                Interlocked.Add(ref _bytesSent, count);
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _control.Stop();
    }
}
