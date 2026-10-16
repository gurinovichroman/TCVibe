using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Localization;
using TCV.Network;

namespace TCV.App.ViewModels.Dialogs;

/// <summary>One host of <c>~/.ssh/config</c> in the import list: what it becomes, and whether it is ticked.</summary>
public sealed partial class SshImportRowViewModel : ObservableObject
{
    public SshImportRowViewModel(SshImportCandidate candidate)
    {
        Candidate = candidate;
        _isSelected = !candidate.IsDuplicate;   // what is saved already is not added twice unless asked
    }

    public SshImportCandidate Candidate { get; }

    /// <summary>The alias: what the user types after <c>ssh</c>, and the connection's name.</summary>
    public string Title => Candidate.Host.Alias;

    /// <summary>Where it connects, as the manager names it: <c>sftp://deploy@web1.example.com:2222/</c>.</summary>
    public string Address => Candidate.Connection.DescribeAddress("/");

    /// <summary>The key it signs in with, or empty for a password.</summary>
    public string KeyFile => Candidate.Connection.AuthMethod == SshAuthMethod.KeyFile
        ? Candidate.Connection.KeyFilePath ?? string.Empty
        : string.Empty;

    /// <summary>Already saved under another name.</summary>
    public bool IsDuplicate => Candidate.IsDuplicate;

    public string DuplicateText => Candidate.DuplicateOf is { } name
        ? string.Format(LocalizationManager.Get("SshImport_Duplicate"), name)
        : string.Empty;

    /// <summary>What the user should know: the gaps filled the way ssh fills them, what is not supported.</summary>
    public string Notes => string.Join(" · ", Candidate.Notes.Select(n => LocalizationManager.Get("SshImport_Note_" + n)));

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>What a screen reader announces for the row.</summary>
    public override string ToString() => Title;
}

/// <summary>
/// Shows what an import from <c>~/.ssh/config</c> would add (6.82) before anything is saved: every
/// concrete host with its address, key and notes, the ones already saved unticked, and what the file
/// said that could not be taken into account.
/// </summary>
public sealed partial class SshConfigImportDialogViewModel : ObservableObject
{
    public SshConfigImportDialogViewModel(
        string configPath, IReadOnlyList<SshImportCandidate> candidates, IReadOnlyList<SshConfigWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(warnings);
        Intro = string.Format(LocalizationManager.Get("SshImport_Intro"), configPath);
        Warnings = string.Join(Environment.NewLine,
            warnings.Select(w => string.Format(LocalizationManager.Get("SshImport_Warn_" + w.Kind), w.Detail)));

        foreach (var candidate in candidates)
        {
            var row = new SshImportRowViewModel(candidate);
            row.PropertyChanged += OnRowChanged;
            Rows.Add(row);
        }
    }

    public ObservableCollection<SshImportRowViewModel> Rows { get; } = [];

    public string Intro { get; }

    /// <summary>What the configuration said that the import could not use, one per line; empty when nothing.</summary>
    public string Warnings { get; }

    public bool HasWarnings => Warnings.Length > 0;

    public string CountText => string.Format(
        LocalizationManager.Get("SshImport_Count"), Rows.Count(r => r.IsSelected), Rows.Count);

    /// <summary>True once the user chose Import.</summary>
    public bool Confirmed { get; private set; }

    /// <summary>The connections to add: the ticked rows', in the file's order.</summary>
    public IReadOnlyList<ConnectionInfo> Chosen => Rows.Where(r => r.IsSelected).Select(r => r.Candidate.Connection).ToList();

    public event EventHandler? CloseRequested;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Import()
    {
        Confirmed = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool HasSelection() => Rows.Any(r => r.IsSelected);

    [RelayCommand]
    private void Cancel()
    {
        Confirmed = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SshImportRowViewModel.IsSelected))
        {
            OnPropertyChanged(nameof(CountText));
            ImportCommand.NotifyCanExecuteChanged();
        }
    }
}
