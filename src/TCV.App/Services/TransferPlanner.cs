using System.IO;
using System.Security.Cryptography;
using System.Text;
using TCV.App.FileSystem;
using TCV.App.Operations;
using TCV.App.Operations.Conflict;
using TCV.App.ViewModels.Dialogs;
using TCV.Core.Domain;
using TCV.Core.Interfaces;
using TCV.Network;

namespace TCV.App.Services;

/// <summary>Why something selected for the transfer queue does not go in. One key per member: <c>Transfer_Skip_&lt;Name&gt;</c>.</summary>
public enum TransferSkipReason
{
    /// <summary>A junction or a symbolic link to a folder: its content lives elsewhere, and following it can loop (TRAP-50, TRAP-64).</summary>
    Link,

    /// <summary>A folder that lists exactly like one above it: a link on a server that cannot say it is one.</summary>
    Cycle,

    /// <summary>Deeper than any real tree: the end of a cycle nothing else caught.</summary>
    TooDeep,

    /// <summary>The folder could not be listed.</summary>
    Unreadable,

    /// <summary>The name cannot exist on the disk, or would land outside the target (a server's «..\\x»).</summary>
    BadName,

    /// <summary>A file where a folder would go, or a folder where a file would go: neither can replace the other.</summary>
    KindClash,
}

/// <summary>Something left out of the queue, and why.</summary>
/// <param name="Path">Where it is, as the source spells it.</param>
/// <param name="Detail">The file system's own words for an unreadable folder.</param>
public sealed record TransferSkip(string Path, TransferSkipReason Reason, string? Detail = null);

/// <summary>One step of a transfer plan: a file to move, or a folder to make at the target.</summary>
/// <param name="SourcePath">As the queue wants it: the server's path, or the Windows path of a disk file.</param>
/// <param name="DestinationPath">Likewise, on the other side.</param>
/// <param name="Size">The listed size; 0 for a folder.</param>
/// <param name="Overwrite">The user agreed to replace what is at the destination.</param>
/// <param name="Display">What the queue window names it: the path under the selection.</param>
public sealed record TransferPlanStep(
    string SourcePath, string DestinationPath, bool IsFolder, long Size, bool Overwrite, string Display);

/// <summary>What the selection turned into: every step in order, each folder before what it holds, and what was left out.</summary>
/// <param name="Stopped">The user answered Cancel to a conflict: nothing goes in the queue.</param>
public sealed record TransferPlan(IReadOnlyList<TransferPlanStep> Steps, IReadOnlyList<TransferSkip> Skips, bool Stopped)
{
    /// <summary>How many of the steps are files.</summary>
    public int FileCount => Steps.Count(s => !s.IsFolder);
}

/// <summary>How far a walk has got: files found so far, and the folder it is in.</summary>
public sealed record TransferPlanProgress(int Files, string Folder);

/// <summary>
/// Turns what is selected in a panel into the queue's items (Shift+F5): files stay files, a folder becomes
/// a step for itself and one for everything under it, with the relative paths kept. Runs off the UI thread
/// (INV-06), one listing per folder on both sides, so that 100 000 files do not cost 100 000 commands.
/// </summary>
/// <remarks>
/// Nothing is left out without a word: every link not followed, folder not read and name not possible is
/// a <see cref="TransferSkip"/>, and the command tells the user. What happens to a name that is taken is
/// <see cref="ConflictPolicy"/>'s to say, as in every copy (TRAP-58). A folder that exists is merged into,
/// as F5 does.
/// </remarks>
public sealed class TransferPlanner
{
    /// <summary>Well past any real tree, and short of what a path can hold: a cycle stops here at the latest.</summary>
    public const int MaxDepth = 64;

    private const int ReportEvery = 64;

    private readonly bool _isDownload;
    private readonly IVirtualFileSystem _source;
    private readonly string _targetRoot;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<IFileEntry>>> _listTarget;
    private readonly Func<IFileEntry, IFileEntry, Task<(ConflictResolution Resolution, bool ApplyToAll)>> _ask;

    private readonly List<TransferPlanStep> _steps = [];
    private readonly List<TransferSkip> _skips = [];
    private readonly Dictionary<string, Dictionary<string, IFileEntry>> _targets;
    private readonly HashSet<string> _walked;
    private (ConflictResolution Resolution, bool ApplyToAll)? _standing;
    private int _files;
    private bool _stopped;

