using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TCV.Network.Tests;

/// <summary>
/// As much of an FTP server as the logic around a reply needs: a login, a passive data connection, STOR
/// and the commands FluentFTP sends on the way there. A test decides what the server answers and when, so
/// "the server is still writing" and "the disk is full" can be played without a real one. Loopback only.
/// </summary>
internal sealed class FtpStubServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<(int Connection, string Line)> _commands = new();
    private readonly ConcurrentDictionary<int, TcpClient> _open = new();
    private readonly ConcurrentDictionary<int, StreamWriter> _writers = new();
    private readonly ConcurrentDictionary<string, long> _received = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _stored = new(StringComparer.Ordinal);
    private int _connections;
    private int _closed;

    public FtpStubServer()
    {
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    /// <summary>The reply to a finished STOR: «226 …», or the refusal a full disk gives.</summary>
    public string FinalReply { get; set; } = "226 Transfer complete";

    /// <summary>What a RETR ends with. Anything but 2xx is a server giving up.</summary>
    public string DownloadFinalReply { get; set; } = "226 Transfer complete";

    /// <summary>How much of a file a RETR sends before it ends; null is all of it.</summary>
    public long? DownloadSendsOnly { get; set; }

    /// <summary>Holds the reply to a RETR back after the data connection has closed.</summary>
    public TaskCompletionSource? DownloadReplyGate { get; set; }

    /// <summary>Set: the final reply waits for this, as a server still writing the file would.</summary>
    public TaskCompletionSource? FinalReplyGate { get; set; }

    /// <summary>Sent before the final reply: a server that reports progress on the control connection.</summary>
    public string? ExtraPreliminaryReply { get; set; }

    /// <summary>The server says nothing at all after a STOR.</summary>
    public bool NeverRepliesToStor { get; set; }

    /// <summary>The reply to PASS.</summary>
    public string LoginReply { get; set; } = "230 Logged in";

    /// <summary>More control connections than this are turned away with a 421, as a per-user limit does.</summary>
    public int MaxConnections { get; set; } = int.MaxValue;

    /// <summary>What a client connects with.</summary>
    public ConnectionInfo Connection => new()
    {
        Protocol = NetworkProtocol.Ftp,
        Host = "127.0.0.1",
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port,
        Username = "bob",
        Password = "pw",
    };

    /// <summary>Every control connection ever accepted.</summary>
    public int ConnectionCount => Volatile.Read(ref _connections);

    /// <summary>Control connections the client has closed by now.</summary>
    public int ClosedCount => Volatile.Read(ref _closed);

    /// <summary>The commands in the order they came (the password is not kept apart: it is a test's).</summary>
    public IReadOnlyList<string> Commands => _commands.Select(c => c.Line).ToList();

    /// <summary>How many bytes of the file have reached the server's data socket.</summary>
    public long? ReceivedBytes(string path) => _received.TryGetValue(path, out var n) ? n : null;

    /// <summary>The file as a listing would show it: only once the final reply has gone out.</summary>
    public long? StoredBytes(string path) => _stored.TryGetValue(path, out var n) ? n : null;

    /// <summary>A line nobody asked for, on every open control connection: what an idle timeout looks like.</summary>
    public async Task SayOnEveryConnectionAsync(string line)
    {
        foreach (var writer in _writers.Values)
        {
            await writer.WriteLineAsync(line);
        }
    }

    /// <summary>Hangs up every control connection, as a server does on one that has been quiet too long.</summary>
    public void HangUp()
    {
        foreach (var client in _open.Values)
        {
            client.Close();
        }
    }

    /// <summary>Puts a file on the server, as if it had always been there.</summary>
    public void Seed(string path, long size) => _stored[path] = size;

    /// <summary>Whether a command starting with <paramref name="prefix"/> came on control connection number <paramref name="connection"/>.</summary>
    public bool Saw(string prefix, int? connection = null) =>
        _commands.Any(c => c.Line.StartsWith(prefix, StringComparison.Ordinal) && (connection is null || c.Connection == connection));

    /// <summary>Waits for something the server does in its own time; false when it never happened.</summary>
    public static async Task<bool> EventuallyAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var started = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - started > timeoutMs)
            {
                return false;
            }

            await Task.Delay(10);
        }

        return true;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        foreach (var client in _open.Values)
        {
            client.Dispose();
        }

        FinalReplyGate?.TrySetResult();
        DownloadReplyGate?.TrySetResult();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                var number = Interlocked.Increment(ref _connections);
                _ = ServeAsync(client, number);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // The test is over.
        }
    }

    private async Task ServeAsync(TcpClient client, int number)
    {
        _open[number] = client;
        var ct = _stop.Token;
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 1024, leaveOpen: true);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
                if (_open.Count > MaxConnections)
                {
                    await writer.WriteLineAsync("421 Too many connections");
                    return;
                }

                await writer.WriteLineAsync("220 stub ready");
                _writers[number] = writer;
                TcpListener? passive = null;
                string? renaming = null;
                try
                {
                    while (await reader.ReadLineAsync(ct) is { } line)
                    {
                        _commands.Enqueue((number, line));
                        var space = line.IndexOf(' ');
                        var verb = (space < 0 ? line : line[..space]).ToUpperInvariant();
                        var argument = space < 0 ? string.Empty : line[(space + 1)..];
                        switch (verb)
                        {
                            case "USER":
                                await writer.WriteLineAsync("331 Password please");
                                break;
                            case "PASS":
                                await writer.WriteLineAsync(LoginReply);
                                break;
                            case "FEAT":
                                await writer.WriteLineAsync("211-Features:");
                                await writer.WriteLineAsync(" MFMT");
                                await writer.WriteLineAsync(" MLST type*;size*;modify*;");
                                await writer.WriteLineAsync(" EPSV");
                                await writer.WriteLineAsync(" SIZE");
                                await writer.WriteLineAsync(" UTF8");
                                await writer.WriteLineAsync("211 End");
                                break;
                            case "SYST":
                                await writer.WriteLineAsync("215 UNIX Type: L8");
                                break;
                            case "PWD":
                                await writer.WriteLineAsync("257 \"/\" is the current directory");
                                break;
                            case "TYPE":
                            case "OPTS":
                            case "NOOP":
                                await writer.WriteLineAsync("200 OK");
                                break;
                            case "SIZE":
                                await writer.WriteLineAsync(_stored.TryGetValue(argument, out var size)
                                    ? string.Create(CultureInfo.InvariantCulture, $"213 {size}")
                                    : "550 No such file");
                                break;
                            case "MFMT":
                                await writer.WriteLineAsync("213 Modify=stamped");
                                break;
                            case "MLST":
                                await WriteMlstAsync(writer, argument);
                                break;
                            case "EPSV":
                                passive?.Stop();
                                passive = new TcpListener(IPAddress.Loopback, 0);
                                passive.Start();
                                await writer.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                                    $"229 Entering Extended Passive Mode (|||{((IPEndPoint)passive.LocalEndpoint).Port}|)"));
                                break;
                            case "STOR":
                                await StoreAsync(writer, passive!, argument, ct);
                                break;
                            case "RETR":
                                await SendAsync(writer, passive!, argument, ct);
                                break;
                            case "DELE":
                                await writer.WriteLineAsync(_stored.TryRemove(argument, out _) ? "250 Deleted" : "550 No such file");
                                break;
                            case "RNFR":
                                renaming = argument;
                                await writer.WriteLineAsync(_stored.ContainsKey(argument) ? "350 Ready for RNTO" : "550 No such file");
                                break;
                            case "RNTO":
                                if (renaming is not null && _stored.TryRemove(renaming, out var moved))
                                {
                                    _stored[argument] = moved;
                                }

                                await writer.WriteLineAsync("250 Renamed");
                                break;
                            case "QUIT":
                                await writer.WriteLineAsync("221 Bye");
                                return;
                            default:
                                await writer.WriteLineAsync("502 Not implemented");
                                break;
                        }
                    }
                }
                finally
                {
                    passive?.Stop();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // The client went away, or the test is over.
        }
        finally
        {
            _writers.TryRemove(number, out _);
            _open.TryRemove(number, out _);
            Interlocked.Increment(ref _closed);
        }
    }

    private async Task WriteMlstAsync(StreamWriter writer, string path)
    {
        if (path is "/" or "")
        {
            await writer.WriteLineAsync("250-Listing /");
            await writer.WriteLineAsync(" type=dir;modify=20260101000000; /");
            await writer.WriteLineAsync("250 End");
        }
        else if (_stored.TryGetValue(path, out var size))
        {
            await writer.WriteLineAsync($"250-Listing {path}");
            await writer.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $" type=file;size={size};modify=20260101000000; {path}"));
            await writer.WriteLineAsync("250 End");
        }
        else
        {
            await writer.WriteLineAsync("550 No such file");
        }
    }

    private async Task StoreAsync(StreamWriter writer, TcpListener passive, string path, CancellationToken ct)
    {
        await writer.WriteLineAsync("150 Ok to send data");
        using (var data = await passive.AcceptTcpClientAsync(ct))
        {
            var buffer = new byte[64 * 1024];
            long total = 0;
            _received[path] = 0;
            int read;
            var stream = data.GetStream();
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                _received[path] = total;
            }

            ReceivedToEnd.TryAdd(path, true);
        }

        if (NeverRepliesToStor)
        {
            return;
        }

        if (ExtraPreliminaryReply is { } extra)
        {
            await writer.WriteLineAsync(extra);
        }

        if (FinalReplyGate is { } gate)
        {
            await gate.Task.WaitAsync(ct);
        }

        if (FinalReply.StartsWith('2'))
        {
            _stored[path] = _received[path];
        }

        await writer.WriteLineAsync(FinalReply);
    }

    private async Task SendAsync(StreamWriter writer, TcpListener passive, string path, CancellationToken ct)
    {
        if (!_stored.TryGetValue(path, out var size))
        {
            await writer.WriteLineAsync("550 No such file");
            return;
        }

        await writer.WriteLineAsync("150 Sending");
        using (var data = await passive.AcceptTcpClientAsync(ct))
        {
            await data.GetStream().WriteAsync(new byte[DownloadSendsOnly ?? size], ct);
        }

        if (DownloadReplyGate is { } gate)
        {
            await gate.Task.WaitAsync(ct);
        }

        await writer.WriteLineAsync(DownloadFinalReply);
    }

    /// <summary>The files whose data connection the client has closed.</summary>
    public ConcurrentDictionary<string, bool> ReceivedToEnd { get; } = new(StringComparer.Ordinal);
}
