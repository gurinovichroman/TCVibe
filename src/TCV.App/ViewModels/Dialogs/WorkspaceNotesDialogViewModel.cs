using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Localization;

namespace TCV.App.ViewModels.Dialogs;

/// <summary>
/// Per-workspace scratchpad: free-form markdown notes that answer "what was I doing here?" after a
/// week away. The command the project is started with used to sit under them; it is in the
/// properties window now, next to a hint that names the command that runs it.
/// </summary>
public partial class WorkspaceNotesDialogViewModel : ObservableObject
{
    public WorkspaceNotesDialogViewModel(string workspaceName, string notes, string notesPath)
    {
        WorkspaceName = workspaceName;
        NotesPath = notesPath;
        Notes = notes;

        WindowTitle = string.Format(LocalizationManager.Get("Ws_NotesWindowTitle"), workspaceName);

        _initial = Notes;
    }

    private readonly string _initial;

    /// <summary>
    /// True when the notes were edited since the dialog opened. Esc in a text editor is an easy key
    /// to hit by accident, so the view asks before a dismissal throws the edits away.
    /// </summary>
    public bool IsDirty =>
        !string.Equals(Notes ?? string.Empty, _initial ?? string.Empty, StringComparison.Ordinal);

    public string WorkspaceName { get; }
    public string WindowTitle { get; }

    /// <summary>Where the notes land on disk — shown so the file can be opened in a real editor.</summary>
    public string NotesPath { get; }

    [ObservableProperty]
    private string _notes = string.Empty;

    /// <summary>The notes, trimmed; null until confirmed.</summary>
    public string? Result { get; private set; }

    public bool DialogResult { get; private set; }
    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void Confirm()
    {
        Result = Notes?.Trim() ?? string.Empty;
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
