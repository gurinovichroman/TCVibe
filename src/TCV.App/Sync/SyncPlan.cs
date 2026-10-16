using System.IO;
using Microsoft.Extensions.Logging;
using TCV.App.FileSystem;
using TCV.App.Localization;
using TCV.App.Operations;
using TCV.App.Operations.Conflict;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Sync;

/// <summary>One ticked row of the comparison with the direction the user settled on.</summary>
public sealed record SyncPlanItem(ComparisonRow Row, SyncDirection Direction)
{
    /// <summary>The entry that will be copied.</summary>
    public IFileEntry? Source => Direction == SyncDirection.ToRight ? Row.Left : Row.Right;

    /// <summary>The entry that will be replaced, or null when the copy creates something new.</summary>
    public IFileEntry? Target => Direction == SyncDirection.ToRight ? Row.Right : Row.Left;

    /// <summary>A file against a folder (or the reverse): copying either way would destroy one of them.</summary>
    public bool KindMismatch => Row.Left is not null && Row.Right is not null
                                && Row.Left.IsDirectory != Row.Right.IsDirectory;
}

/// <summary>What a plan is about to do to the target side — shown before anything is touched (INV-11).</summary>
/// <param name="NewFolders">Folders that will be created.</param>
/// <param name="NewFiles">Files that will be created.</param>
/// <param name="Overwritten">Existing files that will be replaced.</param>
/// <param name="OverwrittenNewer">Of those, files whose target copy is <b>newer</b> than the source — the
/// arrow points the wrong way round, which is the classic way to lose a day of work.</param>
/// <param name="Skipped">Rows that cannot be synchronised (a file against a folder).</param>
/// <param name="OverwriteSamples">Up to a handful of relative paths of replaced files, for the dialog.</param>
/// <param name="NewerSamples">Up to a handful of relative paths of replaced files that were newer.</param>
public sealed record SyncPlanSummary(
    int NewFolders,
    int NewFiles,
    int Overwritten,
    int OverwrittenNewer,
    int Skipped,
    IReadOnlyList<string> OverwriteSamples,
    IReadOnlyList<string> NewerSamples)
{
    /// <summary>Number of samples listed in the confirmation.</summary>
    public const int SampleCount = 8;

    public int Total => NewFolders + NewFiles + Overwritten;

    public static SyncPlanSummary Of(IReadOnlyList<SyncPlanItem> plan, TimeSpan tolerance)
    {
        int folders = 0, created = 0, overwritten = 0, newer = 0, skipped = 0;
        var samples = new List<string>();
        var newerSamples = new List<string>();

        foreach (var item in plan)
        {
            if (item.KindMismatch || item.Source is null)
            {
                skipped++;
                continue;
            }

            if (item.Row.IsDirectory)
            {
                if (item.Target is null)
                {
                    folders++;
                }

                continue;
            }

            if (item.Target is null)
            {
                created++;
                continue;
            }

            overwritten++;
            if (samples.Count < SampleCount)
            {
                samples.Add(item.Row.RelativePath);
            }

            if (DirectoryComparison.TimeDifference(item.Target.LastModified, item.Source.LastModified) > tolerance)
            {
                newer++;
                if (newerSamples.Count < SampleCount)
                {
                    newerSamples.Add(item.Row.RelativePath);
                }
            }
        }

        return new SyncPlanSummary(folders, created, overwritten, newer, skipped, samples, newerSamples);
    }
}

/// <summary>One side of a synchronisation: a file system and the folder being synchronised.</summary>
/// <param name="Fs">The file system the folder lives on.</param>
/// <param name="Root">The folder, in that file system's own form (Windows path on disk, VFS path in a mount).</param>
/// <param name="IsMounted">The panel shows a mount (archive, network, search results) rather than the plain disk.</param>
public sealed record SyncEndpoint(IVirtualFileSystem Fs, string Root, bool IsMounted)
{
    /// <summary>The plain local disk: the only place with a Recycle Bin and timestamps we can copy natively.</summary>
    public bool IsLocalDisk => Fs is WindowsFileSystem && !IsMounted;

    /// <summary>Joins <see cref="Root"/> and a relative path with the separator this side speaks (TRAP-18).</summary>
    public string Combine(string relative)
    {
        if (string.IsNullOrEmpty(relative))
        {
            return Root;
        }

        return IsMounted
            ? Root.TrimEnd('/') + "/" + relative
            : Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
    }
}

/// <summary>What a run did.</summary>
/// <param name="FoldersCreated">Folders created on the target.</param>
/// <param name="FilesCopied">Files written.</param>
/// <param name="FilesReplaced">Of those, files that replaced an existing one (sent to the Recycle Bin first when local).</param>
/// <param name="Failures">Human-readable reasons for everything that did not happen.</param>
/// <param name="Unstamped">Copied files whose target would not take the source's modification time
/// (WebDAV, an FTP server without MFMT). They carry the copy time instead and will look newer than
/// their source on the next comparison.</param>
public sealed record SyncRunResult(
    int FoldersCreated, int FilesCopied, int FilesReplaced, IReadOnlyList<string> Failures, int Unstamped = 0)
{
    public bool Succeeded => Failures.Count == 0;
}

