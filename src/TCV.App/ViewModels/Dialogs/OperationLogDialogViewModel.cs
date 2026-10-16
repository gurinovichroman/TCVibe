using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Operations;
using TCV.App.Services;

namespace TCV.App.ViewModels.Dialogs;

/// <summary>
/// Backs the file-operation log window: a history of copy/move/delete/new-folder, newest first.
/// Reads from <see cref="OperationLog"/> and refreshes live while open. The one thing it can change
/// is a rename batch it recorded, which it can take back (the AI rename's «undo after», 6.55).
/// </summary>
public sealed partial class OperationLogDialogViewModel : ObservableObject, IDisposable
{
    private readonly OperationLog _log;
    private readonly SavedOperationStore? _savedOperations;
    private readonly Func<RenameBatchRecord, Task>? _undoRename;

    /// <param name="undoRename">Takes a logged rename batch back: checks the folder, asks, and runs
    /// it through the queue (INV-10, INV-11). Null keeps the undo button disabled.</param>
    public OperationLogDialogViewModel(
        OperationLog log,
        SavedOperationStore? savedOperations = null,
        Func<RenameBatchRecord, Task>? undoRename = null)
    {
        _log = log;
        _savedOperations = savedOperations;
        _undoRename = undoRename;
        _log.Changed += OnLogChanged;
        Reload();
    }

    public ObservableCollection<OperationLogRow> Entries { get; } = new();

    [ObservableProperty] private bool _isEmpty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveSelected))]
    [NotifyPropertyChangedFor(nameof(CanUndoSelected))]
    private OperationLogRow? _selected;

    /// <summary>True while an undo is being checked, asked about or run; the button waits for it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUndoSelected))]
    private bool _isBusy;

    /// <summary>True when the selected line is a rename batch that can be taken back.</summary>
    public bool CanUndoSelected => !IsBusy && _undoRename is not null && Selected?.CanUndo == true;

    /// <summary>
    /// Takes the selected rename batch back. Whether every file can have its old name again is decided
    /// against the folder as it is now, by the callback, which also asks first.
    /// </summary>
    [RelayCommand]
    private async Task UndoSelectedAsync()
    {
        if (!CanUndoSelected || Selected?.Entry.Renames is not { } batch)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _undoRename!(batch);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>True when the selected line describes something that can be replayed (6.22).</summary>
    public bool CanSaveSelected => Selected?.CanReplay == true && _savedOperations is not null;

    /// <summary>
    /// Turns the selected log line into a named, replayable operation. Returns the name it was
    /// saved under, or null when the line cannot be replayed or the write failed.
    /// </summary>
    public string? SaveSelectedAsOperation(string name)
    {
        if (Selected is not { Entry: { } entry } || _savedOperations is null) return null;
        if (string.IsNullOrWhiteSpace(name)) return null;

        var operation = SavedOperation.FromLogEntry(
            entry.Kind, entry.Source, entry.Destination, name.Trim());
        if (operation is null) return null;

        return _savedOperations.Save(operation) ? operation.Name : null;
    }

    [RelayCommand]
    private void Refresh() => Reload();

    [RelayCommand]
    private void Clear()
    {
        _log.Clear();
        Reload();
    }

    private void OnLogChanged(object? sender, OperationLogEntry? entry)
    {
        // The queue appends from its worker thread — hop to the UI thread before touching the
        // observable collection bound to the DataGrid. If there's no dispatcher (shutdown, or a
        // headless host), skip the refresh rather than mutate the collection off the UI thread.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (dispatcher.CheckAccess()) Apply(entry);
        else dispatcher.BeginInvoke(() => Apply(entry));
    }

    /// <summary>
    /// Applies a single change: a new entry is inserted as one row (newest first) so a burst of
    /// operations doesn't rebuild the whole grid; a <c>null</c> entry (e.g. Clear) forces a full
    /// reload.
    /// </summary>
    private void Apply(OperationLogEntry? entry)
    {
        if (entry is null)
        {
            Reload();
            return;
        }

        Entries.Insert(0, new OperationLogRow(entry));
        while (Entries.Count > OperationLog.MaxEntries)
            Entries.RemoveAt(Entries.Count - 1);
        IsEmpty = false;
    }

    private void Reload()
    {
        Entries.Clear();
        foreach (var e in _log.LoadAll())
            Entries.Add(new OperationLogRow(e));
        IsEmpty = Entries.Count == 0;
    }

    public void Dispose() => _log.Changed -= OnLogChanged;
}

