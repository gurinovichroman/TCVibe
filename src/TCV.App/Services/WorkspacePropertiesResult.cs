namespace TCV.App.Services;

/// <summary>What the workspace properties dialog gives back. Text is trimmed, the colour is
/// normalised to <c>#RRGGBB</c>; empty means "not set". <see cref="IconImage"/> is a picture in
/// <c>WorkspaceIconStore</c>; <see cref="AutoIconImage"/> is false once the user has chosen one,
/// an emoji or none, so the project is no longer searched for a picture. <see cref="TerminalCommand"/>
/// and <see cref="TerminalDirectory"/> are what the workspace terminal command runs, and where.</summary>
public record WorkspacePropertiesResult(
    string Name,
    string Icon,
    string Color,
    string Group,
    bool Pinned,
    string IconImage = "",
    bool AutoIconImage = true,
    string TerminalCommand = "",
    string TerminalDirectory = "");
