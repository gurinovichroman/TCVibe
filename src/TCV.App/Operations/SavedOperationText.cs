using TCV.App.Localization;

namespace TCV.App.Operations;

/// <summary>
/// How a saved operation reads in a list (the Ctrl+K Ctrl+O picker, the manager): the kind in the
/// user's language, which files, and where to. The kind's text is looked up by the enum member's
/// name, <c>SavedOp_Kind_&lt;Name&gt;</c>, so every member needs that key in both dictionaries
/// (TRAP-11) — the raw member name used to be shown instead.
/// </summary>
public static class SavedOperationText
{
    /// <summary>Prefix of the per-kind dictionary keys.</summary>
    public const string KindKeyPrefix = "SavedOp_Kind_";

    /// <summary>The dictionary key naming <paramref name="kind"/>.</summary>
    public static string KindKey(SavedOperationKind kind) => KindKeyPrefix + kind;

    /// <summary>The kind in the current language.</summary>
    public static string KindLabel(SavedOperationKind kind) => LocalizationManager.Get(KindKey(kind));

    /// <summary>"Copy *.log → D:\archive", in the current language.</summary>
    public static string Describe(SavedOperation operation) => Describe(operation, LocalizationManager.Get);

    /// <summary>
    /// "Copy *.log → D:\archive": the kind, the mask (or "the selection" when it is empty) and the
    /// target (or "the other panel" when it is empty). A delete has no target and a new folder takes
    /// no files, so each shows only the half it uses.
    /// </summary>
    /// <param name="text">Looks a dictionary key up; injected so the wording can be tested headless.</param>
    public static string Describe(SavedOperation operation, Func<string, string> text)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(text);

        var kind = text(KindKey(operation.Kind));
        var mask = string.IsNullOrWhiteSpace(operation.SourceMask)
            ? text("SavedOp_Selection")
            : operation.SourceMask.Trim();
        var target = string.IsNullOrWhiteSpace(operation.Target)
            ? text("SavedOp_OtherPanel")
            : operation.Target.Trim();

        return operation.Kind switch
        {
            SavedOperationKind.Delete => $"{kind} {mask}",
            SavedOperationKind.CreateDirectory => $"{kind} → {target}",
            _ => $"{kind} {mask} → {target}",
        };
    }
}
