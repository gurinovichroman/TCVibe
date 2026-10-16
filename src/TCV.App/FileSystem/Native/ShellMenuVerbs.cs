namespace TCV.App.FileSystem.Native;

/// <summary>What happens to a command picked from the Windows shell menu.</summary>
public enum ShellMenuAction
{
    /// <summary>The shell runs it: Open with, Send to, Properties, an extension's own item.</summary>
    Shell,
    Rename,
    MultiRename,
    Delete,
    DeletePermanent,
    Cut,
    Copy,
    Paste,
    NewFolder,
    EnterFolder,
}

/// <summary>
/// The menu that was opened: one for the folder's empty space (Paste, New ▸), or one for
/// <see cref="ItemCount"/> files and folders.
/// </summary>
public readonly record struct ShellMenuScope(bool IsFolderBackground, int ItemCount, bool SingleItemIsFolder)
{
    public static ShellMenuScope Background => new(true, 0, false);

    public static ShellMenuScope Items(int count, bool singleItemIsFolder) => new(false, count, singleItemIsFolder);
}

/// <summary>
/// Which shell menu verbs the manager runs itself. The shell would rename, delete and paste past the
/// queue and the log (INV-10) — and an agent session open over the folder would be blamed for it
/// (TRAP-31) — and would delete without our confirmation (INV-11). So these verbs come back to the
/// manager's own commands, as Total Commander does with the same menu; everything else stays the
/// shell's. Verbs are the canonical ones <c>IContextMenu::GetCommandString</c> reports.
/// </summary>
public static class ShellMenuVerbs
{
    public static ShellMenuAction Route(string? verb, ShellMenuScope scope, bool shiftDown = false)
    {
        if (string.IsNullOrEmpty(verb))
        {
            return ShellMenuAction.Shell;
        }

        if (scope.IsFolderBackground)
        {
            return verb.ToLowerInvariant() switch
            {
                "paste" => ShellMenuAction.Paste,
                "newfolder" => ShellMenuAction.NewFolder,   // New ▸ Folder (CMDSTR_NEWFOLDER)
                _ => ShellMenuAction.Shell,
            };
        }

        var single = scope.ItemCount == 1;
        return verb.ToLowerInvariant() switch
        {
            // Explorer renames a multi-selection one by one; the manager has Multi-Rename for that.
            "rename" => single ? ShellMenuAction.Rename : ShellMenuAction.MultiRename,
            "delete" => shiftDown ? ShellMenuAction.DeletePermanent : ShellMenuAction.Delete,
            "cut" => ShellMenuAction.Cut,
            "copy" => ShellMenuAction.Copy,
            // The shell offers Paste on a folder only: it pastes into that folder.
            "paste" when single && scope.SingleItemIsFolder => ShellMenuAction.Paste,
            // Open on a folder goes into it in the panel, as Explorer opens it in the same window.
            // On a file, and «Open in new window», stay the shell's.
            "open" when single && scope.SingleItemIsFolder => ShellMenuAction.EnterFolder,
            _ => ShellMenuAction.Shell,
        };
    }

    /// <summary>
    /// The name a shell command added to the folder when it added exactly one (New ▸ Text Document),
    /// so the cursor can land on it; null when nothing or several were added.
    /// </summary>
    public static string? SingleNewName(IEnumerable<string> before, IEnumerable<string> after)
    {
        var known = new HashSet<string>(before, StringComparer.OrdinalIgnoreCase);
        string? added = null;
        foreach (var name in after)
        {
            if (known.Contains(name))
            {
                continue;
            }

            if (added is not null)
            {
                return null;
            }

            added = name;
        }

        return added;
    }
}
