using System.ComponentModel;
using System.Runtime.CompilerServices;
using TCV.Core.Domain;

namespace TCV.Network;

/// <summary>Status of a queued transfer item.</summary>
public enum TransferStatus
{
    Pending,
    Transferring,
    Paused,
    Complete,
    Failed,
    Cancelled,
}

/// <summary>
/// Represents a single file transfer in the queue. Mutable state raises change notifications
/// so a queue window can follow a transfer without polling.
/// </summary>
public sealed class TransferItem : INotifyPropertyChanged
{
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Source path on the remote (download) or local (upload) side.</summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>Destination path.</summary>
    public string DestinationPath { get; init; } = string.Empty;

    /// <summary>Connection info used to create the VFS for this transfer.</summary>
    public ConnectionInfo Connection { get; init; } = new();

    /// <summary>Direction of the transfer.</summary>
    public TransferDirection Direction { get; init; }

    /// <summary>
    /// The user agreed to replace an existing destination. Without it the queue refuses to touch a
    /// file that is already there: a background transfer that overwrites silently is data loss.
    /// </summary>
    public bool OverwriteExisting { get; init; }

    /// <summary>File name shown in the queue window.</summary>
    public string FileName => new VfsPath(SourcePath).FileName;

    // ── Mutable state (updated by TransferQueue) ──────────────────────────────

    private TransferStatus _status = TransferStatus.Pending;
    private long _bytesTransferred;
    private long _totalBytes = -1;
    private string? _errorMessage;

    public TransferStatus Status
    {
        get => _status;
        internal set => Set(ref _status, value);
    }

    /// <summary>Total file size in bytes, or -1 if unknown (learned from the server once it answers).</summary>
    public long TotalBytes
    {
        get => _totalBytes;
        init => _totalBytes = value;
    }

    /// <summary>Bytes transferred so far (survives pause/resume).</summary>
    public long BytesTransferred
    {
        get => _bytesTransferred;
        internal set
        {
            if (Set(ref _bytesTransferred, value))
            {
                OnPropertyChanged(nameof(PercentComplete));
            }
        }
    }

    /// <summary>Error message when Status == Failed.</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        internal set => Set(ref _errorMessage, value);
    }

    /// <summary>0–100, or 0 while the total size is still unknown.</summary>
    public double PercentComplete =>
        TotalBytes > 0 ? Math.Min(100d, BytesTransferred * 100d / TotalBytes) : 0d;

    /// <summary>True for a transfer that has stopped for good (done, failed or cancelled).</summary>
    public bool IsFinished => Status is TransferStatus.Complete or TransferStatus.Failed or TransferStatus.Cancelled;

    /// <summary>Used internally to resume a paused transfer.</summary>
    internal CancellationTokenSource? Cts { get; set; }

    /// <summary>The queue opened the local destination file itself, so cancelling may delete it.</summary>
    internal bool OwnsLocalFile { get; set; }

    /// <summary>
    /// The queue has written to the remote destination itself (with the user's consent, or onto
    /// nothing), so a restart may replace what it left there.
    /// </summary>
    internal bool OwnsRemoteFile { get; set; }

    /// <summary>
    /// Size and UTC modification time of the source when the bytes transferred so far were read.
    /// A resume continues only while the source still matches: otherwise the result would be the
    /// head of one file spliced onto the tail of another.
    /// </summary>
    internal (long Size, DateTime ModifiedUtc)? SourceStamp { get; set; }

    /// <summary>The ETag or Last-Modified a WebDAV server gave for the download, sent back as If-Range on resume.</summary>
    internal string? ResumeValidator { get; set; }

    // ── Attempts (what the operation log records) ─────────────────────────────

    private int _attempt = 1;
    private int _reportedAttempt;

    /// <summary>The run the user started last: the first one, then one more per Retry. Pause and Resume stay within a run.</summary>
    internal int Attempt => Volatile.Read(ref _attempt);

    /// <summary>A retry is a new run, and gets its own line in the log.</summary>
    internal void BeginNewAttempt() => Interlocked.Increment(ref _attempt);

    /// <summary>
    /// True the first time the end of <paramref name="attempt"/> is claimed. A pause that races a cancel
    /// ends one run on two threads (the worker and the window), and the log must hear of it once.
    /// </summary>
    internal bool TryClaimEnd(int attempt)
    {
        while (true)
        {
            var reported = Volatile.Read(ref _reportedAttempt);
            if (reported >= attempt)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _reportedAttempt, attempt, reported) == reported)
            {
                return true;
            }
        }
    }

    /// <summary>Learned total size (internal: the queue records what the server reports).</summary>
    internal void LearnTotal(long total)
    {
        if (total > 0 && _totalBytes != total)
        {
            _totalBytes = total;
            OnPropertyChanged(nameof(TotalBytes));
            OnPropertyChanged(nameof(PercentComplete));
        }
    }

    // ── INotifyPropertyChanged ────────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        if (name == nameof(Status))
        {
            OnPropertyChanged(nameof(IsFinished));
        }

        return true;
    }
}

public enum TransferDirection
{
    Download,
    Upload,
}
