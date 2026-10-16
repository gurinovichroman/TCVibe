using System.IO;

namespace TCV.App.Operations;

/// <summary>What the user decided after an item of an operation failed.</summary>
public enum OperationErrorResolution
{
    Retry,
    Skip,
    /// <summary>Skip this item and every later one that fails, without asking again.</summary>
    SkipAll,
    Abort,
}

/// <summary>An item that could not be processed, shown to the user so they can choose what happens next.</summary>
public sealed record OperationErrorInfo(string Path, string Message, Exception Exception);

/// <summary>Asks the user (or a policy) how to continue after <paramref name="error"/>.</summary>
public delegate Task<OperationErrorResolution> OperationErrorHandler(
    OperationErrorInfo error, CancellationToken ct);

/// <summary>
/// The destination of a copy or move lies inside the folder being copied or moved. Explorer and TC
/// refuse it; silently nesting a folder into itself is never what the user meant.
/// </summary>
public sealed class DestinationInsideSourceException : IOException
{
    public DestinationInsideSourceException(string name)
        : base($"Cannot copy or move '{name}' into itself.")
    {
        ItemName = name;
    }

    public string ItemName { get; }
}
