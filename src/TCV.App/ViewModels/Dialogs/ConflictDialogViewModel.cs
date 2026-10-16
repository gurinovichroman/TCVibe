using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Localization;
using TCV.App.Operations.Conflict;
using TCV.Core.Interfaces;

namespace TCV.App.ViewModels.Dialogs;

public partial class ConflictDialogViewModel : ObservableObject
{
    public ConflictDialogViewModel(IFileEntry source, IFileEntry existing)
    {
        // A name that is the same on both sides tells nothing; the folder is what differs.
        SourcePath = Operations.OperationPaths.ToDisplay(source.Path.Value);
        DestPath = Operations.OperationPaths.ToDisplay(existing.Path.Value);
        SourceSize = FormatSize(source.Size);
        SourceDate = source.LastModified.ToString("yyyy-MM-dd HH:mm:ss");

        DestSize = FormatSize(existing.Size);
        DestDate = existing.LastModified.ToString("yyyy-MM-dd HH:mm:ss");

        var isFolder = source.IsDirectory || existing.IsDirectory;
        Headline = string.Format(
            LocalizationManager.Get(isFolder ? "Conflict_HeadlineFolder" : "Conflict_HeadlineFile"), source.Name);

        Hint = isFolder
            ? string.Empty
            : source.LastModified > existing.LastModified ? LocalizationManager.Get("Conflict_SourceNewer")
            : source.LastModified < existing.LastModified ? LocalizationManager.Get("Conflict_SourceOlder")
            : LocalizationManager.Get("Conflict_SameTime");
        ShowSizeAndDate = !isFolder;
    }

    public string Headline { get; }

    /// <summary>Which side is newer, so the choice does not need date arithmetic.</summary>
    public string Hint { get; }

    public bool ShowSizeAndDate { get; }

    public string SourcePath { get; }
    public string SourceSize { get; }
    public string SourceDate { get; }

    public string DestPath { get; }
    public string DestSize { get; }
    public string DestDate { get; }

    [ObservableProperty]
    private bool _applyToAll;

    // Cancel, not the enum's zero value (Overwrite): closing the window with X or Alt+F4 never
    // reaches a command, and that must not overwrite the file.
    public ConflictResolution Resolution { get; private set; } = ConflictResolution.Cancel;

    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void Overwrite() => Choose(ConflictResolution.Overwrite);

    [RelayCommand]
    private void OverwriteIfNewer() => Choose(ConflictResolution.OverwriteIfNewer);

    [RelayCommand]
    private void Skip() => Choose(ConflictResolution.Skip);

    [RelayCommand]
    private void RenameAuto() => Choose(ConflictResolution.RenameAuto);

    [RelayCommand]
    private void Cancel() => Choose(ConflictResolution.Cancel);

    private void Choose(ConflictResolution resolution)
    {
        Resolution = resolution;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private static string FormatSize(long bytes) => TCV.App.Converters.FileSizeConverter.Format(bytes);
}
