using TCV.App.Settings;
using TCV.Core.Domain;

namespace TCV.App.Shells;

/// <summary>
/// The shells the command line finds by itself, in the order its picker offers them: what to run, how
/// to hand it a command so the window stays open afterwards, and where it lives when it is not on
/// <c>PATH</c>. Data only: probing the disk is <see cref="Services.ShellService"/>'s, building the
/// command line is <see cref="ShellCommandLine"/>'s.
/// </summary>
/// <remarks>
/// The same split as the terminal profiles of VS Code and Windows Terminal: the common shells and the
/// WSL distributions are detected, anything else (MSYS2, Cygwin, Nushell, a developer prompt) is a row
/// the user adds in Settings → Shells. cmd comes first: the command line ran everything in cmd before it
/// had a choice.
/// </remarks>
public static class ShellCatalog
{
    public static IReadOnlyList<ShellTemplate> All { get; } =
    [
        new("cmd", "cmd.exe", "/s /k %C", []),
        new("PowerShell 7", "pwsh.exe", "-NoLogo -NoExit -Command %C",
            [@"%ProgramFiles%\PowerShell\7\pwsh.exe"]),
        new("Windows PowerShell", "powershell.exe", "-NoLogo -NoExit -Command %C",
            [@"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"]),
        // Never looked up on PATH: a bare "bash" there is WSL's System32\bash.exe, not Git's (TRAP-88).
        new("Git Bash", string.Empty, "--login -i -c \"%C; exec bash --login -i\"",
            [@"%ProgramFiles%\Git\bin\bash.exe",
             @"%ProgramFiles(x86)%\Git\bin\bash.exe",
             @"%LOCALAPPDATA%\Programs\Git\bin\bash.exe",
             @"%USERPROFILE%\scoop\apps\git\current\bin\bash.exe"]),
    ];

    /// <summary>
    /// WSL as installed by <c>wsl --install</c>. Not System32\wsl.exe: that one ships with every Windows
    /// 11, WSL installed or not. Each distribution is a shell of its own (<see cref="ForWslDistribution"/>).
    /// </summary>
    public const string WslProgram = @"%ProgramFiles%\WSL\wsl.exe";

    /// <summary>
    /// The distribution as a shell: it starts in the Linux view of the working directory, runs the
    /// command in bash and stays in bash.
    /// </summary>
    public static ShellProfile ForWslDistribution(string program, string distribution) => new()
    {
        Name = distribution + " (WSL)",
        Command = program,
        Arguments = $"-d {ShellQuoting.QuoteIfNeeded(distribution)} -e bash -lic \"%C; exec bash -li\"",
    };

    /// <summary>
    /// The distributions in the output of <c>wsl --list --quiet</c>, as VS Code reads them: Docker
    /// Desktop's own are left out — they have no bash and are no place to work in, and on many machines
    /// they are the only ones there.
    /// </summary>
    public static IReadOnlyList<string> WslDistributions(string listOutput) =>
        listOutput.Replace("\0", string.Empty).Replace("﻿", string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => !name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>cmd: always there, and what the command line runs in when nothing else is offered.</summary>
    public static ShellProfile Fallback => All[0].ToSettings(All[0].Command);

    /// <summary>
    /// The shell named <paramref name="name"/> among <paramref name="shells"/>, else the first of them,
    /// else <see cref="Fallback"/>: a shell removed from the settings or uninstalled must not leave the
    /// command line with nothing to run in.
    /// </summary>
    public static ShellProfile Pick(IReadOnlyList<ShellProfile> shells, string? name) =>
        shells.FirstOrDefault(s => string.Equals(s.Name.Trim(), name?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? shells.FirstOrDefault()
        ?? Fallback;
}

/// <summary>A known shell.</summary>
/// <param name="Command">Looked up on <c>PATH</c>; empty for a shell that is only found by its
/// <paramref name="ProbePaths"/>.</param>
/// <param name="ProbePaths">Install locations checked when the command is not on <c>PATH</c>, with
/// environment variables in them. The one found is stored as written, so a portable copy carried to
/// another machine still finds it there.</param>
public sealed record ShellTemplate(string Name, string Command, string Arguments, IReadOnlyList<string> ProbePaths)
{
    /// <summary>The settings shape, running <paramref name="command"/> (as found on this machine).</summary>
    public ShellProfile ToSettings(string command) => new()
    {
        Name = Name,
        Command = command,
        Arguments = Arguments,
    };
}
