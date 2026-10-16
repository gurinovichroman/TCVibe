namespace TCV.Ai.Llm;

/// <summary>
/// Everything needed to reach one OpenAI-compatible endpoint (ADR-0007). One abstraction covers
/// Ollama, LM Studio, llama.cpp and the clouds, and — the part that matters for ADR-0008 — an unset
/// <see cref="Endpoint"/> means the application makes no network call at all.
/// </summary>
public sealed record LlmOptions(
    string Endpoint,
    string ApiKey,
    string Model,
    string EmbeddingModel,
    int MaxTokens = 800,
    double Temperature = 0.2,
    int TimeoutSeconds = 60)
{
    public static LlmOptions Empty { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty);

    /// <summary>
    /// The shortest wait for an answer, in seconds. Below it even a fast cloud model times out on a
    /// long prompt, so a smaller number is a typo rather than a choice.
    /// </summary>
    public const int MinTimeoutSeconds = 5;

    /// <summary>
    /// The longest wait for an answer, in seconds. A slow local model on a CPU needs minutes; past ten
    /// the server is hung, and the window that asked would sit on «working…» with nothing to show.
    /// </summary>
    public const int MaxTimeoutSeconds = 600;

    /// <summary>True when <paramref name="seconds"/> is a timeout the client uses as it is.</summary>
    public static bool IsValidTimeout(int seconds) => seconds is >= MinTimeoutSeconds and <= MaxTimeoutSeconds;

    /// <summary>
    /// The timeout the client actually waits: a hand-edited <c>settings.json</c> can hold anything,
    /// and 0 or a day are both worse than the nearest bound.
    /// </summary>
    public static int ClampTimeout(int seconds) => Math.Clamp(seconds, MinTimeoutSeconds, MaxTimeoutSeconds);

    /// <summary>True when a chat call is possible. Checked before every feature that needs one.</summary>
    public bool IsConfigured => Endpoint.Trim().Length > 0 && Model.Trim().Length > 0;

    /// <summary>True when embeddings are possible — a separate model, so a separate check (6.16).</summary>
    public bool CanEmbed => Endpoint.Trim().Length > 0 && EmbeddingModel.Trim().Length > 0;

    /// <summary>
    /// Host of the configured endpoint (empty when unset or not a URL) — what a «this text will be
    /// sent to …» notice names.
    /// </summary>
    public string EndpointHost =>
        Uri.TryCreate(Endpoint.Trim(), UriKind.Absolute, out var uri) ? uri.Host : string.Empty;

    /// <summary>
    /// True when the endpoint is this machine. Text sent to it does not leave the computer, which is
    /// the only case where sending file contents needs no acknowledgement.
    /// </summary>
    public bool IsLocalEndpoint =>
        Uri.TryCreate(Endpoint.Trim(), UriKind.Absolute, out var uri)
        && (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));

    public string ChatUrl => Combine(Endpoint, "chat/completions");
    public string EmbeddingsUrl => Combine(Endpoint, "embeddings");

    /// <summary>
    /// Joins the configured base with an OpenAI route. Written to survive what people actually paste:
    /// with or without a trailing slash, with or without <c>/v1</c>, and — the case worth handling
    /// explicitly — the full route already included, which would otherwise become
    /// <c>/v1/chat/completions/chat/completions</c>.
    /// </summary>
    public static string Combine(string endpoint, string route)
    {
        var baseUrl = (endpoint ?? string.Empty).Trim().TrimEnd('/');
        if (baseUrl.Length == 0) return string.Empty;

        if (baseUrl.EndsWith("/" + route, StringComparison.OrdinalIgnoreCase))
            return baseUrl;

        // A bare host is assumed to speak the /v1 dialect, because every server in this family does.
        if (!baseUrl.Contains("/v1", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            && uri.AbsolutePath.Trim('/').Length == 0)
        {
            baseUrl += "/v1";
        }

        return baseUrl + "/" + route;
    }
}
