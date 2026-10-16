using TCV.App.Services;
using TCV.Network;

namespace TCV.App.Operations;

/// <summary>
/// Operation-log lines for the two writers that run beside <see cref="OperationQueue"/> rather than
/// through it (INV-10, 6.49): the background transfer queue (Shift+F5) and the write-back of a file
/// edited on a server (F4, <see cref="RemoteEditService"/>).
/// </summary>
/// <remarks>
/// Neither goes through the queue, on purpose. The queue runs one operation at a time: an hour-long
/// background download would hold every copy, delete and agent write behind it, which is what Shift+F5
/// exists to avoid, and the transfer queue has its own pause, resume and retry. A remote-edit save is
/// started by the editor, retries and checks for conflicts on its own, and must still reach the server
/// on exit while the queue is busy or paused. What INV-10 needs from them is the journal: every run that
/// ends — completed, failed or cancelled — becomes one line.
/// <para>
/// The line is shaped like a copy's (<see cref="CopyOperation"/>): the source item, the destination
/// <em>folder</em> (the full path only when the name changed), one item, and the folder the file arrived
/// in said outright (<see cref="OperationLogDescriptor.ArrivedIn"/>, what the name index rescans, TRAP-62).
/// Change attribution relies on that shape. Local paths are written the Windows way, server paths as
/// the server spells them — behind the server's address when it is known
/// (<c>sftp://deploy@example.org/var/www</c>): «/var/www» alone does not say which server.
/// </para>
/// </remarks>
public static class RemoteTransferLog
{
    /// <summary>Writes every run of <paramref name="queue"/> that ends to <paramref name="log"/>.</summary>
    public static void Attach(TransferQueue queue, OperationLog log) =>
        queue.Finished += (_, e) =>
        {
            if (Entry(e, DateTime.Now) is { } entry)
            {
                log.Append(entry);
            }
        };

    /// <summary>The descriptor of a queued transfer.</summary>
    /// <param name="server">The connection the transfer runs over; its address is written before the
    /// server-side path. Null (or one without a host) writes the path alone.</param>
    public static OperationLogDescriptor Describe(
        TransferDirection direction, string sourcePath, string destinationPath, ConnectionInfo? server = null)
    {
        var download = direction == TransferDirection.Download;
        var source = download ? sourcePath : Local(sourcePath);
        var destination = download ? Local(destinationPath) : destinationPath;

        // The folder is found on the bare path first: the address has slashes of its own, and a file
        // in the server's root would otherwise lose the root's «/».
        var target = TargetOf(Leaf(source), destination);
        var arrivedIn = FolderOf(destination);
        if (server is { Host.Length: > 0 })
        {
            if (download)
            {
                source = OnServer(server, source);
            }
            else
            {
                target = OnServer(server, target);
                arrivedIn = OnServer(server, arrivedIn);
            }
        }

        return new OperationLogDescriptor(download ? OperationKind.Download : OperationKind.Upload, source, target, 1)
        {
            // The file lands in this folder whatever it is called there (TRAP-62).
            ArrivedIn = arrivedIn,
        };
    }

    /// <summary>
    /// A server path behind the server's address: scheme, user, host, and the port when it is not the
    /// protocol's default. Never the password. The same name the tab and the connection chip show.
    /// </summary>
    public static string OnServer(ConnectionInfo server, string path) => server.DescribeAddress(path);

    /// <summary>The log line for a run of the transfer queue that ended; null for a state that is not an end.</summary>
    public static OperationLogEntry? Entry(TransferFinishedEventArgs e, DateTime timestamp)
    {
        OperationOutcome? outcome = e.Status switch
        {
            TransferStatus.Complete => OperationOutcome.Completed,
            TransferStatus.Failed => OperationOutcome.Failed,
            TransferStatus.Cancelled => OperationOutcome.Cancelled,
            _ => null,
        };

        return outcome is { } ended
            ? Create(Describe(e.Item.Direction, e.Item.SourcePath, e.Item.DestinationPath, e.Item.Connection), ended, e.Error, timestamp)
            : null;
    }

    /// <summary>
    /// The log line for one write-back of a remote edit. The source is the temporary copy: after a
    /// failed or declined upload it is the only place the edits are, and the log says where it is.
    /// A declined write-back (the server copy changed, the user kept it) did not happen: Cancelled.
    /// </summary>
    public static OperationLogEntry RemoteSave(
        string localCopy, string remotePath, RemoteEditOutcome outcome, string? error, DateTime timestamp)
    {
        var descriptor = Describe(TransferDirection.Upload, localCopy, remotePath);
        var logged = outcome switch
        {
            RemoteEditOutcome.Uploaded => OperationOutcome.Completed,
            RemoteEditOutcome.Failed => OperationOutcome.Failed,
            _ => OperationOutcome.Cancelled,
        };

        return Create(descriptor, logged, logged == OperationOutcome.Failed ? error : null, timestamp);
    }

    private static OperationLogEntry Create(
        OperationLogDescriptor d, OperationOutcome outcome, string? error, DateTime timestamp) => new()
        {
            Timestamp = timestamp,
            Kind = d.Kind,
            Source = d.Source,
            Destination = d.Destination,
            ItemCount = d.ItemCount,
            Outcome = outcome,
            Error = error,
            ArrivedIn = d.ArrivedIn,
        };

    /// <summary>The destination folder, or the whole path when the file arrives under another name.</summary>
    private static string TargetOf(string sourceName, string destination) =>
        string.Equals(Leaf(destination), sourceName, StringComparison.Ordinal) ? FolderOf(destination) : destination;

    /// <summary>The folder holding <paramref name="path"/>; the path itself when it has no parent.</summary>
    private static string FolderOf(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var slash = trimmed.LastIndexOfAny(['\\', '/']);
        if (slash < 0)
        {
            return path;
        }

        // "/a.txt" lives in "/", "D:\a.txt" in "D:\": the root keeps its separator.
        return slash == 0 || (slash == 2 && trimmed[1] == ':') ? trimmed[..(slash + 1)] : trimmed[..slash];
    }

    private static string Leaf(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var slash = trimmed.LastIndexOfAny(['\\', '/']);
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }

    /// <summary>A disk path as Windows writes it: the queue may hold one built from a panel's «D:/dir».</summary>
    private static string Local(string path) => path.Replace('/', '\\');
}
