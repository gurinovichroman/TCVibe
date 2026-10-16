using FluentAssertions;
using TCV.App.FileSystem.Native;

namespace TCV.App.Tests;

/// <summary>Which shell menu verbs the manager runs itself, and which stay the shell's.</summary>
public class ShellMenuVerbsTests
{
    private static readonly ShellMenuScope OneFile = ShellMenuScope.Items(1, singleItemIsFolder: false);
    private static readonly ShellMenuScope OneFolder = ShellMenuScope.Items(1, singleItemIsFolder: true);
    private static readonly ShellMenuScope ThreeItems = ShellMenuScope.Items(3, singleItemIsFolder: false);

    [Theory]
    [InlineData("rename")]
    [InlineData("Rename")]
    public void Route_RenameOnOneItem_IsTheManagersRename(string verb)
    {
        ShellMenuVerbs.Route(verb, OneFile).Should().Be(ShellMenuAction.Rename);
        ShellMenuVerbs.Route(verb, OneFolder).Should().Be(ShellMenuAction.Rename);
    }

    [Fact]
    public void Route_RenameOnSeveralItems_OpensMultiRename()
    {
        ShellMenuVerbs.Route("rename", ThreeItems).Should().Be(ShellMenuAction.MultiRename);
    }

    [Fact]
    public void Route_Delete_GoesThroughTheManagerAndShiftSkipsTheBin()
    {
        ShellMenuVerbs.Route("delete", ThreeItems).Should().Be(ShellMenuAction.Delete);
        ShellMenuVerbs.Route("delete", ThreeItems, shiftDown: true).Should().Be(ShellMenuAction.DeletePermanent);
    }

    [Fact]
    public void Route_CutAndCopy_FillTheManagersBuffer()
    {
        ShellMenuVerbs.Route("cut", ThreeItems).Should().Be(ShellMenuAction.Cut);
        ShellMenuVerbs.Route("copy", OneFile).Should().Be(ShellMenuAction.Copy);
    }

    [Fact]
    public void Route_PasteOnAFolder_PastesIntoIt()
    {
        ShellMenuVerbs.Route("paste", OneFolder).Should().Be(ShellMenuAction.Paste);
        ShellMenuVerbs.Route("paste", OneFile).Should().Be(ShellMenuAction.Shell);
    }

    [Fact]
    public void Route_OpenOnOneFolder_EntersItInThePanel()
    {
        ShellMenuVerbs.Route("open", OneFolder).Should().Be(ShellMenuAction.EnterFolder);
    }

    [Fact]
    public void Route_OpenOnAFileOrSeveralItems_StaysTheShells()
    {
        ShellMenuVerbs.Route("open", OneFile).Should().Be(ShellMenuAction.Shell);
        ShellMenuVerbs.Route("open", ShellMenuScope.Items(2, singleItemIsFolder: false)).Should().Be(ShellMenuAction.Shell);
        ShellMenuVerbs.Route("opennewwindow", OneFolder).Should().Be(ShellMenuAction.Shell);
    }

    [Theory]
    [InlineData("properties")]
    [InlineData("link")]
    [InlineData("openas")]
    [InlineData("copyaspath")]
    [InlineData("runas")]
    [InlineData("")]
    [InlineData(null)]
    public void Route_OtherVerbsOnItems_StayTheShells(string? verb)
    {
        ShellMenuVerbs.Route(verb, OneFile).Should().Be(ShellMenuAction.Shell);
    }

    [Fact]
    public void Route_Background_PasteAndNewFolderAreTheManagers()
    {
        ShellMenuVerbs.Route("paste", ShellMenuScope.Background).Should().Be(ShellMenuAction.Paste);
        ShellMenuVerbs.Route("NewFolder", ShellMenuScope.Background).Should().Be(ShellMenuAction.NewFolder);
    }

    [Theory]
    [InlineData("rename")]
    [InlineData("delete")]
    [InlineData("cut")]
    [InlineData("copy")]
    [InlineData("open")]
    [InlineData("properties")]
    [InlineData("pastelink")]
    public void Route_Background_LeavesEverythingElseToTheShell(string verb)
    {
        // The background menu acts on the folder the user stands in: never rename or delete it.
        ShellMenuVerbs.Route(verb, ShellMenuScope.Background).Should().Be(ShellMenuAction.Shell);
    }

    [Fact]
    public void SingleNewName_OneEntryAdded_IsReturned()
    {
        ShellMenuVerbs.SingleNewName(["..", "a.txt", "src"], ["..", "a.txt", "New Text Document.txt", "src"])
            .Should().Be("New Text Document.txt");
    }

    [Fact]
    public void SingleNewName_CaseOnlyDifference_IsNotNew()
    {
        ShellMenuVerbs.SingleNewName(["Readme.md"], ["README.md"]).Should().BeNull();
    }

    [Fact]
    public void SingleNewName_NothingOrSeveralAdded_IsNull()
    {
        ShellMenuVerbs.SingleNewName(["a"], ["a"]).Should().BeNull();
        ShellMenuVerbs.SingleNewName(["a"], ["a", "b", "c"]).Should().BeNull();
    }
}
