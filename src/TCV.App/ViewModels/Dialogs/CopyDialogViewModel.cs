using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Localization;
using TCV.App.Operations;
using TCV.Core.Interfaces;

namespace TCV.App.ViewModels.Dialogs;

public partial class CopyDialogViewModel : ObservableObject
{
    /// <summary>Upper bound for parallel copy: more threads than this only fights over the disk.</summary>
    public const int MaxThreads = 16;

    /// <param name="offered">The options this transfer honours (out of or into an archive, fewer than all).</param>
    public CopyDialogViewModel(
        IReadOnlyList<IFileEntry> files, string defaultTarget, bool isMove, CopyOptionSet offered = CopyOptionSet.All)
    {
        Files = files;
        TargetPath = defaultTarget;
        IsMove = isMove;
        Offered = offered;
        Title = LocalizationManager.Get(isMove ? "Copy_TitleMove" : "Copy_TitleCopy");
        Description = files.Count == 1
            ? string.Format(LocalizationManager.Get(isMove ? "Copy_PromptMoveOne" : "Copy_PromptCopyOne"), files[0].Name)
            : LocalizationManager.Format(isMove ? "Copy_PromptMoveMany" : "Copy_PromptCopyMany", files.Count);
    }

    public string Title { get; }
    public string Description { get; }
    public IReadOnlyList<IFileEntry> Files { get; }
    public bool IsMove { get; }

    /// <summary>The options the transfer honours; the others are neither shown nor passed on.</summary>
    public CopyOptionSet Offered { get; }

    /// <summary>
    /// Timestamp, "only if newer" and parallel options belong to copying. A move renames or streams
    /// and deletes; showing the boxes for F6 promised things it never did.
    /// </summary>
    public bool ShowCopyOptions => !IsMove && Offered != CopyOptionSet.None;

    public bool ShowTimestamps => Offered.HasFlag(CopyOptionSet.Timestamps);
    public bool ShowOnlyIfNewer => Offered.HasFlag(CopyOptionSet.OnlyIfNewer);
    public bool ShowParallel => Offered.HasFlag(CopyOptionSet.Parallel);

    // ---- Destination ----
    [ObservableProperty]
    private string _targetPath;

    // ---- Conflict mode ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConflictModeAsk))]
    [NotifyPropertyChangedFor(nameof(ConflictModeOverwrite))]
    [NotifyPropertyChangedFor(nameof(ConflictModeSkip))]
    [NotifyPropertyChangedFor(nameof(ConflictModeRename))]
    private ConflictMode _conflictMode = ConflictMode.Ask;

    public bool ConflictModeAsk
    {
        get => ConflictMode == ConflictMode.Ask;
        set { if (value) ConflictMode = ConflictMode.Ask; }
    }
    public bool ConflictModeOverwrite
    {
        get => ConflictMode == ConflictMode.Overwrite;
        set { if (value) ConflictMode = ConflictMode.Overwrite; }
    }
    public bool ConflictModeSkip
    {
        get => ConflictMode == ConflictMode.Skip;
        set { if (value) ConflictMode = ConflictMode.Skip; }
    }
    public bool ConflictModeRename
    {
        get => ConflictMode == ConflictMode.RenameAuto;
        set { if (value) ConflictMode = ConflictMode.RenameAuto; }
    }

    // ---- Options ----
    [ObservableProperty]
    private bool _preserveTimestamps = true;

    [ObservableProperty]
    private bool _onlyIfNewer;

    [ObservableProperty]
    private bool _parallelCopy;

    [ObservableProperty]
    private int _threads = 4;

    partial void OnThreadsChanged(int value)
    {
        var clamped = Math.Clamp(value, 2, MaxThreads);
        if (clamped != value) Threads = clamped;
    }

    // ---- Dialog result ----
    public bool DialogResult { get; private set; }

    public event EventHandler? CloseRequested;

    [RelayCommand(CanExecute = nameof(CanConfirm))]
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

    private bool CanConfirm() => !string.IsNullOrWhiteSpace(TargetPath);

    partial void OnTargetPathChanged(string value) => ConfirmCommand.NotifyCanExecuteChanged();

    /// <summary>What was chosen; an option not offered keeps its default.</summary>
    public CopyOptions BuildOptions() => IsMove
        ? new CopyOptions(ConflictMode: ConflictMode)
        : new CopyOptions(
            ConflictMode: ConflictMode,
            PreserveTimestamps: !ShowTimestamps || PreserveTimestamps,
            OnlyIfNewer: ShowOnlyIfNewer && OnlyIfNewer,
            Threads: ShowParallel && ParallelCopy ? Math.Clamp(Threads, 2, MaxThreads) : 1);
}
