namespace TCV.App.FileSystem;

/// <summary>
/// How much room a volume has: what the current user may still write (quotas counted) and its size.
/// Shown on each panel's drive strip, as Total Commander and Double Commander show it beside the drive.
/// </summary>
public readonly record struct DriveSpace(long Free, long Total)
{
    /// <summary>How full the volume is, 0…1, for the bar.</summary>
    public double UsedFraction => Total > 0 ? Math.Clamp(1 - Free / (double)Total, 0, 1) : 0;

    /// <summary>Less than a tenth left: Explorer turns its drive bar red at the same point.</summary>
    public bool IsLow => Total > 0 && Free < Total / 10;

    /// <summary>
    /// The root of the volume a Windows path is on: <c>C:\</c> for a drive, <c>\\server\share\</c> for a
    /// share; empty for anything else — a path inside an archive or on a server (<c>/docs</c>), or a bare
    /// <c>C:</c>, which means "the current folder of C:", not its root.
    /// </summary>
    public static string RootOf(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/')
        {
            return char.ToUpperInvariant(path[0]) + @":\";
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal)
            && !path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            var parts = path[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? $@"\\{parts[0]}\{parts[1]}\" : string.Empty;
        }

        return string.Empty;
    }
}
