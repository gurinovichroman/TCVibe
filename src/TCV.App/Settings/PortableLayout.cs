using System.IO;
using Velopack.Locators;

namespace TCV.App.Settings;

/// <summary>
/// Which layout this copy runs from (ADR-0031). The portable package is Velopack's:
/// <c>TCVibe.exe</c> (a stub), <c>Update.exe</c>, <c>current\</c> with the binaries and
/// <c>data\</c> with the state. A dev build or an unpacked pre-Velopack ZIP is a plain folder.
/// </summary>
public static class PortableLayout
{
    /// <summary>The folder that holds <c>current\</c> and <c>data\</c>, or null for a plain folder.</summary>
    public static string? Root { get; private set; }

    /// <summary>True when this copy can update itself: Velopack's layout, with this exe inside <c>current\</c>.</summary>
    public static bool IsVelopack => Root is not null;

    /// <summary>How many files and folders <see cref="LegacyDataMigration"/> brought over on this start.</summary>
    public static int MigratedEntries { get; private set; }

    /// <summary>
    /// The state folder given with <c>--data</c>, or null when the layout chose it. Every process this
    /// one starts as itself (a guest window, the restart after an update) has to be handed it again.
    /// </summary>
    public static string? CustomDataDirectory { get; private set; }

    /// <summary>
    /// Looks for the layout and points the state at <paramref name="customDataDirectory"/> or, in the
    /// layout, at <c>data\</c>, moving an older copy's state there. Runs in <c>Main</c> right after
    /// Velopack's hooks, before logging, DI or any store reads a path.
    /// </summary>
    public static void Initialize(string? customDataDirectory)
    {
        Root = FindRoot();
        try
        {
            if (customDataDirectory is not null)
            {
                // Taken as it is: nothing migrates into a folder chosen by hand.
                CustomDataDirectory = customDataDirectory;
                TCV.Core.Domain.AppPaths.UseDataDirectory(customDataDirectory);
            }
            else if (Root is { } root)
            {
                TCV.Core.Domain.AppPaths.UseDataDirectory(Path.Combine(root, "data"));
                MigratedEntries = LegacyDataMigration.Run(root, TCV.Core.Domain.AppPaths.DataDirectory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // data\ cannot be created: a read-only medium or a locked-down folder. This runs before
            // logging and the crash handlers, so throwing would end the start without a word; the
            // stores already cope with a location they cannot write.
        }
    }

    private static string? FindRoot()
    {
        try
        {
            var locator = VelopackLocator.Current;
            if (string.IsNullOrEmpty(locator?.RootAppDir) || string.IsNullOrEmpty(locator.AppContentDir))
            {
                return null;
            }

            // A Velopack-looking folder is not enough: the exe must be the one inside current\, or a
            // dev build that happens to sit next to an Update.exe would move its state.
            var exeDir = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\');
            var content = Path.GetFullPath(locator.AppContentDir).TrimEnd('\\');
            return string.Equals(exeDir, content, StringComparison.OrdinalIgnoreCase) ? locator.RootAppDir : null;
        }
        catch (Exception)
        {
            // No layout to be found is the dev build's normal case, whatever the locator throws for it.
            return null;
        }
    }
}
