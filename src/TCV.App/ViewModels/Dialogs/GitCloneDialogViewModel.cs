using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Localization;
using TCV.App.Services;
using TCV.Git;

namespace TCV.App.ViewModels.Dialogs;

/// <summary>
/// Clone-from-URL: paste a link, watch it land. The target folder derives from the URL as it is
/// pasted, so the common case is paste-and-Enter — and cancelling actually stops the transfer
/// rather than hiding a window that keeps downloading.
/// </summary>
public partial class GitCloneDialogViewModel : ObservableObject
{
    private readonly GitCloneService _clone;
    private CancellationTokenSource? _cts;

    /// <summary>Set while the target is being derived from the URL, so the derivation does not read
    /// as the user typing a folder.</summary>
    private bool _updatingDerived;
    private bool _targetEdited;

    public GitCloneDialogViewModel(GitCloneService clone, string parentDirectory, string initialUrl = "")
    {
        _clone = clone;
        ParentDirectory = parentDirectory;

        WindowTitle = LocalizationManager.Get("Git_CloneTitle");

        // A URL already on the clipboard is why this dialog was opened often enough to prefill it.
        // An SSH link that cannot be cloned is filled in too: the form then says why at once and
        // what to copy instead, which an empty box after a copy would not (ROADMAP 6.54).
        if (GitCloneTarget.LooksLikeRepositoryUrl(initialUrl)) Url = initialUrl.Trim();
    }

    public string WindowTitle { get; }

