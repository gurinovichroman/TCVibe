using System.Globalization;
using System.Text.Json;

namespace TCV.Network.Cloud;

/// <summary>Tolerant readers for the providers' JSON: a missing or oddly typed field is null, never an exception.</summary>
internal static class CloudJson
{
    public static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>A number, or a number in a string (Drive sends sizes as strings).</summary>
    public static long? Int64(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    public static JsonElement? Object(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    public static bool Has(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind != JsonValueKind.Null;

    /// <summary>
    /// An ISO 8601 time as local time — the panels show and compare local times. An absent or
    /// unparseable value is <see cref="DateTime.MinValue"/>: one odd timestamp must not fail a listing.
    /// </summary>
    public static DateTime Time(string? text) =>
        !string.IsNullOrEmpty(text)
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.LocalDateTime
            : DateTime.MinValue;

    /// <summary>The start of a non-JSON body, for an error message: one line, at most 200 characters.</summary>
    public static string? Excerpt(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var line = body.Trim().ReplaceLineEndings(" ");

        // An HTML error page says nothing a user can act on.
        if (line.StartsWith('<'))
        {
            return null;
        }

        return line.Length <= 200 ? line : line[..200] + "…";
    }
}
