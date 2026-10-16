using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using TCV.Ai.Llm;

namespace TCV.Ai.Tests;

/// <summary>
/// The one HTTP path to a model, against a scripted handler instead of a network: the request shape a
/// newer OpenAI model insists on, a slow server that must not be cut off early, and an embeddings reply
/// with a hole in it.
/// </summary>
public class LlmClientTests
{
    private static LlmOptions Options(int timeoutSeconds = 60) =>
        new("http://localhost:11434/v1", "key", "model", "embed", TimeoutSeconds: timeoutSeconds);

    private const string Completion = """{"choices":[{"message":{"content":"hello"}}]}""";

    private sealed class ScriptedHandler(Func<JsonObject, int, (HttpStatusCode Status, string Body)> reply)
        : HttpMessageHandler
    {
        public List<JsonObject> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = (JsonObject)JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            Bodies.Add(body);

            var (status, text) = reply(body, Bodies.Count);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(text, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static readonly IReadOnlyList<ChatMessage> Messages = [ChatMessage.User("hi")];

    // ── Request shape ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CompleteAsync_DefaultRequest_UsesMaxTokensAndTemperature()
    {
        var handler = new ScriptedHandler((_, _) => (HttpStatusCode.OK, Completion));
        using var client = new LlmClient(() => Options(), handler: handler);

        var result = await client.CompleteAsync(Messages);

        result.Success.Should().BeTrue();
        handler.Bodies.Single().ContainsKey("max_tokens").Should().BeTrue();
        handler.Bodies.Single().ContainsKey("temperature").Should().BeTrue();
    }

    [Fact]
    public async Task CompleteAsync_ServerRejectsMaxTokens_RetriesWithMaxCompletionTokens()
    {
        var handler = new ScriptedHandler((body, call) => call == 1
            ? (HttpStatusCode.BadRequest,
               """{"error":{"message":"Unsupported parameter: 'max_tokens' is not supported with this model. Use 'max_completion_tokens' instead."}}""")
            : (HttpStatusCode.OK, Completion));
        using var client = new LlmClient(() => Options(), handler: handler);

        var result = await client.CompleteAsync(Messages);

        result.Success.Should().BeTrue();
        handler.Bodies.Should().HaveCount(2);
        handler.Bodies[1].ContainsKey("max_completion_tokens").Should().BeTrue();
        handler.Bodies[1].ContainsKey("max_tokens").Should().BeFalse();
    }

    [Fact]
    public async Task CompleteAsync_ServerRejectsTemperature_RetriesWithoutIt()
    {
        var handler = new ScriptedHandler((body, call) => call == 1
            ? (HttpStatusCode.BadRequest,
               """{"error":{"message":"Unsupported value: 'temperature' does not support 0.2 with this model."}}""")
            : (HttpStatusCode.OK, Completion));
        using var client = new LlmClient(() => Options(), handler: handler);

        var result = await client.CompleteAsync(Messages);

        result.Success.Should().BeTrue();
        handler.Bodies[1].ContainsKey("temperature").Should().BeFalse();
    }

    [Fact]
    public async Task CompleteAsync_RemembersWhatTheModelAccepts_SoTheRetryHappensOnce()
    {
        var handler = new ScriptedHandler((body, _) => body.ContainsKey("max_completion_tokens")
            ? (HttpStatusCode.OK, Completion)
            : (HttpStatusCode.BadRequest, """{"error":{"message":"use max_completion_tokens"}}"""));
        using var client = new LlmClient(() => Options(), handler: handler);

        await client.CompleteAsync(Messages);
        var before = handler.Bodies.Count;
        await client.CompleteAsync(Messages);

        handler.Bodies.Count.Should().Be(before + 1);
    }

    [Fact]
    public async Task CompleteAsync_ABadRequestThatNamesNothingFixable_IsNotRetried()
    {
        var handler = new ScriptedHandler((_, _) =>
            (HttpStatusCode.BadRequest, """{"error":{"message":"model not found"}}"""));
        using var client = new LlmClient(() => Options(), handler: handler);

        var result = await client.CompleteAsync(Messages);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("model not found");
        handler.Bodies.Should().ContainSingle();
    }

    // ── Timeout ────────────────────────────────────────────────────────────────

    private sealed class NeverAnswers : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    [Fact]
    public async Task CompleteAsync_ServerThatNeverAnswers_FailsAfterTheConfiguredTimeout()
    {
        // The floor is 5 s; the point is that the message names the limit that was applied and that the
        // client's own 100 s default is not what ends the wait.
        using var client = new LlmClient(() => Options(timeoutSeconds: 1), handler: new NeverAnswers());
        var started = DateTime.UtcNow;

        var result = await client.CompleteAsync(Messages);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("5s");
        (DateTime.UtcNow - started).Should().BeGreaterThan(TimeSpan.FromSeconds(4)).And.BeLessThan(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task CompleteAsync_CallerCancels_ThrowsRatherThanReportingATimeout()
    {
        using var client = new LlmClient(() => Options(), handler: new NeverAnswers());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = async () => await client.CompleteAsync(Messages, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── Embeddings ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task EmbedAsync_ReplyWithAMissingVector_ReturnsNothingRatherThanAShiftedList()
    {
        // Three inputs, but the server sent vectors for indexes 0 and 2 only. Dropping the hole would
        // pair the third text with the second vector.
        var handler = new ScriptedHandler((_, _) => (HttpStatusCode.OK, """
            {"data":[{"index":0,"embedding":[1.0]},{"index":2,"embedding":[3.0]}]}
            """));
        using var client = new LlmClient(() => Options(), handler: handler);
        string? error = null;

        var vectors = await client.EmbedAsync(["a", "b", "c"], e => error = e);

        vectors.Should().BeEmpty();
        error.Should().Contain("2 vector(s) for 3");
    }

    [Fact]
    public async Task EmbedAsync_CompleteReply_ReturnsEveryVectorInInputOrder()
    {
        var handler = new ScriptedHandler((_, _) => (HttpStatusCode.OK, """
            {"data":[{"index":1,"embedding":[2.0]},{"index":0,"embedding":[1.0]}]}
            """));
        using var client = new LlmClient(() => Options(), handler: handler);

        var vectors = await client.EmbedAsync(["a", "b"]);

        vectors.Select(v => v[0]).Should().Equal(1f, 2f);
    }

    [Fact]
    public async Task CompleteAsync_NoEndpoint_NeverOpensASocket()
    {
        var handler = new ScriptedHandler((_, _) => throw new InvalidOperationException("must not be called"));
        using var client = new LlmClient(() => LlmOptions.Empty, handler: handler);

        var result = await client.CompleteAsync(Messages);

        result.Success.Should().BeFalse();
        handler.Bodies.Should().BeEmpty();
    }

    // ── Where the text goes ────────────────────────────────────────────────────

    [Theory]
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("http://127.0.0.1:1234", true)]
    [InlineData("http://[::1]:8080/v1", true)]
    [InlineData("https://api.openai.com/v1", false)]
    [InlineData("http://192.168.1.20:11434", false)]
    [InlineData("", true)]
    public void IsLocalEndpoint_IsTrueOnlyForThisMachine(string endpoint, bool expectedLocal)
    {
        var options = new LlmOptions(endpoint, "", "m", "e");

        // An unset endpoint sends nothing anywhere, which is not «remote» — it is reported as not local
        // only because there is no host; the caller checks IsConfigured first.
        if (endpoint.Length == 0) options.IsLocalEndpoint.Should().BeFalse();
        else options.IsLocalEndpoint.Should().Be(expectedLocal);
    }
}
