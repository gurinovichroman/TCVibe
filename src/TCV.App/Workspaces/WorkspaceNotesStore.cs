using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TCV.App.Workspaces;

/// <summary>
/// Per-workspace markdown notes — the scratchpad that answers "what was I doing here?" after a
/// week away. Kept out of <c>workspaces.json</c> on purpose: notes are free-form prose that grows,
/// gets pasted into, and is worth reading raw, so each workspace owns a plain <c>.md</c> file under
/// <c>&lt;exe&gt;/workspaces/</c> (portable, INV-03).
/// </summary>
public sealed class WorkspaceNotesStore
{
    /// <summary>Longest slug kept from the workspace name; the hash suffix is what guarantees
    /// uniqueness, so truncating here only costs readability.</summary>
    private const int MaxSlugLength = 40;

    private readonly string _directory;

    public WorkspaceNotesStore()
        : this(global::TCV.Core.Domain.AppPaths.Data("workspaces")) { }

    public WorkspaceNotesStore(string directory) => _directory = directory;

    /// <summary>Full path of the notes file backing <paramref name="workspaceName"/>, whether or not
    /// it exists yet — shown in the dialog so the user can open the file in their own editor.</summary>
    public string PathFor(string workspaceName) =>
        Path.Combine(_directory, FileNameFor(workspaceName));

    /// <summary>Notes for the workspace, or an empty string when there are none. An unreadable file
    /// degrades to empty rather than throwing, mirroring <see cref="WorkspaceStore.LoadAll"/>.</summary>
    public string Read(string workspaceName)
    {
        try
        {
            var path = PathFor(workspaceName);
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>True when the workspace has non-blank notes — drives the menu marker.</summary>
    public bool HasNotes(string workspaceName) =>
        !string.IsNullOrWhiteSpace(Read(workspaceName));

    /// <summary>
    /// Saves the notes. Blank text deletes the file instead of leaving an empty one behind, so
    /// "cleared my notes" and "never wrote any" stay the same state on disk.
    /// </summary>
    public void Write(string workspaceName, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            Delete(workspaceName);
            return;
        }

        Directory.CreateDirectory(_directory);
        File.WriteAllText(PathFor(workspaceName), text, Encoding.UTF8);
    }

    /// <summary>
    /// Moves the notes to the file name of the new workspace name, so a rename does not orphan them.
    /// Never overwrites: if the target name already has notes (a stale file from a deleted
    /// workspace) the old file stays where it is. Returns true when nothing had to move or the
    /// move succeeded.
    /// </summary>
    public bool Rename(string oldName, string newName)
    {
        try
        {
            var from = PathFor(oldName);
            var to = PathFor(newName);
            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!File.Exists(from))
            {
                return true;
            }

            if (File.Exists(to))
            {
                return false;
            }

            File.Move(from, to);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Removes the notes file; a workspace without one is not an error.</summary>
    public void Delete(string workspaceName)
    {
        try
        {
            var path = PathFor(workspaceName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Notes are auxiliary: failing to delete them must not abort deleting the workspace.
        }
    }

    /// <summary>
    /// Maps a workspace name to a file name: a readable slug plus a short content hash. The hash is
    /// not decoration — names are keyed case-insensitively but are otherwise free text, so
    /// <c>"api/v1"</c> and <c>"api:v1"</c> both slug to <c>api-v1</c> and would silently share one
    /// notes file. It also defuses the reserved device names (<c>CON</c>, <c>PRN</c>, <c>COM1</c>…),
    /// which stay unopenable even with an extension: <c>con-1a2b3c4d.md</c> is an ordinary file.
    /// </summary>
    public static string FileNameFor(string workspaceName)
    {
        // Lower-cased key, because the workspace store treats "Work" and "work" as one workspace.
        var key = (workspaceName ?? string.Empty).Trim().ToLowerInvariant();
        return $"{Slug(key)}-{ShortHash(key)}.md";
    }

    /// <summary>Unicode-aware slug: letters and digits survive (a Russian-first product should not
    /// turn "Мой проект" into a row of dashes), everything else collapses to a single dash.</summary>
    private static string Slug(string key)
    {
        var sb = new StringBuilder(key.Length);
        foreach (var ch in key)
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_')
            {
                sb.Append(ch);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }

            if (sb.Length >= MaxSlugLength)
            {
                break;
            }
        }

        var slug = sb.ToString().Trim('-', '_');
        return slug.Length == 0 ? "workspace" : slug;
    }

    /// <summary>SHA-256 prefix — deterministic across runs, unlike <see cref="string.GetHashCode()"/>,
    /// which is randomised per process and would orphan every notes file on restart.</summary>
    private static string ShortHash(string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }
}
