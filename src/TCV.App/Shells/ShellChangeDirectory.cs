using System.IO;
using System.Text;
using TCV.App.ViewModels;

namespace TCV.App.Shells;

/// <summary>Whose rules a <c>cd</c> typed in the command line follows.</summary>
public enum ShellDialect
{
    /// <summary>cmd, and every shell not recognised: the rules the command line has always read <c>cd</c> by.</summary>
    Windows,

    PowerShell,

    /// <summary>Git's bash: <c>/c/Users</c>, and a home folder Windows can see.</summary>
    GitBash,

    /// <summary>A WSL distribution: <c>/mnt/c/Users</c>, and everything else inside Linux.</summary>
    Wsl,
}

/// <summary>What a line typed in the command line asks of the panel.</summary>
public enum ChangeDirectoryKind
{
    /// <summary>Not a move: the line runs in the shell.</summary>
    NotAMove,

    /// <summary>A move to <see cref="ChangeDirectory.Path"/>: absolute, or relative to the panel's folder.</summary>
    Move,

    /// <summary>A move to <see cref="ChangeDirectory.Path"/>, as typed, which exists only inside the shell.</summary>
    NotOnPanel,
}

/// <summary>The answer of <see cref="ShellChangeDirectory.Read"/>.</summary>
public readonly record struct ChangeDirectory(ChangeDirectoryKind Kind, string Path = "")
{
    public static ChangeDirectory NotAMove => new(ChangeDirectoryKind.NotAMove);

    public static ChangeDirectory Move(string path) => new(ChangeDirectoryKind.Move, path);

    public static ChangeDirectory NotOnPanel(string path) => new(ChangeDirectoryKind.NotOnPanel, path);
}

/// <summary>
/// Reads a typed <c>cd</c> by the rules of the shell it was typed for, and says where on the panel that
/// is. Pure: the program and the home folder come from the caller.
/// </summary>
/// <remarks>
/// The panel shows Windows folders, so a shell's path either has a Windows folder behind it or is said
/// to have none: going to some other folder silently would be worse than not going. Git Bash's <c>/</c>
/// is Git's install folder, as <c>mount</c> says. Its other paths (<c>/usr/bin</c>, <c>/tmp</c>) are not
/// read as folders under it: the mount table sends <c>/bin</c> and <c>/tmp</c> elsewhere. WSL's home is
/// found only by asking the distribution, which would start it for a <c>cd</c>.
/// A line bash would do more with than move to one place (a second word, a chain, a redirect, an
/// expansion, a glob, an option) is not a move: bash runs it and answers for itself.
/// </remarks>
public static class ShellChangeDirectory
{
    // What ends or expands an unquoted word in bash. '#' only starts a comment at the start of a word.
    private const string BashSpecial = "$`*?[{()};&|<>";

    /// <summary>
    /// Whose rules <paramref name="program"/> follows. By the program, not the row's name: a row called
    /// «Git Bash» can run anything. A shell outside the catalog (MSYS2, Cygwin, a bare <c>bash</c> that
    /// PATH may give to anyone) stays <see cref="ShellDialect.Windows"/>, as it has always been.
    /// </summary>
    public static ShellDialect DialectOf(string program)
    {
        var path = Clean(program);
        if (ShellCommandLine.IsCmd(path))
        {
            return ShellDialect.Windows;
        }

        var name = Path.GetFileNameWithoutExtension(path);
        if (name.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
            || name.Equals("powershell", StringComparison.OrdinalIgnoreCase))
        {
            return ShellDialect.PowerShell;
        }

        // System32\bash.exe is WSL's (TRAP-88).
        if (name.Equals("wsl", StringComparison.OrdinalIgnoreCase)
            || (name.Equals("bash", StringComparison.OrdinalIgnoreCase)
                && FolderName(Path.GetDirectoryName(path)).Equals("System32", StringComparison.OrdinalIgnoreCase)))
        {
            return ShellDialect.Wsl;
        }

        return GitRootOf(path) is null ? ShellDialect.Windows : ShellDialect.GitBash;
    }

    /// <summary>The line by cmd's rules: for a shell without rules of its own, and for a panel in an
    /// archive or on a server, whose paths are its own and not the shell's.</summary>
    public static ChangeDirectory ReadAsCmd(string text) =>
        CommandLine.TryParseChangeDirectory(text, out var target) ? ChangeDirectory.Move(target) : ChangeDirectory.NotAMove;