    /// <param name="isDownload">The source is a server and the target the disk; otherwise the other way round.</param>
    /// <param name="source">The file system the selection lives on.</param>
    /// <param name="targetRoot">The folder to put it in, as its own side spells it.</param>
    /// <param name="listTarget">Lists a folder of the target side. A folder that is not there gives an empty list.</param>
    /// <param name="ask">Puts a conflict to the user: source, what is there. Called off the UI thread.</param>
    public TransferPlanner(
        bool isDownload,
        IVirtualFileSystem source,
        string targetRoot,
        Func<string, CancellationToken, Task<IReadOnlyList<IFileEntry>>> listTarget,
        Func<IFileEntry, IFileEntry, Task<(ConflictResolution Resolution, bool ApplyToAll)>> ask)
    {
        _isDownload = isDownload;
        _source = source;
        _targetRoot = targetRoot;
        _listTarget = listTarget;
        _ask = ask;

        // A server's names are as it spells them, the disk's are not.
        _walked = new HashSet<string>(isDownload ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        _targets = new Dictionary<string, Dictionary<string, IFileEntry>>(isDownload ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }

    /// <summary>Walks the selection. Cancelling throws; a changed host key stops it too (TRAP-43), other failures are skips.</summary>
    public async Task<TransferPlan> BuildAsync(
        IReadOnlyList<IFileEntry> selected, IProgress<TransferPlanProgress>? progress, CancellationToken ct)
    {
        foreach (var entry in selected)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.IsDirectory)
            {
                await AddFolderAsync(entry, _targetRoot, entry.Name, 0, [], progress, ct).ConfigureAwait(false);
            }
            else
            {
                await AddFileAsync(entry, _targetRoot, entry.Name, ct).ConfigureAwait(false);
            }

            if (_stopped)
            {
                return new TransferPlan([], _skips, Stopped: true);
            }
        }

        progress?.Report(new TransferPlanProgress(_files, string.Empty));
        return new TransferPlan(_steps, _skips, Stopped: false);
    }

    private string SourceOf(IFileEntry entry) =>
        _isDownload ? entry.Path.Value : WindowsFileSystem.ToWindowsPath(entry.Path);

    private string Join(string folder, string name) =>
        _isDownload ? Path.Combine(folder, name) : new VfsPath(folder).Combine(name).Value;

    /// <summary>The name is one a file can have on the target. A server's listing is not to be trusted with «..» either.</summary>
    private bool NameFits(IFileEntry entry)
    {
        var bad = entry.Name.Length == 0 || entry.Name is "." or ".." || entry.Name.Contains('/')
            || (_isDownload && FileNameRules.GetErrorKey(entry.Name) is not null);
        if (bad)
        {
            _skips.Add(new TransferSkip(SourceOf(entry), TransferSkipReason.BadName));
        }

        return !bad;
    }

