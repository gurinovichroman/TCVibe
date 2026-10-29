using System.IO;
using System.Text.Json;
using TCV.App.Settings;

namespace TCV.App.Shells;

/// <summary>
/// The profiles of Windows Terminal's <c>settings.json</c>, and which of them is the command line's shell:
/// a shell started through its own profile gets the profile's title, icon, colours and font, and opened
/// without a command, the profile's own command line. Pure: reading the file is
/// <see cref="Services.ShellService"/>'s.
/// </summary>
/// <remarks>
/// A shell and a profile are the same when they run the same program. A profile Windows Terminal
/// generates by itself has no command line in the file, only its <c>source</c>: PowerShell 7, each WSL
/// distribution (named after it), Git Bash (the fragment Git for Windows installs). Other generated
/// profiles (Visual Studio prompts, Azure) are never matched: what they run is not in the file. Names
/// differ between the two lists («cmd» is «Command Prompt»), so the program decides, and the name only
/// breaks a tie — or lets a profile with arguments of its own (<c>cmd /k vsdevcmd</c>) stand for the
/// shell: without the same name such a profile is a different shell that runs the same program.
/// </remarks>
public static class WindowsTerminalProfiles
{
    private const string PowerShellCoreSource = "Windows.Terminal.PowershellCore";
    private const string WslSource = "Windows.Terminal.Wsl";
    private const string GitSource = "Git";

    /// <summary>
    /// The profiles in <paramref name="json"/>, as written: <c>profiles</c> is either the list itself or
    /// an object holding it in <c>list</c>. The file is JSON with comments and trailing commas; one that
    /// does not read has no profiles.
    /// </summary>
    public static IReadOnlyList<WindowsTerminalProfile> Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("profiles", out var profiles))
            {
                return [];
            }

            if (profiles.ValueKind == JsonValueKind.Object && profiles.TryGetProperty("list", out var list))
            {
                profiles = list;
            }

            if (profiles.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<WindowsTerminalProfile>();
            foreach (var profile in profiles.EnumerateArray())
            {
                if (profile.ValueKind == JsonValueKind.Object && Text(profile, "name") is { Length: > 0 } name)
                {
                    result.Add(new WindowsTerminalProfile(
                        name,
                        Text(profile, "guid"),
                        Text(profile, "source"),
                        Text(profile, "commandline"),
                        profile.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True));
                }
            }

            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The profile that runs <paramref name="shell"/>, or null. A hidden profile is not offered: the user
    /// put it away. Among several, the one named as the shell, else the first in the file.
    /// </summary>
    public static WindowsTerminalProfile? Match(ShellProfile shell, IReadOnlyList<WindowsTerminalProfile> profiles)
    {
        var program = Program.OfShell(shell);
        if (program is null)
        {
            return null;
        }

        WindowsTerminalProfile? first = null;
        foreach (var profile in profiles)
        {
            if (profile.Hidden || Program.OfProfile(profile) is not { } runs || !runs.Runs(program))
            {
                continue;
            }

            var sameName = string.Equals(profile.Name.Trim(), shell.Name.Trim(), StringComparison.OrdinalIgnoreCase);
            if (sameName)
            {
                return profile;
            }

            if (!runs.HasOwnArguments)
            {
                first ??= profile;
            }
        }

        return first;
    }

    /// <summary>What <c>wt -p</c> takes: the guid, which no rename changes, else the name.</summary>
    public static string Selector(WindowsTerminalProfile profile) =>
        profile.Guid is { Length: > 0 } guid ? guid : profile.Name;

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>A program as far as the files say: its file name, its path when one is written, the WSL
    /// distribution, whether it is Git's bash, and whether arguments of its own follow it.</summary>
    private sealed record Program(string FileName, string? FullPath, string? Distribution, bool IsGitBash, bool HasOwnArguments)
    {
        public static Program? OfShell(ShellProfile shell) =>
            Read(shell.Command, ShellCommandLine.SplitArguments(shell.Arguments ?? string.Empty));

        public static Program? OfProfile(WindowsTerminalProfile profile)
        {
            if (!string.IsNullOrWhiteSpace(profile.Commandline))
            {
                // Windows Terminal takes a path with blanks unquoted (C:\Program Files\…\pwsh.exe -nologo),
                // as CreateProcess does: the program runs up to ".exe".
                var line = profile.Commandline.Trim();
                var exe = line.IndexOf(".exe", StringComparison.OrdinalIgnoreCase) + 4;
                if (line[0] != '"' && exe > 4 && (exe == line.Length || line[exe] is ' ' or '\t'))
                {
                    return Read(line[..exe], ShellCommandLine.SplitArguments(line[exe..]));
                }

                var words = ShellCommandLine.SplitArguments(line);
                return words.Count == 0 ? null : Read(words[0], words.Skip(1).ToList());
            }

            return profile.Source switch
            {
                PowerShellCoreSource => new Program("pwsh.exe", null, null, false, false),
                WslSource => new Program("wsl.exe", null, profile.Name, false, false),
                GitSource => new Program("bash.exe", null, null, true, false),
                _ => null,
            };
        }

        /// <summary>
        /// The same program. A path on both sides must be the same path (PowerShell 7 is not its
        /// preview); bash is Git's or WSL's (TRAP-88), never both; a WSL shell is its distribution.
        /// </summary>
        public bool Runs(Program shell)
        {
            if (!string.Equals(FileName, shell.FileName, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Distribution, shell.Distribution, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (FullPath is not null && shell.FullPath is not null)
            {
                return string.Equals(FullPath, shell.FullPath, StringComparison.OrdinalIgnoreCase);
            }

            return FileName != "bash.exe" || IsGitBash && shell.IsGitBash;
        }

        private static Program? Read(string command, IReadOnlyList<string> arguments)
        {
            var program = Environment.ExpandEnvironmentVariables(command.Trim().Trim('"'));
            if (program.Length == 0)
            {
                return null;
            }

            var fileName = Path.GetFileName(program).ToLowerInvariant();
            if (Path.GetExtension(fileName).Length == 0)
            {
                fileName += ".exe";
            }

            string? fullPath = null;
            if (Path.IsPathFullyQualified(program))
            {
                try
                {
                    fullPath = Path.GetFullPath(program);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    return null;
                }
            }

            var own = arguments.ToList();
            string? distribution = null;
            if (fileName == "wsl.exe")
            {
                var at = own.FindIndex(a => a is "-d" or "--distribution");
                if (at >= 0 && at + 1 < own.Count)
                {
                    distribution = own[at + 1];
                    own.RemoveRange(at, 2);
                }
            }

            var isGitBash = fileName == "bash.exe"
                && fullPath is not null
                && fullPath.Contains(@"\Git\", StringComparison.OrdinalIgnoreCase);
            return new Program(fileName, fullPath, distribution, isGitBash, own.Count > 0);
        }
    }
}

/// <summary>One profile of Windows Terminal's <c>settings.json</c>, as far as matching a shell needs it.</summary>
/// <param name="Source">Who generated it (<c>Windows.Terminal.Wsl</c>, <c>Git</c>…); null for one written by hand.</param>
/// <param name="Commandline">Null for a generated profile: what it runs is not in the file.</param>
public sealed record WindowsTerminalProfile(string Name, string? Guid, string? Source, string? Commandline, bool Hidden);
