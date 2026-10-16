namespace TCV.App.Services;

/// <summary>What the workspace notes dialog gives back: the prose plus the terminal setup saved
/// alongside it. All three are already trimmed; empty means "not set".</summary>
public record WorkspaceNotesResult(
    string Notes,
    string TerminalCommand,
    string TerminalDirectory);
