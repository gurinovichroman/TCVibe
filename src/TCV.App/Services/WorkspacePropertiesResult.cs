namespace TCV.App.Services;

/// <summary>What the workspace properties dialog gives back. Text is trimmed, the colour is
/// normalised to <c>#RRGGBB</c>; empty means "not set".</summary>
public record WorkspacePropertiesResult(
    string Name,
    string Icon,
    string Color,
    string Group,
    bool Pinned);
