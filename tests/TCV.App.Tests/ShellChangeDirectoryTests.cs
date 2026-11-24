using FluentAssertions;
using TCV.App.Shells;

namespace TCV.App.Tests;

/// <summary>
/// A typed <c>cd</c> is read by the rules of the shell it was typed for, and a path with no folder on
/// the panel is said to have none rather than opening some other folder.
/// </summary>
public class ShellChangeDirectoryTests
{
    private const string Cmd = @"C:\Windows\System32\cmd.exe";
    private const string Pwsh = @"C:\Program Files\PowerShell\7\pwsh.exe";
    private const string GitBash = @"C:\Program Files\Git\bin\bash.exe";
    private const string Wsl = @"C:\Program Files\WSL\wsl.exe";
    private const string Profile = @"C:\Users\me";

    // ── Whose rules ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("cmd.exe", ShellDialect.Windows)]
    [InlineData(Cmd, ShellDialect.Windows)]
    [InlineData(@"C:\tools\vcvars.cmd", ShellDialect.Windows)]
    [InlineData("pwsh.exe", ShellDialect.PowerShell)]
    [InlineData("pwsh", ShellDialect.PowerShell)]
    [InlineData(Pwsh, ShellDialect.PowerShell)]
    [InlineData("powershell.exe", ShellDialect.PowerShell)]
    [InlineData(@"C:\WINDOWS\System32\WindowsPowerShell\v1.0\powershell.exe", ShellDialect.PowerShell)]
    [InlineData(GitBash, ShellDialect.GitBash)]
    [InlineData("\"C:\\Program Files\\Git\\bin\\bash.exe\"", ShellDialect.GitBash)]
    [InlineData(@"C:\Program Files\Git\usr\bin\bash.exe", ShellDialect.GitBash)]
    [InlineData(@"C:\Program Files\Git\bin\sh.exe", ShellDialect.GitBash)]
    [InlineData(@"C:\Program Files\Git\git-bash.exe", ShellDialect.GitBash)]
    [InlineData(@"D:\tools\PortableGit\bin\bash.exe", ShellDialect.GitBash)]
    [InlineData(@"C:\Users\me\scoop\apps\git\current\bin\bash.exe", ShellDialect.GitBash)]
    [InlineData("wsl.exe", ShellDialect.Wsl)]
    [InlineData(Wsl, ShellDialect.Wsl)]
    [InlineData(@"C:\Windows\System32\bash.exe", ShellDialect.Wsl)]
    public void DialectOf_KnownPrograms(string program, ShellDialect expected)
    {
        ShellChangeDirectory.DialectOf(program).Should().Be(expected);
    }

    [Theory]
    [InlineData(@"C:\msys64\usr\bin\bash.exe")]
    [InlineData(@"C:\cygwin64\bin\bash.exe")]
    [InlineData("bash.exe")]
    [InlineData("nu.exe")]
    [InlineData("python.exe")]
    [InlineData("")]
    public void DialectOf_AShellOutsideTheCatalog_LivesAsBefore(string program)
    {
        ShellChangeDirectory.DialectOf(program).Should().Be(ShellDialect.Windows);
    }

    [Fact]
    public void DialectOf_EveryCatalogShell_IsKnown()
    {
        ShellDialect[] expected = [ShellDialect.Windows, ShellDialect.PowerShell, ShellDialect.PowerShell, ShellDialect.GitBash];

        ShellCatalog.All
            .Select(s => ShellChangeDirectory.DialectOf(s.Command.Length > 0 ? s.Command : s.ProbePaths[0]))
            .Should().Equal(expected);
        foreach (var path in ShellCatalog.All.Single(s => s.Name == "Git Bash").ProbePaths)
        {
            ShellChangeDirectory.DialectOf(path).Should().Be(ShellDialect.GitBash, path);
        }

        ShellChangeDirectory.DialectOf(ShellCatalog.WslProgram).Should().Be(ShellDialect.Wsl);
    }

