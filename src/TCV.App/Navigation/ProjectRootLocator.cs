using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Navigation;

/// <summary>
/// Finds the repository root above a directory, so "find file in the project" means the project and
/// not whatever subdirectory the panel happens to show.
/// </summary>
public static class ProjectRootLocator
{
    /// <summary>Markers that identify a working copy. In a git worktree <c>.git</c> is a file, not a
    /// directory — both are accepted.</summary>
    private static readonly string[] Markers = [".git", ".hg", ".svn"];

    /// <summary>Guards against a pathological path; no real tree nests this deep.</summary>
    private const int MaxClimb = 64;

    /// <summary>
    /// Walks up from <paramref name="startPath"/> to the nearest directory holding a VCS marker.
    /// Returns <paramref name="startPath"/> unchanged when there is none — outside a repository the
    /// current directory is the best available scope.
    /// </summary>
    public static async Task<string> FindAsync(
        IVirtualFileSystem vfs, string startPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(startPath)) return startPath;

        var current = new VfsPath(startPath);
        for (var i = 0; i < MaxClimb; i++)
        {
            ct.ThrowIfCancellationRequested();

            foreach (var marker in Markers)
            {
                try
                {
                    if (await vfs.GetEntryAsync(current.Combine(marker), ct) is not null)
                        return current.Value;
                }
                catch (OperationCanceledException) { throw; }
                catch { /* unreadable level — keep climbing */ }
            }

            var parent = current.Parent;
            if (parent is null || parent.Value == current.Value) break;
            current = parent;
        }

        return startPath;
    }
}