    /// <summary>The line by the rules of <paramref name="program"/>, the shell's program with environment variables expanded.</summary>
    /// <param name="userProfile">The Windows user's folder: <c>~</c> in PowerShell and, without a
    /// <c>HOME</c>, in Git Bash.</param>
    /// <param name="homeVariable">The <c>HOME</c> environment variable, which Git Bash prefers.</param>
    public static ChangeDirectory Read(string text, string program, string userProfile, string? homeVariable)
    {
        switch (DialectOf(program))
        {
            case ShellDialect.PowerShell:
                return ReadPowerShell(text, userProfile);
            case ShellDialect.GitBash:
                return ReadBash(text, ShellDialect.GitBash, GitRootOf(Clean(program)), GitBashHome(userProfile, homeVariable));
            case ShellDialect.Wsl:
                return ReadBash(text, ShellDialect.Wsl, gitRoot: null, home: null);
            default:
                return ReadAsCmd(text);
        }
    }

    // cmd's reading, plus the '~' PowerShell's file system understands, quoted or not.
    private static ChangeDirectory ReadPowerShell(string text, string userProfile)
    {
        if (!CommandLine.TryParseChangeDirectory(text, out var target))
        {
            return ChangeDirectory.NotAMove;
        }

        return IsHome(target) ? ChangeDirectory.Move(UnderHome(userProfile, target)) : ChangeDirectory.Move(target);
    }

    private static ChangeDirectory ReadBash(string text, ShellDialect dialect, string? gitRoot, string? home)
    {
        var line = text.Trim();
        if (CommandLine.IsBareDrive(line))
        {
            return ChangeDirectory.Move(line + "\\");
        }

        if (!CommandLine.TryReadVerb(line, out var rest))
        {
            return ChangeDirectory.NotAMove;
        }

        // A plain "cd" goes home in bash.
        var argument = rest.Trim();
        if (argument.Length == 0)
        {
            return home is null ? ChangeDirectory.NotOnPanel("~") : ChangeDirectory.Move(home);
        }

        // "cd -" and the options are bash's.
        if (argument[0] == '-' || !TryReadBashWord(argument, out var word))
        {
            return ChangeDirectory.NotAMove;
        }

        // Only an unquoted '~' is the home folder: "~" is a folder of that name.
        if (argument[0] == '~')
        {
            if (!IsHome(word))
            {
                return ChangeDirectory.NotAMove;   // ~user, ~+: bash knows, the panel does not
            }

            return home is null ? ChangeDirectory.NotOnPanel(word) : ChangeDirectory.Move(UnderHome(home, word));
        }

        // Relative to the panel's folder, or a Windows path typed out of habit: it can mean nothing else.
        if (!word.StartsWith('/'))
        {
            return ChangeDirectory.Move(word);
        }

        return dialect == ShellDialect.GitBash ? FromGitBashPath(word, gitRoot) : FromWslPath(word);
    }

    // "/c/Users" and "/C/Users" alike (Windows answers both), "//server/share", "/" is Git's own folder.
    private static ChangeDirectory FromGitBashPath(string path, string? gitRoot)
    {
        if (path.StartsWith("//", StringComparison.Ordinal) && !path.StartsWith("///", StringComparison.Ordinal))
        {
            return ChangeDirectory.Move(path);
        }

        var parts = PosixSegments(path);
        if (parts.Count == 0)
        {
            return gitRoot is null ? ChangeDirectory.NotOnPanel(path) : ChangeDirectory.Move(gitRoot);
        }

        return parts[0] is [var letter] && char.IsAsciiLetter(letter)
            ? ChangeDirectory.Move(OnDrive(letter, parts.Skip(1)))
            : ChangeDirectory.NotOnPanel(path);
    }

    // "/mnt/c/Users" only, in small letters: Linux tells cases apart, and /mnt/wsl is no drive.
    private static ChangeDirectory FromWslPath(string path)
    {
        var parts = PosixSegments(path);
        return parts is ["mnt", [var letter], ..] && char.IsAsciiLetterLower(letter)
            ? ChangeDirectory.Move(OnDrive(letter, parts.Skip(2)))
            : ChangeDirectory.NotOnPanel(path);
    }

    private static string OnDrive(char letter, IEnumerable<string> folders) =>
        char.ToUpperInvariant(letter) + @":\" + string.Join('\\', folders);

