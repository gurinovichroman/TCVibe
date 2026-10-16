using System.Text.Json.Nodes;
using FluentAssertions;
using TCV.Ai.Llm;

namespace TCV.Ai.Tests;

/// <summary>
/// The wire format. Response parsing is where OpenAI-compatible servers actually differ, so the
/// shapes seen in practice are all pinned here — this is the part of an LLM feature that can be
/// tested without a network.
/// </summary>
public class LlmPayloadTests
{
    private static readonly LlmOptions Options =
        new("http://localhost:11434/v1", string.Empty, "qwen2.5-coder", "nomic-embed-text");

    [Fact]
    public void BuildChatRequest_CarriesModelMessagesAndNeverStreams()
    {
        var body = LlmPayload.BuildChatRequest(Options,
            [ChatMessage.System("be brief"), ChatMessage.User("hello")]);

        body["model"]!.GetValue<string>().Should().Be("qwen2.5-coder");
        body["stream"]!.GetValue<bool>().Should().BeFalse();

        var messages = body["messages"]!.AsArray();
        messages.Should().HaveCount(2);
        messages[0]!["role"]!.GetValue<string>().Should().Be("system");
        messages[1]!["content"]!.GetValue<string>().Should().Be("hello");
    }

    [Fact]
    public void ParseChatResponse_StandardOpenAiShape()
    {
        var response = JsonNode.Parse("""
            { "choices": [ { "message": { "role": "assistant", "content": "  fix the parser  " } } ] }
            """);

        LlmPayload.ParseChatResponse(response).Should().Be("fix the parser");
    }

    [Fact]
    public void ParseChatResponse_ContentAsTypedParts()
    {
        // Some proxies return an array of parts rather than a string.
        var response = JsonNode.Parse("""
            { "choices": [ { "message": { "content": [
                { "type": "text", "text": "fix " },
                { "type": "text", "text": "the parser" } ] } } ] }
            """);

        LlmPayload.ParseChatResponse(response).Should().Be("fix the parser");
    }

    [Fact]
    public void ParseChatResponse_LegacyCompletionShape()
    {
        var response = JsonNode.Parse("""{ "choices": [ { "text": "fix the parser" } ] }""");

        LlmPayload.ParseChatResponse(response).Should().Be("fix the parser");
    }

    [Fact]
    public void ParseChatResponse_NoChoices_IsEmptyRatherThanAThrow()
    {
        LlmPayload.ParseChatResponse(JsonNode.Parse("{}")).Should().BeEmpty();
        LlmPayload.ParseChatResponse(JsonNode.Parse("""{ "choices": [] }""")).Should().BeEmpty();
        LlmPayload.ParseChatResponse(null).Should().BeEmpty();
    }

    [Fact]
    public void BuildEmbeddingRequest_SendsTheBatchAsAnArray()
    {
        var body = LlmPayload.BuildEmbeddingRequest(Options, ["a", "b"]);

        body["model"]!.GetValue<string>().Should().Be("nomic-embed-text");
        body["input"]!.AsArray().Should().HaveCount(2);
    }

    [Fact]
    public void ParseEmbeddingResponse_ReadsVectorsInOrder()
    {
        var response = JsonNode.Parse("""
            { "data": [
                { "index": 0, "embedding": [1.0, 0.0] },
                { "index": 1, "embedding": [0.0, 1.0] } ] }
            """);

        var vectors = LlmPayload.ParseEmbeddingResponse(response, 2);

        vectors.Should().HaveCount(2);
        vectors[0].Should().Equal(1f, 0f);
        vectors[1].Should().Equal(0f, 1f);
    }

    [Fact]
    public void ParseEmbeddingResponse_HonoursTheIndexFieldOverArrayOrder()
    {
        // The spec permits out-of-order data; a silently shuffled vector produces an index that is
        // wrong in a way no test of the search itself would catch.
        var response = JsonNode.Parse("""
            { "data": [
                { "index": 1, "embedding": [0.0, 1.0] },
                { "index": 0, "embedding": [1.0, 0.0] } ] }
            """);

        var vectors = LlmPayload.ParseEmbeddingResponse(response, 2);

        vectors[0].Should().Equal(1f, 0f);
        vectors[1].Should().Equal(0f, 1f);
    }

    [Fact]
    public void ParseEmbeddingResponse_IgnoresAnOutOfRangeIndex()
    {
        var response = JsonNode.Parse("""
            { "data": [ { "index": 7, "embedding": [1.0] }, { "index": 0, "embedding": [2.0] } ] }
            """);

        LlmPayload.ParseEmbeddingResponse(response, 1).Should().HaveCount(1);
    }

    [Fact]
    public void ParseEmbeddingResponse_NoData_IsEmpty() =>
        LlmPayload.ParseEmbeddingResponse(JsonNode.Parse("{}"), 2).Should().BeEmpty();

    [Fact]
    public void ParseError_ReadsTheNestedMessage()
    {
        var response = JsonNode.Parse("""{ "error": { "message": "model not found" } }""");

        LlmPayload.ParseError(response).Should().Be("model not found");
    }

    [Fact]
    public void ParseError_NoError_IsEmpty() =>
        LlmPayload.ParseError(JsonNode.Parse("""{ "choices": [] }""")).Should().BeEmpty();

    // ── Endpoint composition ──────────────────────────────────────────────────

    [Theory]
    [InlineData("http://localhost:11434/v1", "http://localhost:11434/v1/chat/completions")]
    [InlineData("http://localhost:11434/v1/", "http://localhost:11434/v1/chat/completions")]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1/chat/completions")]
    // A bare host is assumed to speak the /v1 dialect, because every server in this family does.
    [InlineData("http://localhost:11434", "http://localhost:11434/v1/chat/completions")]
    // Already the full route: must not become .../chat/completions/chat/completions.
    [InlineData("http://localhost:11434/v1/chat/completions", "http://localhost:11434/v1/chat/completions")]
    public void ChatUrl_SurvivesWhatPeopleActuallyPaste(string endpoint, string expected) =>
        (Options with { Endpoint = endpoint }).ChatUrl.Should().Be(expected);

    [Fact]
    public void EmbeddingsUrl_UsesTheEmbeddingsRoute() =>
        Options.EmbeddingsUrl.Should().Be("http://localhost:11434/v1/embeddings");

    [Fact]
    public void Urls_AreEmptyWhenNothingIsConfigured()
    {
        LlmOptions.Empty.ChatUrl.Should().BeEmpty();
        LlmOptions.Empty.IsConfigured.Should().BeFalse();
        LlmOptions.Empty.CanEmbed.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_NeedsBothAnEndpointAndAModel()
    {
        (Options with { Model = "" }).IsConfigured.Should().BeFalse();
        (Options with { Endpoint = "  " }).IsConfigured.Should().BeFalse();
        Options.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void CanEmbed_IsSeparateFromChat()
    {
        (Options with { EmbeddingModel = "" }).CanEmbed.Should().BeFalse();
        (Options with { Model = "" }).CanEmbed.Should().BeTrue();
    }
}
