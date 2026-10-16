using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Localization;
using TCV.Git;

namespace TCV.App.ViewModels.Dialogs;

/// <summary>
/// Diff of one file, or of everything below a folder, against HEAD (Ctrl+G). The repository is read on a background thread and the
/// result is plain lines — the window never touches libgit2 (INV-06).
/// </summary>
public partial class GitDiffDialogViewModel : ObservableObject
{
    private readonly GitStatusProvider _provider;
    private readonly string _filePath;

    public GitDiffDialogViewModel(GitStatusProvider provider, string filePath)
    {
        _provider = provider;
        _filePath = filePath;
        // A folder (Ctrl+G on a directory or on "..") may arrive with a trailing separator.
        var trimmed = filePath.TrimEnd('\\', '/');
        var name = Path.GetFileName(trimmed);
        Title = string.Format(LocalizationManager.Get("Git_DiffTitle"), name.Length > 0 ? name : trimmed);
    }

    public string Title { get; }

    public ObservableCollection<DiffLine> Lines { get; } = new();

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _branchLabel = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoDiff))]
    private bool _hasDiff;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Drives the empty-state message: shown only after a load, when there is nothing to list.</summary>
    public bool HasNoDiff => !HasDiff;

    /// <summary>The whole diff as text, for the Copy button.</summary>
    public string DiffText => string.Join(Environment.NewLine, Lines.Select(l => l.Text));

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsBusy = true;
        StatusMessage = LocalizationManager.Get("Git_DiffLoading");
        try
        {
            var diff = await _provider.GetDiffVsHeadAsync(_filePath);

            Lines.Clear();
            BranchLabel = string.Empty;

            if (diff.Text is null)
            {
                HasDiff = false;
                StatusMessage = LocalizationManager.Get("Git_DiffNotRepo");
            }
            else if (string.IsNullOrWhiteSpace(diff.Text))
            {
                HasDiff = false;
                StatusMessage = LocalizationManager.Get("Git_DiffClean");
                BranchLabel = diff.Branch;
            }
            else
            {
                foreach (var line in GitDiffLines.Parse(diff.Text)) Lines.Add(line);
                HasDiff = true;
                StatusMessage = string.Empty;
                BranchLabel = diff.Branch;
            }

            OnPropertyChanged(nameof(DiffText));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Copy()
    {
        try
        {
            System.Windows.Clipboard.SetText(DiffText);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The clipboard is momentarily owned by another process; the user can press Copy again.
        }
    }
}