    private async Task AddFolderAsync(
        IFileEntry folder, string targetParent, string display, int depth, List<string> above,
        IProgress<TransferPlanProgress>? progress, CancellationToken ct)
    {
        if (!NameFits(folder))
        {
            return;
        }

        var sourcePath = SourceOf(folder);
        if (OperationPaths.IsReparseDirectory(folder))
        {
            _skips.Add(new TransferSkip(sourcePath, TransferSkipReason.Link));
            return;
        }

        if (depth >= MaxDepth)
        {
            _skips.Add(new TransferSkip(sourcePath, TransferSkipReason.TooDeep));
            return;
        }

        if (!_walked.Add(folder.Path.Value))
        {
            return;   // selected twice, or reached again: it is in the plan already.
        }

        progress?.Report(new TransferPlanProgress(_files, sourcePath));

        IReadOnlyList<IFileEntry> entries;
        try
        {
            entries = await _source.ListAsync(folder.Path, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (NetworkErrors.Find<HostKeyChangedException>(ex) is null)
        {
            // A timeout is an unreadable folder as well, not the walk being stopped.
            _skips.Add(new TransferSkip(sourcePath, TransferSkipReason.Unreadable, ex.Message));
            return;
        }

        var signature = Signature(entries);
        if (entries.Count > 0 && above.Contains(signature))
        {
            _skips.Add(new TransferSkip(sourcePath, TransferSkipReason.Cycle));
            return;
        }

        var target = Join(targetParent, folder.Name);
        var existing = await FindAtTargetAsync(targetParent, folder.Name, ct).ConfigureAwait(false);
        if (existing is { IsDirectory: false })
        {
            _skips.Add(new TransferSkip(sourcePath, TransferSkipReason.KindClash));
            return;
        }

        if (existing is null)
        {
            _steps.Add(new TransferPlanStep(sourcePath, target, IsFolder: true, 0, Overwrite: false, display));
            Remember(targetParent, folder);

            // It does not exist yet, so neither does anything in it: no listing to ask for.
            _targets[target] = NewNames();
        }

        above.Add(signature);
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var childDisplay = display + "/" + entry.Name;
            if (entry.IsDirectory)
            {
                await AddFolderAsync(entry, target, childDisplay, depth + 1, above, progress, ct).ConfigureAwait(false);
            }
            else
            {
                await AddFileAsync(entry, target, childDisplay, ct).ConfigureAwait(false);
                if (_files % ReportEvery == 0)
                {
                    progress?.Report(new TransferPlanProgress(_files, sourcePath));
                }
            }

            if (_stopped)
            {
                break;
            }
        }

        above.RemoveAt(above.Count - 1);
    }

    private async Task AddFileAsync(IFileEntry entry, string targetFolder, string display, CancellationToken ct)
    {
        if (!NameFits(entry))
        {
            return;
        }

        var name = entry.Name;
        var overwrite = false;
        var existing = await FindAtTargetAsync(targetFolder, name, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.IsDirectory)
            {
                _skips.Add(new TransferSkip(SourceOf(entry), TransferSkipReason.KindClash));
                return;
            }

            // Never replace anything without the user saying so: a background transfer that overwrites
            // silently is data loss nobody sees until later (INV-11). "Overwrite if newer" is settled by
            // the one rule every copy goes by (TRAP-58).
            var resolution = await ConflictPolicy.ResolveAsync(
                new ConflictInfo(entry, existing, new VfsPath(Join(targetFolder, name))),
                onlyIfNewer: false,
                (_, _) => AskAsync(entry, existing),
                ct).ConfigureAwait(false);

            switch (resolution)
            {
                case ConflictResolution.Cancel:
                    _stopped = true;
                    return;
                case ConflictResolution.Skip:
                    return;
                case ConflictResolution.RenameAuto:
                    name = await FreeNameAsync(targetFolder, name, ct).ConfigureAwait(false);
                    break;
                default:
                    overwrite = true;
                    break;
            }
        }

        _files++;
        _steps.Add(new TransferPlanStep(SourceOf(entry), Join(targetFolder, name), IsFolder: false, entry.Size, overwrite, display));

        // Two selected files of one name (search hits from different folders) would meet here.
        RememberAs(targetFolder, name, entry);
    }

    private async Task<ConflictResolution> AskAsync(IFileEntry source, IFileEntry existing)
    {
        if (_standing is { } standing)
        {
            return standing.Resolution;
        }

        var answer = await _ask(source, existing).ConfigureAwait(false);
        if (answer.ApplyToAll)
        {
            _standing = answer;
        }

        return answer.Resolution;
    }

    private async Task<IFileEntry?> FindAtTargetAsync(string folder, string name, CancellationToken ct)
    {
        var names = await TargetNamesAsync(folder, ct).ConfigureAwait(false);
        return names.GetValueOrDefault(name);
    }

    private async Task<Dictionary<string, IFileEntry>> TargetNamesAsync(string folder, CancellationToken ct)
    {
        if (_targets.TryGetValue(folder, out var known))
        {
            return known;
        }

        var names = NewNames();
        foreach (var entry in await _listTarget(folder, ct).ConfigureAwait(false))
        {
            names.TryAdd(entry.Name, entry);
        }

        _targets[folder] = names;
        return names;
    }

    private Dictionary<string, IFileEntry> NewNames() =>
        new(_isDownload ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private void Remember(string folder, IFileEntry entry) => RememberAs(folder, entry.Name, entry);

    private void RememberAs(string folder, string name, IFileEntry entry)
    {
        if (_targets.TryGetValue(folder, out var names))
        {
            names[name] = entry;
        }
    }

    private async Task<string> FreeNameAsync(string folder, string name, CancellationToken ct)
    {
        var names = await TargetNamesAsync(folder, ct).ConfigureAwait(false);
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var counter = 1; ; counter++)
        {
            var candidate = $"{stem} ({counter}){extension}";
            if (!names.ContainsKey(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// What a folder holds, as one value: names, kinds, sizes and times. A server may present a link to a
    /// folder as a plain folder, so a cycle has no name to be told by, only the same content one level
    /// further down. A real copy nested inside itself would need its own name inside it too, with the same
    /// times to the tick: not something a tree holds by accident, and it is reported, not lost in silence.
    /// </summary>
    private static string Signature(IReadOnlyList<IFileEntry> entries)
    {
        var text = new StringBuilder();
        foreach (var entry in entries.OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            text.Append(entry.Name).Append('|').Append(entry.IsDirectory ? 'd' : 'f').Append('|')
                .Append(entry.Size).Append('|').Append(entry.LastModified.Ticks).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
