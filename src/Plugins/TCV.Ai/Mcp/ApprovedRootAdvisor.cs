namespace TCV.Ai.Mcp;

/// <summary>
/// Judges whether a directory is too wide to hand to an agent without a second thought. Approving
/// <c>C:\</c> or the whole user profile makes the approved-roots list meaningless — the agent then
/// reaches SSH keys, browser profiles and every other project — so the app asks before it accepts one.
/// Pure: the caller supplies the profile and system folders, which keeps it testable.
/// </summary>
public static class ApprovedRootAdvisor
{
    /// <summary>
    /// True for a drive root, for the user profile, for any folder that contains the profile
    /// (<c>C:\Users</c>), and for the Windows directory.
    /// </summary>
    public static bool IsBroad(string path, string? userProfile, string? windowsDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        string full;
        try
        {
            full = Trim(Path.GetFullPath(path));
        }
        catch
        {
            return false;
        }

        var root = Path.GetPathRoot(full);
        if (!string.IsNullOrEmpty(root) && string.Equals(Trim(root), full, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(userProfile) && PathScope.IsInside(full, Trim(userProfile)))
            return true;    // the profile itself, or a folder above it

        return !string.IsNullOrWhiteSpace(windowsDirectory)
               && PathScope.IsInside(Trim(windowsDirectory), full);
    }

    private static string Trim(string path) => path.TrimEnd('\\', '/');
}
