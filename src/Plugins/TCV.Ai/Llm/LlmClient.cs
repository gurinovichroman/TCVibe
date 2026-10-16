using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace TCV.Ai.Llm;

/// <summary>Outcome of a call: text or vectors on success, a message the dialog can show otherwise.</summary>
public sealed record LlmResult(bool Success, string Text, string Error)
{
    public static LlmResult Ok(string text) => new(true, text, string.Empty);
    public static LlmResult Fail(string error) => new(false, string.Empty, error);
}

/// <summary>
/// The single HTTP path to a language model (ADR-0007). Every LLM feature goes through here, so
/// "is anything configured?" and "did we go to the network?" have exactly one answer each.
/// <para>
/// Without configuration the client refuses locally and never opens a socket — that is the ADR-0008
/// promise, and it is enforced here rather than at each call site, where one forgotten check would
/// quietly break it.
/// </para>
/// </summary>
public sealed class LlmClient : IDisposable
{
    private readonly Func<LlmOptions> _options;
    private readonly ILogger<LlmClient>? _logger;
    private readonly HttpClient _http;

    /// <param name="options">Read per call, so editing the endpoint in settings takes effect without
    /// a restart.</param>
    public LlmClient(Func<LlmOptions> options, ILogger<LlmClient>? logger = null, HttpMessageHandler? handler = null)
    {
        _options = options;
        _logger = logger;
        // HttpClient's own 100 s timeout would cut a slow local model off no matter what the setting
        // says, and report it as the configured value. The time limit is per request instead, in
        // PostAsync, from LlmOptions.TimeoutSeconds.
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    // What each endpoint+model has turned out to accept, learned from 400 replies. Kept for the life of
    // the process so the retry happens once, not on every call.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, LlmQuirks> _quirks = new();

    public LlmOptions Options => _options();

    public bool IsConfigured => Options.IsConfigured;

    /// <summary>Sends a chat completion and returns the assistant's text.</summary>
    public async Task<LlmResult> CompleteAsync(
        IReadOnlyList<ChatMessage> messages, CancellationToken ct = default)
    {
        var options = Options;
        if (!options.IsConfigured)
            return LlmResult.Fail(NotConfigured);

        var quirkKey = options.ChatUrl + "|" + options.Model;
        var quirks = _quirks.GetValueOrDefault(quirkKey);

        PostResult response;
        var attempts = 0;
        while (true)
        {
            var body = LlmPayload.BuildChatRequest(options, messages, quirks);
            response = await PostAsync(options, options.ChatUrl, body, ct);

            // A 400 that names a parameter the model does not take is answered by rebuilding the
            // request without it, at most a couple of times.
            if (!response.Success && response.Status == 400 && ++attempts <= 2
                && quirks.AdaptTo(response.Error) is { } adapted)
            {
                quirks = adapted;
                _quirks[quirkKey] = adapted;
                continue;
            }

            break;
        }

        if (!response.Success) return LlmResult.Fail(response.Error);

        var text = LlmPayload.ParseChatResponse(response.Payload);
        return text.Length == 0
            ? LlmResult.Fail(EmptyAnswer)
            : LlmResult.Ok(text);
    }

    /// <summary>
    /// Embeds a batch of texts. Returns an empty list on failure with the reason in
    /// <paramref name="error"/> — the indexer processes thousands of chunks and must be able to skip a
    /// bad batch without unwinding the whole run.
    /// </summary>
    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> inputs, Action<string>? error = null, CancellationToken ct = default)
    {
        var options = Options;
        if (!options.CanEmbed)
        {
            error?.Invoke(NotConfiguredForEmbeddings);
            return Array.Empty<float[]>();
        }

        if (inputs.Count == 0) return Array.Empty<float[]>();

        var body = LlmPayload.BuildEmbeddingRequest(options, inputs);
        var response = await PostAsync(options, options.EmbeddingsUrl, body, ct);
        if (!response.Success)
        {
            error?.Invoke(response.Error);
            return Array.Empty<float[]>();
        }

        var vectors = LlmPayload.ParseEmbeddingResponse(response.Payload, inputs.Count);
        if (vectors.Count != inputs.Count)
        {
            // All or nothing. The parser drops a missing vector, which shifts every later one by a
            // place; handing that list to a caller that pairs it with its inputs by position would
            // index each chunk under the wrong text.
            error?.Invoke($"The endpoint returned {vectors.Count} vector(s) for {inputs.Count} input(s).");
            return Array.Empty<float[]>();
        }

        return vectors;
    }

    private readonly record struct PostResult(bool Success, JsonNode? Payload, string Error, int Status = 0);

    private async Task<PostResult> PostAsync(
        LlmOptions options, string url, JsonObject body, CancellationToken ct)
    {
        var seconds = LlmOptions.ClampTimeout(options.TimeoutSeconds);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };

            // A local llama.cpp or Ollama needs no key; sending an empty bearer would make some of
            // them reject the call outright, so the header is added only when there is a key.
            if (options.ApiKey.Trim() is { Length: > 0 } key)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(seconds));

            using var response = await _http.SendAsync(request, timeout.Token);
            var text = await response.Content.ReadAsStringAsync(timeout.Token);

            JsonNode? payload = null;
            try { payload = JsonNode.Parse(text); }
            catch (Exception ex) { _logger?.LogDebug(ex, "LLM endpoint returned a non-JSON body"); }

            if (!response.IsSuccessStatusCode)
            {
                var detail = LlmPayload.ParseError(payload);
                return new PostResult(false, null, detail.Length > 0
                    ? $"{(int)response.StatusCode}: {detail}"
                    : $"{(int)response.StatusCode} {response.ReasonPhrase}", (int)response.StatusCode);
            }

            return payload is null
                ? new PostResult(false, null, NotJson)
                : new PostResult(true, payload, string.Empty, (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new PostResult(false, null, TimedOut(seconds));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "LLM request to {Url} failed", url);
            return new PostResult(false, null, ex.Message);
        }
    }

    /// <summary>Message shown when no endpoint is set — it says what to do, not just what failed.</summary>
    public const string NotConfigured =
        "No language model is configured. Set the endpoint and model in Settings → Agent layer "
        + "(any OpenAI-compatible server: Ollama, LM Studio, llama.cpp, or a cloud API).";

    /// <summary>The model answered with nothing usable. Named so the application can translate it.</summary>
    public const string EmptyAnswer = "The model returned an empty answer.";

    public const string NotJson = "The endpoint replied with something that is not JSON.";

    public const string TimedOutPrefix = "The endpoint did not answer within ";

    public static string TimedOut(int seconds) => $"{TimedOutPrefix}{seconds}s.";

    public const string NotConfiguredForEmbeddings =
        "No embedding model is configured. Set it in Settings → Agent layer; it can be the same "
        + "local endpoint as the chat model.";

    public void Dispose() => _http.Dispose();
}
