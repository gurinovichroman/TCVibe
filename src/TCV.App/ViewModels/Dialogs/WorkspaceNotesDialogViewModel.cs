using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Localization;
using TCV.App.Services;

namespace TCV.App.ViewModels.Dialogs;

/// <summary>
/// Per-workspace scratchpad: free-form markdown notes plus the command and working directory this
/// project is normally driven with. Both halves answer the same question after a week away — what
/// was I doing here, and what do I type to get going again.
/// </summary>
public partial class WorkspaceNotesDialogViewModel : ObservableObject
{
    public WorkspaceNotesDialogViewModel(
        string workspaceName,
        string notes,
        string terminalCommand,
        string terminalDirectory,
        string notesPath)
    {
        WorkspaceName = workspaceName;
        NotesPath = notesPath;
        Notes = notes;
        TerminalCommand = terminalCommand;
        TerminalDirectory = terminalDirectory;

        WindowTitle = string.Format(LocalizationManager.Get("Ws_NotesWindowTitle"), workspaceName);

        _initial = (Notes, TerminalCommand, TerminalDirectory);
    }

    private readonly (string Notes, string Command, string Directory) _initial;

    /// <summary>
    /// True when anything was edited since the dialog opened. Esc in a text editor is an easy key to
    /// hit by accident, so the view asks before a dismissal throws the edits away.
    /// </summary>
    public bool IsDirty =>
        !string.Equals(Notes ?? string.Empty, _initial.Notes ?? string.Empty, StringComparison.Ordinal)
        || !string.Equals(TerminalCommand ?? string.Empty, _initial.Command ?? string.Empty, StringComparison.Ordinal)
        || !string.Equals(TerminalDirectory ?? string.Empty, _initial.Directory ?? string.Empty, StringComparison.Ordinal);

    public string WorkspaceName { get; }
    public string WindowTitle { get; }

    /// <summary>Where the notes land on disk — shown so the file can be opened in a real editor.</summary>
    public string NotesPath { get; }

    [ObservableProperty]
    private string _notes = string.Empty;

    [ObservableProperty]
    private string _terminalCommand = string.Empty;

    [ObservableProperty]
    private string _terminalDirectory = string.Empty;

    public WorkspaceNotesResult? Result { get; private set; }

    public bool DialogResult { get; private set; }
    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void Confirm()
    {
        Result = new WorkspaceNotesResult(
            Notes?.Trim() ?? string.Empty,
            TerminalCommand?.Trim() ?? string.Empty,
            TerminalDirectory?.Trim() ?? string.Empty);
        DialogResult = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        DialogResult = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