    /// <summary>Directory the clone is created in — the panel's current folder.</summary>
    public string ParentDirectory { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanClone))]
    [NotifyPropertyChangedFor(nameof(ValidationMessage))]
    [NotifyPropertyChangedFor(nameof(InfoMessage))]
    private string _url = string.Empty;

    /// <summary>
    /// The URL as the clone will use it. An SSH link always failed here — there is no SSH transport
    /// — after passing every check; now a well-known host's link becomes its https twin and any
    /// other is refused before the Clone button lights up.
    /// </summary>
    public GitCloneUrl ResolvedUrl => GitCloneTarget.Resolve(Url);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanClone))]
    [NotifyPropertyChangedFor(nameof(ValidationMessage))]
    private string _targetPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanClone))]
    [NotifyPropertyChangedFor(nameof(IsIndeterminate))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIndeterminate))]
    private int _percent;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>Red status text: the last attempt failed.</summary>
    [ObservableProperty]
    private bool _isError;

    /// <summary>Optional user name for a private repository. Held in memory for this clone only.</summary>
    [ObservableProperty]
    private string _userName = string.Empty;

    /// <summary>Opens the credentials section; set automatically when the server rejects an anonymous clone.</summary>
    [ObservableProperty]
    private bool _showCredentials;

    /// <summary>
    /// Reads the access token from the window's <c>PasswordBox</c>. A password box cannot be bound
    /// (that is the point of it), so the token never sits in a property, a settings file or a log —
    /// it is fetched at the moment of cloning and dropped with the call.
    /// </summary>
    public Func<string>? SecretProvider { get; set; }

    /// <summary>The server counts objects before it sends any, and during that stretch there is no
    /// total to divide by — a bar pinned at 0% would read as "stuck".</summary>
    public bool IsIndeterminate => IsBusy && Percent < 0;

    partial void OnUrlChanged(string value)
    {
        if (_targetEdited) return;

        var name = GitCloneTarget.RepositoryName(value);
        _updatingDerived = true;
        try
        {
            TargetPath = name.Length == 0 || ParentDirectory.Length == 0
                ? string.Empty
                : Path.Combine(ParentDirectory, name);
        }
        finally
        {
            _updatingDerived = false;
        }
    }

    partial void OnTargetPathChanged(string value)
    {
        if (!_updatingDerived) _targetEdited = true;
        _ = ProbeTargetAsync();
    }

    /// <summary>
    /// Where the clone actually goes: an absolute path as typed, anything else under
    /// <see cref="ParentDirectory"/>. Left relative, the path would resolve against the process's
    /// working directory — the program's own folder — and the clone would land next to the exe.
    /// </summary>
    public static string ResolveTarget(string parentDirectory, string? target)
    {
        var trimmed = target?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return string.Empty;

        try
        {
            // "C:\x" is absolute. "\x" and "C:x" are rooted but still depend on the current drive or
            // directory, so they are refused rather than guessed at.
            if (Path.IsPathFullyQualified(trimmed)) return trimmed;
            if (Path.IsPathRooted(trimmed) || parentDirectory.Length == 0) return string.Empty;
            return Path.GetFullPath(Path.Combine(parentDirectory, trimmed));
        }
        catch (ArgumentException)
        {
            return string.Empty;   // characters no path can hold; the clone button stays off
        }
    }

    /// <summary>Where the clone will be created, as <see cref="ResolveTarget"/> reads the box.</summary>
    public string EffectiveTarget => ResolveTarget(ParentDirectory, TargetPath);

    /// <summary>
    /// Whether the target already holds files, as last probed. The probe touches the disk — on a
    /// share, for seconds — so it runs off the UI thread (INV-06) instead of inside a property the
    /// binding reads on every keystroke; the service checks again before cloning anyway.
    /// </summary>
    private bool _targetHasContent;
    private int _probeVersion;

    private async Task ProbeTargetAsync()
    {
        var version = ++_probeVersion;
        var target = EffectiveTarget;
        var hasContent = await Task.Run(() => DirectoryHasContent(target));
        if (version != _probeVersion) return;   // the box changed while the disk answered

        _targetHasContent = hasContent;
        OnPropertyChanged(nameof(CanClone));
        OnPropertyChanged(nameof(ValidationMessage));
    }

    public bool CanClone =>
        !IsBusy
        && ResolvedUrl.CanClone
        && EffectiveTarget.Length > 0
        && !_targetHasContent;

    public string ValidationMessage
    {
        get
        {
            if (Url.Length == 0) return string.Empty;

            var resolved = ResolvedUrl;
            if (resolved.Status == GitCloneUrlStatus.Invalid)
            {
                return LocalizationManager.Get("Git_CloneBadUrl");
            }

            if (resolved.Status == GitCloneUrlStatus.SshUnsupported)
            {
                return GitResultText.SshUnsupported(resolved.Host);
            }

            if (EffectiveTarget.Length == 0)
                return LocalizationManager.Get("Git_WtNoPath");
            if (_targetHasContent)
                return string.Format(LocalizationManager.Get("Git_WtPathNotEmpty"), EffectiveTarget);
            return string.Empty;
        }
    }

    /// <summary>A non-blocking note: the SSH link will be cloned over https, and from where. The box
    /// keeps what the user typed — rewriting it under the caret would read as the text changing by itself.</summary>
    public string InfoMessage =>
        ResolvedUrl is { Status: GitCloneUrlStatus.RewrittenToHttps } rewritten
            ? LocalizationManager.Format("Git_CloneViaHttps", rewritten.Url)
            : string.Empty;

    private static bool DirectoryHasContent(string path)
    {
        try
        {
            return path.Length > 0
                   && Directory.Exists(path)
                   && Directory.EnumerateFileSystemEntries(path).Any();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Working directory of the clone once it succeeds — the caller opens it in the panel.</summary>
    public string ClonedPath { get; private set; } = string.Empty;

    public bool DialogResult { get; private set; }
    public event EventHandler? CloseRequested;

    [RelayCommand]
    private async Task CloneAsync()
    {
        if (!CanClone) return;

        _cts = new CancellationTokenSource();
        IsBusy = true;
        IsError = false;
        Percent = -1;
        StatusMessage = LocalizationManager.Get("Git_CloneConnecting");

        var secret = SecretProvider?.Invoke() ?? string.Empty;
        var credentials = secret.Length > 0 ? new GitCredentials(UserName.Trim(), secret) : null;

        try
        {
            var progress = new Progress<GitCloneProgress>(p =>
            {
                Percent = p.Percent;
                var stage = StageText(p.Stage);
                StatusMessage = p.Detail.Length > 0 ? $"{stage} {p.Detail}" : stage;
            });

            var result = await _clone.CloneAsync(
                ResolvedUrl.Url, EffectiveTarget, progress, _cts.Token, credentials);
            if (!result.Success)
            {
                StatusMessage = _cts.IsCancellationRequested
                    ? LocalizationManager.Get("Git_CloneCancelled")
                    : GitResultText.Describe(result);
                IsError = !_cts.IsCancellationRequested;

                // An anonymous clone refused: open the credentials section instead of leaving the
                // user to guess where a token would go.
                if (result.Kind == GitFailureKind.Authentication) ShowCredentials = true;
                return;
            }

            ClonedPath = result.Message;
            DialogResult = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>Localised label for the stage names <see cref="GitCloneService"/> reports.</summary>
    private static string StageText(string stage) => stage switch
    {
        "connecting" => LocalizationManager.Get("Git_CloneConnecting"),
        "receiving" => LocalizationManager.Get("Git_CloneReceiving"),
        "checkout" => LocalizationManager.Get("Git_CloneCheckout"),
        "done" => LocalizationManager.Get("Git_CloneDone"),
        _ => stage,
    };

    /// <summary>
    /// Cancels a running clone, or closes the dialog when nothing is running. Cancelling first and
    /// closing second is deliberate: closing the window while the transfer continues in the
    /// background is how a half-written checkout gets left on disk.
    /// </summary>
    [RelayCommand]
    private void Cancel()
    {
        if (IsBusy)
        {
            _cts?.Cancel();
            StatusMessage = LocalizationManager.Get("Git_CloneCancelling");
            return;
        }

        DialogResult = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