/// <summary>A single row of the operation log, with values pre-formatted for display.</summary>
public sealed class OperationLogRow
{
    public OperationLogRow(OperationLogEntry entry)
    {
        Entry = entry;
        TimeText = entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
        KindText = KindLabel(entry.Kind);
        Source = entry.Source;
        Destination = DestinationLabel(entry.Destination);
        ItemsText = entry.ItemCount.ToString();
        OutcomeText = OutcomeLabel(entry.Outcome);
        Error = entry.Error ?? string.Empty;
        IsFailed = entry.Outcome == OperationOutcome.Failed;
    }

    /// <summary>The underlying entry — what a replayable block is derived from (6.22).</summary>
    public OperationLogEntry Entry { get; }

    public string TimeText { get; }
    public string KindText { get; }
    public string Source { get; }
    public string Destination { get; }
    public string ItemsText { get; }
    public string OutcomeText { get; }
    public string Error { get; }
    public bool IsFailed { get; }

    /// <summary>
    /// True when this line records the renames of a batch (<see cref="RenameOperation"/>), so they can
    /// be taken back. Whether each file still carries its new name is checked when it is asked for.
    /// </summary>
    public bool CanUndo => Entry.Renames?.IsUsable == true;

    /// <summary>
    /// True when this line can become a saved operation: it succeeded, and its kind is one that
    /// can be replayed (a delete cannot — see <see cref="SavedOperation.FromLogEntry"/>).
    /// </summary>
    public bool CanReplay =>
        Entry.Outcome == OperationOutcome.Completed &&
        Entry.Kind is OperationKind.Copy or OperationKind.Move or OperationKind.CreateFolder;

    /// <summary>
    /// The destination column: the Recycle Bin token (and the Russian word older versions stored in
    /// its place) shows in the current language, anything else as written.
    /// </summary>
    public static string DestinationLabel(string? destination) => destination switch
    {
        null or "" => string.Empty,
        OperationLogDescriptor.RecycleBinToken or "Корзина" => Localization.LocalizationManager.Get("Log_RecycleBin"),
        _ => destination,
    };

    private static string KindLabel(OperationKind kind) => Localization.LocalizationManager.Get(KindKey(kind));

    /// <summary>The dictionary key of a kind's name in the «Operation» column.</summary>
    public static string KindKey(OperationKind kind) => kind switch
    {
        OperationKind.Copy => "Log_KindCopy",
        OperationKind.Move => "Log_KindMove",
        OperationKind.Delete => "Log_KindDelete",
        OperationKind.CreateFolder => "Log_KindCreateFolder",
        OperationKind.CreateFile => "Log_KindCreateFile",
        OperationKind.Rename => "Log_KindRename",
        OperationKind.Extract => "Log_KindExtract",
        OperationKind.Pack => "Log_KindPack",
        OperationKind.Download => "Log_KindDownload",
        OperationKind.Upload => "Log_KindUpload",
        _ => "Log_KindOther",
    };

    private static string OutcomeLabel(OperationOutcome outcome) => Localization.LocalizationManager.Get(outcome switch
    {
        OperationOutcome.Completed => "Log_OutcomeOk",
        OperationOutcome.Failed => "Log_OutcomeFailed",
        _ => "Log_OutcomeCancelled",
    });
}