    // A POSIX path walks up from where it is, not from the Windows path it may become:
    // /mnt/c/../../etc is /etc, not C:\etc.
    private static List<string> PosixSegments(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
            }
            else if (part != ".")
            {
                parts.Add(part);
            }
        }

        return parts;
    }

    private static string GitBashHome(string userProfile, string? homeVariable)
    {
        if (string.IsNullOrWhiteSpace(homeVariable))
        {
            return userProfile;
        }

        // HOME may be written either way: C:\Users\me or /c/Users/me.
        return homeVariable.StartsWith('/') && FromGitBashPath(homeVariable, gitRoot: null) is { Kind: ChangeDirectoryKind.Move } home
            ? home.Path
            : homeVariable;
    }

    private static bool IsHome(string target) =>
        target == "~" || target.StartsWith("~/", StringComparison.Ordinal) || target.StartsWith(@"~\", StringComparison.Ordinal);

    private static string UnderHome(string home, string target)
    {
        var below = target[1..].TrimStart('/', '\\');
        return below.Length == 0 ? home : Path.Join(home, below.Replace('/', '\\'));
    }

    /// <summary>
    /// The one word <paramref name="text"/> is, as bash reads it, or false when bash would do more with
    /// it than take it as a path. A backslash that escapes nothing in bash stays: <c>C:\Users</c> and
    /// <c>\\server\share</c> are typed by habit, and a Windows path can mean nothing else here.
    /// </summary>
    private static bool TryReadBashWord(string text, out string word)
    {
        word = string.Empty;
        if (text[0] == '#')
        {
            return false;
        }

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\'')
            {
                var close = text.IndexOf('\'', i + 1);
                if (close < 0)
                {
                    return false;
                }

                sb.Append(text, i + 1, close - i - 1);
                i = close;
            }
            else if (c == '"')
            {
                if (!TryReadDoubleQuoted(text, ref i, sb))
                {
                    return false;
                }
            }
            else if (c == '\\')
            {
                if (i + 1 < text.Length && IsEscapedInWord(text[i + 1]))
                {
                    i++;
                }

                sb.Append(text[i]);
            }
            else if (char.IsWhiteSpace(c) || BashSpecial.Contains(c))
            {
                return false;
            }
            else
            {
                sb.Append(c);
            }
        }

        word = sb.ToString();
        return word.Length > 0;
    }

    // i stands on the opening quote and ends on the closing one.
    private static bool TryReadDoubleQuoted(string text, ref int i, StringBuilder sb)
    {
        for (i++; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                return true;
            }

            if (c is '$' or '`')
            {
                return false;
            }

            if (c == '\\' && i + 1 < text.Length && text[i + 1] is '"' or '$' or '`')
            {
                i++;
                c = text[i];
            }

            sb.Append(c);
        }

        return false;
    }

    private static bool IsEscapedInWord(char c) =>
        char.IsWhiteSpace(c) || BashSpecial.Contains(c) || c is '\'' or '"' or '#' or '~';

    /// <summary>
    /// The folder Git for Windows is installed in, which is bash's <c>/</c>: <c>…\Git\bin\bash.exe</c>,
    /// <c>…\Git\usr\bin\bash.exe</c>, <c>…\Git\git-bash.exe</c>, the same in PortableGit and scoop's
    /// <c>…\git\current</c>. Null for any other bash: MSYS2 and Cygwin have roots and homes of their own.
    /// </summary>
    private static string? GitRootOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var folder = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(folder))
        {
            return null;
        }

        if (name.Equals("git-bash", StringComparison.OrdinalIgnoreCase))
        {
            return folder;
        }

        if (!(name.Equals("bash", StringComparison.OrdinalIgnoreCase) || name.Equals("sh", StringComparison.OrdinalIgnoreCase))
            || !FolderName(folder).Equals("bin", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var root = Path.GetDirectoryName(folder);
        if (FolderName(root).Equals("usr", StringComparison.OrdinalIgnoreCase))
        {
            root = Path.GetDirectoryName(root);
        }

        return !string.IsNullOrEmpty(root) && IsGitFolder(root) ? root : null;
    }

    private static bool IsGitFolder(string folder)
    {
        var name = FolderName(folder);
        return name.Equals("Git", StringComparison.OrdinalIgnoreCase)
            || name.Equals("PortableGit", StringComparison.OrdinalIgnoreCase)
            || (name.Equals("current", StringComparison.OrdinalIgnoreCase)
                && FolderName(Path.GetDirectoryName(folder)).Equals("git", StringComparison.OrdinalIgnoreCase));
    }

    private static string FolderName(string? folder) => Path.GetFileName(folder) ?? string.Empty;

    private static string Clean(string program) => program.Trim().Trim('"').Replace('/', '\\');
}
