using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Localization;
using TCV.App.Services;
using TCV.Core.Interfaces;
using TCV.Core.PluginSdk;

namespace TCV.App.ViewModels.Dialogs;

public partial class PackDialogViewModel : ObservableObject
{
    private readonly bool _sevenZipAvailable;

    public PackDialogViewModel(
        IReadOnlyList<IFileEntry> selectedFiles,
        string defaultDestDir,
        bool sevenZipAvailable)
    {
        _sevenZipAvailable = sevenZipAvailable;

        Description = selectedFiles.Count == 1
            ? string.Format(LocalizationManager.Get("Pack_DescriptionOne"), selectedFiles[0].Name)
            : LocalizationManager.Format("Pack_DescriptionMany", selectedFiles.Count);

        _archiveName = selectedFiles.Count > 0
            ? Path.GetFileNameWithoutExtension(selectedFiles[0].Name)
            : "archive";
        _destinationDir = defaultDestDir;

        // Build available formats: ZIP always, 7z if available, TAR always
        var formats = new List<ArchiveFormatItem>
        {
            new("ZIP (.zip)", ArchiveFormat.Zip),
        };
        if (sevenZipAvailable)
            formats.Add(new("7-Zip (.7z)", ArchiveFormat.SevenZip));
        formats.Add(new("TAR (.tar)", ArchiveFormat.Tar));
        AvailableFormats = formats;

        _selectedFormat = formats[0];
    }

    public string Description { get; }
    public IReadOnlyList<ArchiveFormatItem> AvailableFormats { get; }

    // ---- Archive name ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewPath))]
    [NotifyPropertyChangedFor(nameof(CanAddToExisting))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private string _archiveName;

    // ---- Destination ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewPath))]
    [NotifyPropertyChangedFor(nameof(CanAddToExisting))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private string _destinationDir;

    public string PreviewPath =>
        Path.Combine(DestinationDir, ArchiveName + SelectedFormat.Extension);

    // ---- Format ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewPath))]
    [NotifyPropertyChangedFor(nameof(IsPasswordSupported))]
    [NotifyPropertyChangedFor(nameof(IsCompressionEnabled))]
    [NotifyPropertyChangedFor(nameof(SevenZipInfoVisible))]
    [NotifyPropertyChangedFor(nameof(PasswordUnsupported))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private ArchiveFormatItem _selectedFormat;

    public bool IsFormatZip      => SelectedFormat.Format == ArchiveFormat.Zip;
    public bool IsFormatSevenZip => SelectedFormat.Format == ArchiveFormat.SevenZip;
    public bool IsFormatTar      => SelectedFormat.Format == ArchiveFormat.Tar;

    // These setters are used by RadioButton IsChecked two-way bindings
    public bool FormatZip
    {
        get => IsFormatZip;
        set { if (value) SelectedFormat = AvailableFormats.First(f => f.Format == ArchiveFormat.Zip); }
    }
    public bool FormatSevenZip
    {
        get => IsFormatSevenZip;
        set { if (value) SelectedFormat = AvailableFormats.First(f => f.Format == ArchiveFormat.SevenZip); }
    }
    public bool FormatTar
    {
        get => IsFormatTar;
        set { if (value) SelectedFormat = AvailableFormats.First(f => f.Format == ArchiveFormat.Tar); }
    }

    partial void OnSelectedFormatChanged(ArchiveFormatItem value)
    {
        OnPropertyChanged(nameof(FormatZip));
        OnPropertyChanged(nameof(FormatSevenZip));
        OnPropertyChanged(nameof(FormatTar));
        OnPropertyChanged(nameof(CanAddToExisting));
    }

    // ---- Compression ----
    public bool IsCompressionEnabled => SelectedFormat.Format != ArchiveFormat.Tar;

    [ObservableProperty]
    private ArchiveCompressionLevel _compressionLevel = ArchiveCompressionLevel.Normal;

    public bool CompressionStore  { get => CompressionLevel == ArchiveCompressionLevel.Store;  set { if (value) CompressionLevel = ArchiveCompressionLevel.Store; } }
    public bool CompressionFast   { get => CompressionLevel == ArchiveCompressionLevel.Fast;   set { if (value) CompressionLevel = ArchiveCompressionLevel.Fast; } }
    public bool CompressionNormal { get => CompressionLevel == ArchiveCompressionLevel.Normal; set { if (value) CompressionLevel = ArchiveCompressionLevel.Normal; } }
    public bool CompressionUltra  { get => CompressionLevel == ArchiveCompressionLevel.Ultra;  set { if (value) CompressionLevel = ArchiveCompressionLevel.Ultra; } }

    partial void OnCompressionLevelChanged(ArchiveCompressionLevel value)
    {
        OnPropertyChanged(nameof(CompressionStore));
        OnPropertyChanged(nameof(CompressionFast));
        OnPropertyChanged(nameof(CompressionNormal));
        OnPropertyChanged(nameof(CompressionUltra));
    }

    // ---- Password ----
    public bool IsPasswordSupported =>
        SelectedFormat.Format is ArchiveFormat.Zip
                               or ArchiveFormat.SevenZip;

    // Set from code-behind PasswordChanged events (WPF PasswordBox limitation)
    private string _password = string.Empty;
    private string _passwordConfirm = string.Empty;

    public string Password
    {
        get => _password;
        set
        {
            if (SetProperty(ref _password, value))
            {
                OnPropertyChanged(nameof(PasswordUnsupported));
                ConfirmCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string PasswordConfirm
    {
        get => _passwordConfirm;
        set
        {
            if (SetProperty(ref _passwordConfirm, value))
                ConfirmCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>True when user entered a password but ZIP is selected without 7-Zip available (BCL ZIP has no encryption).</summary>
    public bool PasswordUnsupported =>
        SelectedFormat.Format == ArchiveFormat.Zip
        && !_sevenZipAvailable
        && _password.Length > 0;

    public bool SevenZipAvailable => _sevenZipAvailable;

    /// <summary>Informational banner when 7z format is selected.</summary>
    public bool SevenZipInfoVisible =>
        SelectedFormat.Format == ArchiveFormat.SevenZip && _sevenZipAvailable;

    // ---- Add to existing ----
    [ObservableProperty]
    private bool _addToExisting;

    public bool CanAddToExisting =>
        SelectedFormat.Format == ArchiveFormat.Zip &&
        File.Exists(PreviewPath);

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

    private bool CanConfirm() =>
        !string.IsNullOrWhiteSpace(ArchiveName) &&
        ArchiveName.IndexOfAny(Path.GetInvalidFileNameChars()) == -1 &&
        !string.IsNullOrWhiteSpace(DestinationDir) &&
        _password == _passwordConfirm &&
        !PasswordUnsupported;

    public PackDialogResult BuildResult() => new(
        ArchivePath: Path.Combine(DestinationDir, ArchiveName + SelectedFormat.Extension),
        Format: SelectedFormat.Format,
        CompressionLevel: CompressionLevel,
        // A password typed before switching to TAR must not ride along and be silently ignored.
        Password: IsPasswordSupported && !string.IsNullOrEmpty(_password) ? _password : null,
        AddToExisting: AddToExisting && CanAddToExisting);
}

public record ArchiveFormatItem(string Label, ArchiveFormat Format)
{
    public string Extension => Format switch
    {
        ArchiveFormat.Zip      => ".zip",
        ArchiveFormat.SevenZip => ".7z",
        ArchiveFormat.Tar      => ".tar",
        _                      => ".zip",
    };

    public override string ToString() => Label;
}