/// <summary>
/// Carries out a synchronisation plan through the operation queue (INV-10): folders first, then the
/// files of each target folder in one copy. Everything it does lands in the operation log.
/// </summary>
/// <remarks>
/// Overwriting is the dangerous part (INV-11). On the local disk every file that is about to be
/// replaced goes to the Recycle Bin first, through a logged delete, so a wrong arrow is recoverable.
/// Servers have no bin, so the confirmation says the replacement is final. Timestamps are carried
/// across — without that a copy is stamped "now" and looks newer than its source on the next
/// comparison, so the two sides flip-flop instead of settling.
/// </remarks>
public sealed class SyncPlanRunner
{
    private readonly ILogger<CopyOperation> _copyLogger;
    private readonly ILogger<DeleteOperation> _deleteLogger;
    private readonly ILogger<CreateDirectoryOperation> _directoryLogger;

    public SyncPlanRunner(
        ILogger<CopyOperation> copyLogger,
        ILogger<DeleteOperation> deleteLogger,
        ILogger<CreateDirectoryOperation> directoryLogger)
    {
        _copyLogger = copyLogger;
        _deleteLogger = deleteLogger;
        _directoryLogger = directoryLogger;
    }

    /// <summary>
    /// Runs <paramref name="plan"/>. <paramref name="execute"/> enqueues one operation (with whatever
    /// progress UI the caller wants) and returns whether it completed.
    /// </summary>
    public async Task<SyncRunResult> RunAsync(
        IReadOnlyList<SyncPlanItem> plan,
        SyncEndpoint left,
        SyncEndpoint right,
        Func<FileOperationBase, Task<bool>> execute,
        CancellationToken ct = default)
    {
        var failures = new List<string>();
        var foldersCreated = 0;
        var filesCopied = 0;
        var filesReplaced = 0;
        var unstamped = 0;

        foreach (var item in plan.Where(p => p.KindMismatch))
        {
            failures.Add(string.Format(LocalizationManager.Get("Cmp_FailKind"), item.Row.RelativePath));
        }

        var runnable = plan.Where(p => !p.KindMismatch && p.Source is not null).ToList();

        // Folders first: their contents are separate rows, and a file cannot land in a folder that is not there.
        foreach (var item in runnable.Where(p => p.Row.IsDirectory))
        {
            ct.ThrowIfCancellationRequested();
            var target = TargetOf(item, left, right);
            var path = new VfsPath(target.Combine(item.Row.RelativePath));

            if (await SafeExistsAsync(target.Fs, path, ct).ConfigureAwait(false))
            {
                continue;
            }

            if (await execute(new CreateDirectoryOperation(target.Fs, path, _directoryLogger)).ConfigureAwait(false))
            {
                foldersCreated++;
            }
            else
            {
                failures.Add(string.Format(LocalizationManager.Get("Cmp_FailFolder"), item.Row.RelativePath));
            }
        }

        var files = runnable.Where(p => !p.Row.IsDirectory)
            .GroupBy(p => (p.Direction, p.Row.RelativeDirectory));

        foreach (var group in files)
        {
            ct.ThrowIfCancellationRequested();

            var (direction, relativeDirectory) = group.Key;
            var source = direction == SyncDirection.ToRight ? left : right;
            var target = direction == SyncDirection.ToRight ? right : left;
            var targetDirectory = new VfsPath(target.Combine(relativeDirectory));

            var items = group.ToList();
            var toCopy = items.Select(i => i.Source!).ToList();

            // Files that already exist on the target are about to be replaced: put them in the
            // Recycle Bin first. If that fails they are not overwritten — a copy that cannot be
            // undone is not made behind the user's back.
            var replacing = items.Where(i => i.Target is not null).ToList();
            if (replacing.Count > 0 && target.IsLocalDisk)
            {
                // Only what really has a bin goes there: on a network share, a removable drive or a
                // path beyond MAX_PATH DeleteOperation would delete for good, and the confirmation
                // promised the bin.
                var recyclable = replacing.Where(i => RecycleBin.IsAvailableFor(target.Fs, i.Target!.Path)).ToList();
                if (recyclable.Count > 0)
                {
                    var recycle = new DeleteOperation(
                        target.Fs, recyclable.Select(i => i.Target!).ToList(), moveToRecycleBin: true, _deleteLogger);

                    if (!await execute(recycle).ConfigureAwait(false))
                    {
                        foreach (var r in recyclable)
                        {
                            failures.Add(string.Format(LocalizationManager.Get("Cmp_FailRecycle"), r.Row.RelativePath));
                        }

                        var replacingNames = recyclable.Select(r => r.Source!.Path.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        toCopy = toCopy.Where(e => !replacingNames.Contains(e.Path.Value)).ToList();
                        replacing.RemoveAll(recyclable.Contains);
                    }
                }
            }

            if (toCopy.Count == 0)
            {
                continue;
            }

            var copy = new CopyOperation(
                source.Fs, target.Fs, toCopy, targetDirectory,
                new OverwriteStrategy(), new CopyOptions(PreserveTimestamps: true), _copyLogger);

            if (!await execute(copy).ConfigureAwait(false))
            {
                failures.Add(string.Format(LocalizationManager.Get("Cmp_FailCopy"),
                    relativeDirectory.Length == 0 ? "/" : relativeDirectory));
                continue;
            }

            filesCopied += toCopy.Count;
            filesReplaced += replacing.Count;

            unstamped += await StampAsync(toCopy, targetDirectory, target.Fs, ct).ConfigureAwait(false);
        }

        return new SyncRunResult(foldersCreated, filesCopied, filesReplaced, failures, unstamped);
    }

    private static SyncEndpoint TargetOf(SyncPlanItem item, SyncEndpoint left, SyncEndpoint right) =>
        item.Direction == SyncDirection.ToRight ? right : left;

    /// <summary>
    /// Gives each copied file its source's modification time, wherever the target can take it.
    /// Returns how many would not take it — those were silently "newer" on the next comparison.
    /// </summary>
    private static async Task<int> StampAsync(
        IReadOnlyList<IFileEntry> copied, VfsPath targetDirectory, IVirtualFileSystem targetFs, CancellationToken ct)
    {
        if (targetFs is not IModifiedTimeWriter writer)
        {
            return copied.Count;
        }

        var refused = 0;
        foreach (var entry in copied)
        {
            ct.ThrowIfCancellationRequested();
            if (!await writer.TrySetLastModifiedAsync(targetDirectory.Combine(entry.Name), entry.LastModified, ct).ConfigureAwait(false))
            {
                refused++;
            }
        }

        return refused;
    }

    private static async Task<bool> SafeExistsAsync(IVirtualFileSystem fs, VfsPath path, CancellationToken ct)
    {
        try
        {
            return await fs.GetEntryAsync(path, ct).ConfigureAwait(false) is { IsDirectory: true };
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }
}

/// <summary>The text of the synchronisation confirmation: what will be created and what replaced (INV-11).</summary>
public static class SyncConfirmation
{
    /// <summary>The disk side has a bin only on a fixed local drive; a share or a removable drive deletes for good.</summary>
    private static bool HasRecycleBin(SyncEndpoint endpoint) =>
        endpoint.IsLocalDisk && RecycleBin.IsAvailableForWindowsPath(endpoint.Root);

    /// <summary>Builds the message the user confirms before a synchronisation touches anything.</summary>
    public static string Describe(
        SyncPlanSummary summary, IReadOnlyList<SyncPlanItem> plan, SyncEndpoint left, SyncEndpoint right)
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine(string.Format(
            LocalizationManager.Get("Cmp_ConfirmCounts"), summary.NewFolders, summary.NewFiles, summary.Overwritten));

        if (summary.Overwritten > 0)
        {
            text.AppendLine();
            text.AppendLine(LocalizationManager.Get("Cmp_ConfirmReplaced"));
            foreach (var path in summary.OverwriteSamples)
            {
                text.AppendLine("  " + path);
            }

            if (summary.Overwritten > summary.OverwriteSamples.Count)
            {
                text.AppendLine(string.Format(LocalizationManager.Get("Cmp_ConfirmMore"), summary.Overwritten - summary.OverwriteSamples.Count));
            }

            var anyServerReplaced = plan.Any(i => i.Target is not null && !i.Row.IsDirectory
                && !HasRecycleBin(i.Direction == SyncDirection.ToRight ? right : left));
            text.AppendLine();
            text.AppendLine(LocalizationManager.Get(anyServerReplaced ? "Cmp_ConfirmNoBin" : "Cmp_ConfirmBin"));
        }

        if (summary.OverwrittenNewer > 0)
        {
            text.AppendLine();
            text.AppendLine(string.Format(LocalizationManager.Get("Cmp_ConfirmNewer"), summary.OverwrittenNewer));
            foreach (var path in summary.NewerSamples)
            {
                text.AppendLine("  ⚠ " + path);
            }
        }

        if (summary.Skipped > 0)
        {
            text.AppendLine();
            text.AppendLine(string.Format(LocalizationManager.Get("Cmp_ConfirmSkipped"), summary.Skipped));
        }

        text.AppendLine();
        text.Append(LocalizationManager.Get("Cmp_ConfirmQuestion"));
        return text.ToString();
    }

}
