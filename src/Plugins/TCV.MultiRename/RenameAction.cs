using TCV.Core.Domain;

namespace TCV.MultiRename;

/// <summary>
/// One completed rename, recorded so it can be undone: the file now lives at
/// <see cref="CurrentPath"/> and was originally called <see cref="OriginalName"/>.
/// </summary>
public sealed record RenameAction(VfsPath CurrentPath, string OriginalName);
