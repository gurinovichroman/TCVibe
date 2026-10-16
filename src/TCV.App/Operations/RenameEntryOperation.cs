using System.IO;
using Microsoft.Extensions.Logging;
using TCV.Core.Interfaces;

namespace TCV.App.Operations;

/// <summary>
/// Renames one entry in place. A rename is a file mutation like any other, so it goes through the
/// queue and the log (INV-10): the agent-changes dashboard tells the manager's edits from an
/// agent's by the log line, and a rename with no line would be blamed on whoever was running.
/// </summary>
public sealed class RenameEntryOperation : FileOperationBase
{
    private readonly IVirtualFileSystem _fs;
    private readonly IFileEntry _entry;
    private readonly string _newName;
    private readonly ILogger _logger;

    public RenameEntryOperation(IVirtualFileSystem fs, IFileEntry entry, string newName, ILogger logger)
    {
        _fs = fs;
        _entry = entry;
        _newName = newName;
        _logger = logger;
    }

    public override OperationLogDescriptor LogDescriptor =>
        new(OperationKind.Rename, _entry.Path.Value, _newName, 1);

    public override async Task ExecuteAsync(CancellationToken ct)
    {
        State = OperationState.Running;
        try
        {
            var target = (_entry.Path.Parent ?? TCV.Core.Domain.VfsPath.Root).Combine(_newName);

            // A name that differs only by case is the same entry on Windows: that is a legal rename.
            if (!string.Equals(target.Value, _entry.Path.Value, StringComparison.OrdinalIgnoreCase)
                && await _fs.GetEntryAsync(target, ct) is not null)
                throw new IOException(string.Format(TCV.App.Localization.LocalizationManager.Get("Op_NameTaken"), _newName));

            await _fs.RenameAsync(_entry.Path, _newName, ct);
            _logger.LogDebug("Renamed {Path} to {Name}", _entry.Path, _newName);
            State = OperationState.Completed;
        }
        catch (OperationCanceledException)
        {
            State = OperationState.Cancelled;
            throw;
        }
        catch (Exception ex)
        {
            State = OperationState.Failed;
            FailureException = ex;
            throw;
        }
    }
}
