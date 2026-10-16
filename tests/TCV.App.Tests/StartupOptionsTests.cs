using System.IO;
using FluentAssertions;
using TCV.App.Settings;

namespace TCV.App.Tests;

public class StartupOptionsTests
{
    [Fact]
    public void NoArgs_IsAPrimaryWindow()
    {
        var opts = StartupOptions.Parse(Array.Empty<string>());

        opts.WorkspaceName.Should().BeNull();
        opts.IsSecondaryWindow.Should().BeFalse();
    }

    [Fact]
    public void NullArgs_DoesNotThrow()
    {
        StartupOptions.Parse(null).IsSecondaryWindow.Should().BeFalse();
    }

    [Theory]
    [InlineData("--workspace")]
    [InlineData("--WORKSPACE")]
    [InlineData("-w")]
    public void WorkspaceFlag_TakesTheNextArgument(string flag)
    {
        var opts = StartupOptions.Parse(new[] { flag, "My Project" });

        opts.WorkspaceName.Should().Be("My Project");
        opts.IsSecondaryWindow.Should().BeTrue();
    }

    [Fact]
    public void InlineForm_IsAccepted()
    {
        StartupOptions.Parse(new[] { "--workspace=api" }).WorkspaceName.Should().Be("api");
    }

    [Fact]
    public void InlineForm_StripsSurroundingQuotes()
    {
        StartupOptions.Parse(new[] { "--workspace=\"My Project\"" })
            .WorkspaceName.Should().Be("My Project");
    }

    [Fact]
    public void DanglingFlag_IsIgnored_RatherThanCrashing()
    {
        // "--workspace" with nothing after it must still start the app normally.
        StartupOptions.Parse(new[] { "--workspace" }).IsSecondaryWindow.Should().BeFalse();
    }

    [Fact]
    public void FlagFollowedByAnotherFlag_IsATypo_NotAWorkspaceName()
    {
        StartupOptions.Parse(new[] { "--workspace", "--verbose" })
            .WorkspaceName.Should().BeNull();
    }

    [Fact]
    public void EmptyInlineValue_IsIgnored()
    {
        StartupOptions.Parse(new[] { "--workspace=" }).IsSecondaryWindow.Should().BeFalse();
    }

    [Fact]
    public void UnrelatedArgs_AreIgnored()
    {
        // The shell can hand a file manager anything; unknown arguments must not stop startup.
        StartupOptions.Parse(new[] { @"C:\some\file.txt", "/n" }).IsSecondaryWindow.Should().BeFalse();
    }

    [Fact]
    public void WorkspaceName_IsTrimmed()
    {
        StartupOptions.Parse(new[] { "--workspace", "  api  " }).WorkspaceName.Should().Be("api");
    }

    [Fact]
    public void FlagAfterOtherArgs_IsStillFound()
    {
        StartupOptions.Parse(new[] { "--theme", "dark", "--workspace", "api" })
            .WorkspaceName.Should().Be("api");
    }

    [Fact]
    public void NoDataFlag_LeavesTheFolderToTheLayout()
    {
        StartupOptions.Parse(new[] { "--workspace", "api" }).DataDirectory.Should().BeNull();
    }

    [Theory]
    [InlineData("--data", @"D:\Portable Programs\tcv\data")]
    [InlineData("--DATA", @"D:\Portable Programs\tcv\data\")]
    public void DataFlag_TakesTheNextArgumentAsAFullPath(string flag, string folder)
    {
        StartupOptions.Parse(new[] { flag, folder }).DataDirectory
            .Should().Be(@"D:\Portable Programs\tcv\data");
    }

    [Fact]
    public void DataInlineForm_StripsSurroundingQuotes()
    {
        StartupOptions.Parse(new[] { "--data=\"D:\\tcv data\"" }).DataDirectory.Should().Be(@"D:\tcv data");
    }

    [Fact]
    public void RelativeDataFolder_IsTakenFromTheWorkingDirectory()
    {
        StartupOptions.Parse(new[] { "--data", "sandbox" }).DataDirectory
            .Should().Be(Path.Combine(Environment.CurrentDirectory, "sandbox"));
    }

    [Fact]
    public void DataAndWorkspace_AreBothRead()
    {
        var opts = StartupOptions.Parse(new[] { "--data", @"D:\tcv", "--workspace", "api" });

        opts.DataDirectory.Should().Be(@"D:\tcv");
        opts.WorkspaceName.Should().Be("api");
    }

    [Fact]
    public void DanglingWorkspaceFlag_DoesNotSwallowTheDataFlag()
    {
        var opts = StartupOptions.Parse(new[] { "--workspace", "--data", @"D:\tcv" });

        opts.WorkspaceName.Should().BeNull();
        opts.DataDirectory.Should().Be(@"D:\tcv");
    }

    [Fact]
    public void DataFlagWithoutValue_IsIgnored()
    {
        StartupOptions.Parse(new[] { "--data", "--workspace", "api" }).DataDirectory.Should().BeNull();
    }

    [Fact]
    public void GuestArguments_CarryTheDataFolder_OnlyWhenOneWasGiven()
    {
        StartupOptions.GuestArguments("api", null).Should().Be("--workspace=\"api\"");
        StartupOptions.GuestArguments("My Project", @"D:\tcv data")
            .Should().Be("--data=\"D:\\tcv data\" --workspace=\"My Project\"");
    }

    [Fact]
    public void DataFlagFollowedByTheWorkspaceShortFlag_DoesNotTakeItAsAFolder()
    {
        var opts = StartupOptions.Parse(new[] { "--data", "-w", "api" });

        opts.DataDirectory.Should().BeNull();
        opts.WorkspaceName.Should().Be("api");
    }

    [Theory]
    [InlineData("My Project", @"D:\tcv data")]
    [InlineData("-dash", @"D:\")]
    [InlineData("api", @"\\server\share\tcv")]
    public void GuestArguments_ParseBackToTheSameOptions(string workspace, string data)
    {
        // Split as the guest process will see it: CommandLineToArgvW, the rules ShellQuoting targets.
        var opts = StartupOptions.Parse(SplitCommandLine(StartupOptions.GuestArguments(workspace, data)));

        opts.DataDirectory.Should().Be(data);
        opts.WorkspaceName.Should().Be(workspace);
    }

    private static string[] SplitCommandLine(string arguments)
    {
        // A program name first, as the real command line has: its first token is parsed by other rules.
        var argv = CommandLineToArgvW("app.exe " + arguments, out var count);
        try
        {
            return Enumerable.Range(1, count - 1)
                .Select(i => System.Runtime.InteropServices.Marshal.PtrToStringUni(
                    System.Runtime.InteropServices.Marshal.ReadIntPtr(argv, i * IntPtr.Size))!)
                .ToArray();
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
