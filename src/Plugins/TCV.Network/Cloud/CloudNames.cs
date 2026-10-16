namespace TCV.Network.Cloud;

/// <summary>Item names as a panel shows them and as a provider stores them.</summary>
internal static class CloudNames
{
    /// <summary>DIVISION SLASH: what a <c>/</c> inside a Drive name is shown as, so it is no path separator.</summary>
    public const char SlashStandIn = '∕';

    /// <summary>REVERSE SOLIDUS OPERATOR: the same for <c>\</c>, which a VFS path would also read as a separator.</summary>
    public const char BackslashStandIn = '⧵';

    public static string ToDisplay(string name) => name.Replace('/', SlashStandIn).Replace('\\', BackslashStandIn);

    public static string FromDisplay(string name) => name.Replace(SlashStandIn, '/').Replace(BackslashStandIn, '\\');

    /// <summary>Refuses a new name that is empty or would read as a path.</summary>
    public static void Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['/', '\\']) >= 0 || name is "." or "..")
        {
            throw new IOException($"\"{name}\" is not a valid name.");
        }
    }
}
