using System.Text.Json.Nodes;

namespace TCV.Ai.Llm;

/// <summary>
/// Request-shape differences learned from an endpoint's own error replies (see
/// <see cref="LlmClient"/>): the default is what local servers and older OpenAI models accept.
/// </summary>
public readonly record struct LlmQuirks(bool UseCompletionTokens, bool OmitTemperature)
{
    /// <summary>
    /// Adjusts the quirks according to a 400 reply's message, or returns null when the message names
    /// nothing that can be changed — so a retry happens only when it can plausibly help.
    /// </summary>
    public LlmQuirks? AdaptTo(string error)
    {
        if (string.IsNullOrEmpty(error)) return null;

        if (!UseCompletionTokens && error.Contains("max_completion_tokens", StringComparison.OrdinalIgnoreCase))
            return this with { UseCompletionTokens = true };

        if (!OmitTemperature && error.Contains("temperature", StringComparison.OrdinalIgnoreCase))
            return this with { OmitTemperature = true };

        return null;
    }
}

/// <summary>One turn of a chat request.</summary>
public sealed record ChatMessage(string Role, string Content)
{
    public static ChatMessage System(string content) => new("system", content);
    public static ChatMessage User(string content) => new("user", content);
}

/// <summary>
/// The wire format, as pure functions. Split from <see cref="LlmClient"/> on purpose: request shaping
/// and — much more importantly — response parsing are where OpenAI-compatible servers actually differ,
/// and they are the only part of an LLM feature that can be tested without a network
/// (see LlmPayloadTests).
/// </summary>
public static class LlmPayload
{
    public static JsonObject BuildChatRequest(
        LlmOptions options, IReadOnlyList<ChatMessage> messages, LlmQuirks quirks = default)
    {
        var array = new JsonArray();
        foreach (var message in messages)
        {
            array.Add(new JsonObject
            {
                ["role"] = message.Role,
                ["content"] = message.Content,
            });
        }

        var body = new JsonObject
        {
            ["model"] = options.Model,
            ["messages"] = array,
            // Never stream: every feature here fills a field the user then edits, so partial tokens
            // buy nothing and would complicate the parse for no benefit.
            ["stream"] = false,
        };

        // The cap is spelled differently by different servers: every OpenAI-compatible local server
        // takes max_tokens, while OpenAI's newer models refuse it in favour of max_completion_tokens.
        body[quirks.UseCompletionTokens ? "max_completion_tokens" : "max_tokens"] = options.MaxTokens;

        // Reasoning models accept only the default temperature and reject any explicit value.
        if (!quirks.OmitTemperature) body["temperature"] = options.Temperature;

        return body;
    }

    public static JsonObject BuildEmbeddingRequest(LlmOptions options, IReadOnlyList<string> inputs)
    {
        var array = new JsonArray();
        foreach (var input in inputs) array.Add(input);

        return new JsonObject
        {
            ["model"] = options.EmbeddingModel,
            ["input"] = array,
        };
    }

    /// <summary>
    /// Pulls the assistant text out of a completion. Handles the three shapes seen in practice:
    /// <c>choices[0].message.content</c> (OpenAI), a content <em>array</em> of typed parts (some
    /// proxies), and <c>choices[0].text</c> (legacy completions). Returns empty when there is no text —
    /// the caller shows "the model returned nothing" rather than crashing on a shape it did not expect.
    /// </summary>
    public static string ParseChatResponse(JsonNode? response)
    {
        if (response?["choices"] is not JsonArray choices || choices.Count == 0) return string.Empty;

        var first = choices[0];
        if (first?["message"]?["content"] is { } content)
        {
            if (content is JsonArray parts)
            {
                var text = parts
                    .Select(part => part?["text"]?.GetValue<string>())
                    .Where(part => !string.IsNullOrEmpty(part));
                return string.Join(string.Empty, text).Trim();
            }

            return TryString(content).Trim();
        }

        return TryString(first?["text"]).Trim();
    }

    /// <summary>
    /// Reads embedding vectors, honouring the <c>index</c> field rather than array order: the spec
    /// permits a server to return them out of order, and a silently shuffled vector produces a search
    /// index that is wrong in a way no test of the search itself would catch.
    /// </summary>
    public static IReadOnlyList<float[]> ParseEmbeddingResponse(JsonNode? response, int expected)
    {
        var result = new float[expected][];

        if (response?["data"] is not JsonArray data) return Array.Empty<float[]>();

        for (int position = 0; position < data.Count; position++)
        {
            var item = data[position];
            if (item?["embedding"] is not JsonArray vector) continue;

            var index = item["index"]?.GetValue<int>() ?? position;
            if (index < 0 || index >= expected) continue;

            var values = new float[vector.Count];
            for (int i = 0; i < vector.Count; i++)
                values[i] = vector[i]?.GetValue<float>() ?? 0f;

            result[index] = values;
        }

        return result.Where(v => v is not null).ToList()!;
    }

    /// <summary>The <c>error.message</c> a server sends with a 4xx/5xx, or empty.</summary>
    public static string ParseError(JsonNode? response) =>
        TryString(response?["error"]?["message"]) is { Length: > 0 } message
            ? message
            : TryString(response?["error"]);

    private static string TryString(JsonNode? node)
    {
        if (node is null) return string.Empty;
        try
        {
            return node.GetValue<string>();
        }
        catch
        {
            return node.ToString();
        }
    }
}