    // ── cmd and every unknown shell ───────────────────────────────────────────

    [Theory]
    [InlineData(@"cd /d D:\x", @"D:\x")]
    [InlineData("cd ..", "..")]
    [InlineData("cd..", "..")]
    [InlineData("cd src/app", "src/app")]
    [InlineData("chdir \"My Folder\"", "My Folder")]
    [InlineData("D:", @"D:\")]
    [InlineData("cd /", "/")]
    [InlineData("cd ~", "~")]
    [InlineData("cd /c/Users", "/c/Users")]
    public void Read_Cmd_MovesAsItAlwaysHas(string text, string expected)
    {
        Read(text, Cmd).Should().Be(ChangeDirectory.Move(expected));
    }

    [Theory]
    [InlineData("cd")]
    [InlineData("dir")]
    [InlineData("cd src && npm test")]
    public void Read_Cmd_EverythingElseRunsInTheShell(string text)
    {
        Read(text, Cmd).Should().Be(ChangeDirectory.NotAMove);
    }

    [Theory]
    [InlineData("cd ~", "~")]
    [InlineData("cd /c/Users", "/c/Users")]
    [InlineData("cd /mnt/c/Users", "/mnt/c/Users")]
    public void Read_AnUnknownShell_ReadsLikeCmd(string text, string expected)
    {
        Read(text, @"C:\msys64\usr\bin\bash.exe").Should().Be(ChangeDirectory.Move(expected));
    }

    [Theory]
    [InlineData("cd /var/log", "/var/log")]
    [InlineData("cd ~", "~")]
    [InlineData("cd /c/Users", "/c/Users")]
    public void ReadAsCmd_APanelWithPathsOfItsOwn_NothingIsTranslated(string text, string expected)
    {
        ShellChangeDirectory.ReadAsCmd(text).Should().Be(ChangeDirectory.Move(expected));
    }

    [Fact]
    public void ReadAsCmd_APlainCd_IsNotAMove()
    {
        ShellChangeDirectory.ReadAsCmd("cd").Should().Be(ChangeDirectory.NotAMove);
    }

    // ── PowerShell ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"cd /d D:\x", @"D:\x")]
    [InlineData(@"cd D:\x", @"D:\x")]
    [InlineData("cd ..", "..")]
    [InlineData("cd src/app", "src/app")]
    [InlineData("D:", @"D:\")]
    [InlineData("cd ~", Profile)]
    [InlineData("cd \"~\"", Profile)]
    [InlineData("cd ~/src", @"C:\Users\me\src")]
    [InlineData(@"cd ~\src\app", @"C:\Users\me\src\app")]
    [InlineData("cd ~x", "~x")]
    public void Read_PowerShell_IsCmdsReadingPlusTheHomeFolder(string text, string expected)
    {
        Read(text, Pwsh).Should().Be(ChangeDirectory.Move(expected));
    }

    [Theory]
    [InlineData("cd")]
    [InlineData("Get-ChildItem")]
    [InlineData("cd src | Out-Null")]
    public void Read_PowerShell_EverythingElseRunsInTheShell(string text)
    {
        Read(text, Pwsh).Should().Be(ChangeDirectory.NotAMove);
    }

    // ── Git Bash ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("cd", Profile)]
    [InlineData("cd ~", Profile)]
    [InlineData("cd ~/", Profile)]
    [InlineData("cd ~/src/app", @"C:\Users\me\src\app")]
    [InlineData("cd /c/Users", @"C:\Users")]
    [InlineData("cd /C/Users", @"C:\Users")]
    [InlineData("cd /c", @"C:\")]
    [InlineData("cd /c/", @"C:\")]
    [InlineData("cd /d", @"D:\")]
    [InlineData("cd /d/work/src", @"D:\work\src")]
    [InlineData("cd /c/Users/me/../you/./x", @"C:\Users\you\x")]
    [InlineData("cd /", @"C:\Program Files\Git")]
    [InlineData("cd //server/share", "//server/share")]
    [InlineData("cd src/app", "src/app")]
    [InlineData("cd ..", "..")]
    [InlineData("cd..", "..")]
    [InlineData("cd ../x", "../x")]
    [InlineData("D:", @"D:\")]
    [InlineData("cd C:/Users", "C:/Users")]
    [InlineData(@"cd C:\Users", @"C:\Users")]
    [InlineData(@"cd \\server\share", @"\\server\share")]
    public void Read_GitBash_TheFolderBehindThePath(string text, string expected)
    {
        Read(text, GitBash).Should().Be(ChangeDirectory.Move(expected));
    }

    [Theory]
    [InlineData("cd \"My Folder\"", "My Folder")]
    [InlineData("cd 'My Folder'", "My Folder")]
    [InlineData(@"cd My\ Folder", "My Folder")]
    [InlineData("cd '/c/Program Files'", @"C:\Program Files")]
    [InlineData("cd \"/c/Program Files\"", @"C:\Program Files")]
    [InlineData(@"cd /c/Program\ Files", @"C:\Program Files")]
    [InlineData(@"cd /c/Program\ Files\ \(x86\)", @"C:\Program Files (x86)")]
    [InlineData("cd 'it''s'", "its")]
    [InlineData("cd \"~\"", "~")]
    [InlineData("cd '~/x'", "~/x")]
    [InlineData("cd C#", "C#")]
    [InlineData("cd \"a\\\"b\"", "a\"b")]
    public void Read_GitBash_QuotesAreRead(string text, string expected)
    {
        Read(text, GitBash).Should().Be(ChangeDirectory.Move(expected));
    }

    [Theory]
    [InlineData("/usr/bin")]
    [InlineData("/bin")]
    [InlineData("/tmp")]
    [InlineData("/etc/profile.d")]
    [InlineData("/cygdrive/c/Users")]
    [InlineData("/c/../usr")]
    public void Read_GitBash_APathInsideBash_IsSaidToHaveNoFolder(string path)
    {
        Read("cd " + path, GitBash).Should().Be(ChangeDirectory.NotOnPanel(path));
    }

    [Theory]
    [InlineData("ls -la")]
    [InlineData("cdk deploy")]
    [InlineData("cd src && npm test")]
    [InlineData("cd src; ls")]
    [InlineData("cd src | cat")]
    [InlineData("cd src > out")]
    [InlineData("cd src &")]
    [InlineData("cd src # why")]
    [InlineData("cd # why")]
    [InlineData("cd a b")]
    [InlineData(@"cd /d D:\x")]
    [InlineData("cd -")]
    [InlineData("cd -P /c/Users")]
    [InlineData("cd $HOME")]
    [InlineData("cd \"$HOME/x\"")]
    [InlineData("cd `git rev-parse --show-toplevel`")]
    [InlineData("cd $(git rev-parse --show-toplevel)")]
    [InlineData("cd sr*")]
    [InlineData("cd s?c")]
    [InlineData("cd [ab]")]
    [InlineData("cd {a,b}")]
    [InlineData("cd ~root")]
    [InlineData("cd ~+")]
    [InlineData("cd 'unterminated")]
    [InlineData("cd \"unterminated")]
    [InlineData("cd \"\"")]
    public void Read_GitBash_WhatBashWouldDoMoreWith_RunsInBash(string text)
    {
        Read(text, GitBash).Should().Be(ChangeDirectory.NotAMove);
    }

    [Theory]
    [InlineData(@"D:\home\me", @"D:\home\me")]
    [InlineData("/d/home/me", @"D:\home\me")]
    [InlineData("", Profile)]
    [InlineData("  ", Profile)]
    [InlineData(null, Profile)]
    public void Read_GitBash_HomeIsTheHomeVariable_ThenTheProfile(string? variable, string expected)
    {
        ShellChangeDirectory.Read("cd ~", GitBash, Profile, variable).Should().Be(ChangeDirectory.Move(expected));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Git\usr\bin\bash.exe", @"C:\Program Files\Git")]
    [InlineData(@"D:\tools\PortableGit\bin\bash.exe", @"D:\tools\PortableGit")]
    [InlineData(@"C:\Users\me\scoop\apps\git\current\bin\bash.exe", @"C:\Users\me\scoop\apps\git\current")]
    [InlineData(@"C:\Program Files\Git\git-bash.exe", @"C:\Program Files\Git")]
    public void Read_GitBash_RootIsTheFolderItIsInstalledIn(string program, string expected)
    {
        Read("cd /", program).Should().Be(ChangeDirectory.Move(expected));
    }

    // ── WSL ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("cd /mnt/c/Users", @"C:\Users")]
    [InlineData("cd /mnt/d", @"D:\")]
    [InlineData("cd /mnt/d/", @"D:\")]
    [InlineData("cd '/mnt/c/Program Files'", @"C:\Program Files")]
    [InlineData("cd /mnt/c/Users/../Windows", @"C:\Windows")]
    [InlineData("cd src/app", "src/app")]
    [InlineData("cd ..", "..")]
    [InlineData("D:", @"D:\")]
    [InlineData(@"cd C:\Users", @"C:\Users")]
    public void Read_Wsl_TheFolderBehindThePath(string text, string expected)
    {
        Read(text, Wsl).Should().Be(ChangeDirectory.Move(expected));
    }

    [Theory]
    [InlineData("cd", "~")]
    [InlineData("cd ~", "~")]
    [InlineData("cd ~/src", "~/src")]
    [InlineData("cd /", "/")]
    [InlineData("cd /home/me", "/home/me")]
    [InlineData("cd /etc", "/etc")]
    [InlineData("cd /usr/bin", "/usr/bin")]
    [InlineData("cd /mnt", "/mnt")]
    [InlineData("cd /mnt/wsl", "/mnt/wsl")]
    [InlineData("cd /mnt/C/Users", "/mnt/C/Users")]
    [InlineData("cd /c/Users", "/c/Users")]
    [InlineData("cd /mnt/c/../../etc", "/mnt/c/../../etc")]
    [InlineData("cd //mnt/c/x/../..", "//mnt/c/x/../..")]
    public void Read_Wsl_APathInsideLinux_IsSaidToHaveNoFolder(string text, string expected)
    {
        Read(text, Wsl).Should().Be(ChangeDirectory.NotOnPanel(expected));
    }

    [Theory]
    [InlineData("ls -la")]
    [InlineData("cd src && ls")]
    [InlineData("cd $HOME")]
    [InlineData("cd ~root")]
    public void Read_Wsl_WhatBashWouldDoMoreWith_RunsInBash(string text)
    {
        Read(text, Wsl).Should().Be(ChangeDirectory.NotAMove);
    }

    // ── Up to the panel ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"C:\repo\src", "cd /c/Users", GitBash, @"C:\Users")]
    [InlineData(@"D:\repo\src", "cd ..", GitBash, @"D:\repo")]
    [InlineData(@"D:\repo", "cd src/app", GitBash, @"D:\repo\src\app")]
    [InlineData(@"D:\repo", "cd //server/share", GitBash, @"\\server\share")]
    [InlineData(@"D:\repo", "cd /mnt/c/Users", Wsl, @"C:\Users")]
    [InlineData(@"D:\repo", "cd ~/x", Pwsh, @"C:\Users\me\x")]
    public void ResolveAgainst_AMove_EndsAsAPathThePanelOpens(string folder, string text, string program, string expected)
    {
        ViewModels.CommandLine.ResolveAgainst(folder, Read(text, program).Path).Should().Be(expected);
    }

    private static ChangeDirectory Read(string text, string program) =>
        ShellChangeDirectory.Read(text, program, Profile, homeVariable: null);
}
