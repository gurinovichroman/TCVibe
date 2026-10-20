namespace TCV.App.Operations;

/// <summary>
/// The lanes of <see cref="OperationQueue"/>. Each runs its operations one at a time, and the two run
/// side by side (ADR-0038): an hour's download from a server must not hold every F5/F6/F8 on the disk
/// behind it.
/// </summary>
public enum OperationLane
{
    /// <summary>The disk, archives and plugin file systems: everything that is not a server.</summary>
    Local,

    /// <summary>An operation with a server or a cloud store on one side.</summary>
    Network,
}
