using Microsoft.Extensions.Logging;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Operations;

public sealed class CreateDirectoryOperation : FileOperationBase
{
    private readonly IVirtualFileSystem _fs;
    private readonly VfsPath _path;
    private readonly ILogger _logger;

    public CreateDirectoryOperation(
        IVirtualFileSystem fs,
        VfsPath path,
        ILogger logger)
    {
        _fs = fs;
        _path = path;
        _logger = logger;
    }

    public override OperationLogDescriptor LogDescriptor =>
        new(OperationKind.CreateFolder, _path.Value, null, 1);

    public override async Task ExecuteAsync(CancellationToken ct)
    {
        State = OperationState.Running;
        try
        {
            await _fs.CreateDirectoryAsync(_path, ct);
            _logger.LogDebug("Created directory {Path}", _path);
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
