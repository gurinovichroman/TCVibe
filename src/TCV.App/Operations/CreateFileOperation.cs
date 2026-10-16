using Microsoft.Extensions.Logging;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Operations;

/// <summary>
/// Creates an empty file (Shift+F4). Through the queue like every other mutation (INV-10), so the
/// new file has a log line and is not taken for an agent's work. Never replaces an existing file.
/// </summary>
public sealed class CreateFileOperation : FileOperationBase
{
    private readonly IVirtualFileSystem _fs;
    private readonly VfsPath _path;
    private readonly ILogger _logger;

    public CreateFileOperation(IVirtualFileSystem fs, VfsPath path, ILogger logger)
    {
        _fs = fs;
        _path = path;
        _logger = logger;
    }

    public override OperationLogDescriptor LogDescriptor =>
        new(OperationKind.CreateFile, _path.Value, null, 1);

    public override async Task ExecuteAsync(CancellationToken ct)
    {
        State = OperationState.Running;
        try
        {
            // overwrite: false — a file that appeared since the name was checked is not truncated.
            await using (await _fs.OpenWriteAsync(_path, overwrite: false, ct)) { }
            _logger.LogDebug("Created file {Path}", _path);
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
