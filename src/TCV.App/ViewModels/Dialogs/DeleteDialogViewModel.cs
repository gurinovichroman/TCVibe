using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Localization;
using TCV.Core.Interfaces;

namespace TCV.App.ViewModels.Dialogs;

public partial class DeleteDialogViewModel : ObservableObject
{
    /// <summary>How many names the dialog lists before it says "and N more".</summary>
    public const int PreviewLimit = 10;

    /// <param name="files">What is about to be deleted.</param>
    /// <param name="recycleCount">
    /// How many of them will really go to the Recycle Bin. The rest are deleted for good, and the
    /// dialog says so: promising the bin for a USB stick or a network share is how files get lost (INV-11).
    /// </param>
    public DeleteDialogViewModel(IReadOnlyList<IFileEntry> files, int recycleCount)
    {
        Files = files;
        RecycleCount = Math.Clamp(recycleCount, 0, files.Count);

        Description = files.Count == 1
            ? string.Format(LocalizationManager.Get("Delete_PromptOne"), files[0].Name)
            : string.Format(LocalizationManager.Get("Delete_PromptMany"), files.Count);

        RecycleBinNote = IsAllRecycled
            ? LocalizationManager.Get("Delete_NoteRecycle")
            : IsPermanent
                ? LocalizationManager.Get("Delete_NotePermanent")
                : string.Format(LocalizationManager.Get("Delete_NoteMixed"), RecycleCount, files.Count);

        FolderNote = files.Any(f => f.IsDirectory) ? LocalizationManager.Get("Delete_NoteFolders") : string.Empty;

        var names = files.Take(PreviewLimit).Select(f => f.IsDirectory ? f.Name + "\\" : f.Name).ToList();
        if (files.Count > PreviewLimit)
            names.Add(string.Format(LocalizationManager.Get("Delete_More"), files.Count - PreviewLimit));
        Preview = names;
    }

    public IReadOnlyList<IFileEntry> Files { get; }
    public int RecycleCount { get; }
    public string Description { get; }
    public string RecycleBinNote { get; }
    public string FolderNote { get; }
    public IReadOnlyList<string> Preview { get; }

    public bool IsAllRecycled => RecycleCount == Files.Count && Files.Count > 0;

    /// <summary>Nothing goes to the bin: the deletion cannot be undone.</summary>
    public bool IsPermanent => RecycleCount == 0;

    /// <summary>Some, but not all, items bypass the bin.</summary>
    public bool IsMixed => !IsAllRecycled && !IsPermanent;

    /// <summary>Shown in the danger colour whenever anything is deleted for good.</summary>
    public bool WarnsPermanent => !IsAllRecycled;

    /// <summary>Enter confirms a recoverable delete; for an irreversible one it must not.</summary>
    public bool DeleteIsDefault => IsAllRecycled;

    public bool CancelIsDefault => !IsAllRecycled;

    public bool HasFolderNote => FolderNote.Length > 0;

    public bool DialogResult { get; private set; }
    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void Confirm()
    {
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
