using System.Text.RegularExpressions;
using TCV.Ai.Llm;
using TCV.Ai.Rename;
using TCV.App.Localization;

namespace TCV.App.Agents;

/// <summary>
/// Puts the language-model client's failure messages into the user's language. <see cref="LlmClient"/>
/// answers in English on purpose (it is WPF-free and its messages are also logged); the window that
/// shows one — the F3 summary, the commit draft, AI rename, semantic search — translates the ones it
/// knows and passes the rest (an endpoint's own error text, an OS network message) through unchanged.
/// </summary>
public static partial class LlmErrorText
{
    public static string Localize(string? error)
    {
        if (string.IsNullOrEmpty(error)) return string.Empty;

        if (error == LlmClient.NotConfigured) return LocalizationManager.Get("Llm_NotConfigured");
        if (error == LlmClient.NotConfiguredForEmbeddings) return LocalizationManager.Get("Llm_NotConfiguredEmbeddings");
        if (error == LlmClient.EmptyAnswer) return LocalizationManager.Get("Llm_EmptyAnswer");
        if (error == LlmClient.NotJson) return LocalizationManager.Get("Llm_NotJson");

        if (TimedOut().Match(error) is { Success: true } timeout)
            return string.Format(LocalizationManager.Get("Llm_TimedOut"), timeout.Groups[1].Value);

        return error;
    }

    [GeneratedRegex(@"^The endpoint did not answer within (\d+)s\.$")]
    private static partial Regex TimedOut();
}

/// <summary>
/// The Note column of the AI rename preview in the user's language. <see cref="AiRenamePlan"/> builds
/// each note from the fixed English sentences in <see cref="AiRenameNotes"/>; each is replaced here by
/// its localised form, and anything unrecognised is left as it came.
/// </summary>
public static partial class AiRenameNoteText
{
    private static readonly (string English, string Key)[] Sentences =
    [
        (AiRenameNotes.FolderDropped, "AiRename_NoteFolderDropped"),
        (AiRenameNotes.EmptyName, "AiRename_NoteEmptyName"),
        (AiRenameNotes.ReservedPrefixed, "AiRename_NoteReserved"),
        (AiRenameNotes.Shortened, "AiRename_NoteShortened"),
        (AiRenameNotes.CollisionSuffixed, "AiRename_NoteCollision"),
        (AiRenameNotes.NoName, "AiRename_NoteNoName"),
        (AiRenameNotes.Unchanged, "AiRename_NoteUnchanged"),
    ];

    public static string Localize(string? note)
    {
        if (string.IsNullOrEmpty(note)) return string.Empty;

        var text = ExtensionKept().Replace(
            note, match => string.Format(LocalizationManager.Get("AiRename_NoteExtension"), match.Groups[1].Value));

        foreach (var (english, key) in Sentences)
            text = text.Replace(english, LocalizationManager.Get(key), StringComparison.Ordinal);

        return text;
    }

    [GeneratedRegex(@"Extension kept as (\.\S*?)\.(?=\s|$)")]
    private static partial Regex ExtensionKept();
}
