using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.Selection;
using TCV.App.ViewModels;
using TCV.App.ViewModels.Dialogs;
using TCV.Core.Domain;

namespace TCV.App.Tests;

/// <summary>Covers the Num+ grammar, the template conditions and the marks Num/ brings back.</summary>
public class SelectionTemplateTests
{
    private static readonly DateTime Now = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);

    private static SelectionCandidate File(string name, DateTime modified = default) => new(name, false, modified);
    private static SelectionCandidate Folder(string name, DateTime modified = default) => new(name, true, modified);

    private static bool Matches(string mask, SelectionCandidate c, bool includeFolders = false) =>
        SelectionRule.From(new SelectionTemplate { Mask = mask, IncludeFolders = includeFolders }, Now).Matches(c);

    [Theory]
    [InlineData("*.cs;*.md", "a.cs", true)]
    [InlineData("*.cs;*.md", "a.md", true)]
    [InlineData("*.cs;*.md", "a.txt", false)]
    [InlineData("*.cs | *.g.cs", "Form.g.cs", false)]
    [InlineData("*.cs | *.g.cs", "Form.cs", true)]
    [InlineData("| *.bak", "notes.txt", true)]
    [InlineData("| *.bak", "notes.bak", false)]
    [InlineData("my report*.docx", "my report 2.docx", true)]
    [InlineData("*.*", "README", true)]
    public void Mask_Files(string mask, string name, bool expected) =>
        Matches(mask, File(name)).Should().Be(expected);

    [Fact]
    public void Mask_Space_IsPartOfTheName_NotASeparator()
    {
        Matches("*.cs *.md", File("a.cs")).Should().BeFalse("a space no longer separates masks");
    }

    [Fact]
    public void Mask_PlainMask_SkipsFolders_UnlessAskedFor()
    {
        Matches("*", Folder("src")).Should().BeFalse();
        Matches("*", Folder("src"), includeFolders: true).Should().BeTrue();
    }

    [Theory]
    [InlineData(@"bin\;obj\", "bin", true)]
    [InlineData("bin/", "bin", true)]
    [InlineData(@"*\", "anything", true)]
    [InlineData(@"bin\", "src", false)]
    public void Mask_TrailingSlash_NamesFolders(string mask, string folder, bool expected)
    {
        Matches(mask, Folder(folder)).Should().Be(expected);
        Matches(mask, File(folder)).Should().BeFalse("a folder mask never names a file");
    }

    [Fact]
    public void Mask_FolderExclusion_RemovesThatFolder()
    {
        Matches(@"*\ | node_modules\", Folder("node_modules")).Should().BeFalse();
        Matches(@"*\ | node_modules\", Folder("src")).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ;  ")]
    public void Mask_Blank_IsEmpty(string mask) =>
        SelectionRule.From(new SelectionTemplate { Mask = mask }, Now).IsEmpty.Should().BeTrue();

    [Fact]
    public void Age_OlderThan_MarksOnlyOlderEntries_FoldersIncludedWhenAsked()
    {
        var rule = SelectionRule.From(new SelectionTemplate
        {
            Mask = "*", IncludeFolders = true, Age = SelectionAge.OlderThan, AgeAmount = 2, AgeUnit = SelectionAgeUnit.Days,
        }, Now);

        rule.Matches(File("old.tmp", Now.AddDays(-3))).Should().BeTrue();
        rule.Matches(Folder("old-dir", Now.AddDays(-3))).Should().BeTrue();
        rule.Matches(File("fresh.tmp", Now.AddDays(-1))).Should().BeFalse();
        rule.Matches(File("unknown.tmp")).Should().BeFalse("an unknown time is neither old nor new");
    }

    [Fact]
    public void Age_NewerThan_ComparesMoments_NotClockReadings()
    {
        var rule = SelectionRule.From(new SelectionTemplate
        {
            Mask = "*", Age = SelectionAge.NewerThan, AgeAmount = 1, AgeUnit = SelectionAgeUnit.Hours,
        }, Now);

        // 30 minutes ago, once as UTC (an FTP listing) and once as local time (the disk).
        rule.Matches(File("utc.txt", Now.AddMinutes(-30))).Should().BeTrue();
        rule.Matches(File("local.txt", Now.AddMinutes(-30).ToLocalTime())).Should().BeTrue();
        rule.Matches(File("old.txt", Now.AddHours(-2).ToLocalTime())).Should().BeFalse();
    }

    [Fact]
    public void GitAndAgentConditions_AreRequired_WhenSet()
    {
        var git = SelectionRule.From(new SelectionTemplate { Mask = "*", GitChanged = true }, Now);
        git.NeedsGit.Should().BeTrue();
        git.Matches(new SelectionCandidate("a.cs", false, Now, GitChanged: true)).Should().BeTrue();
        git.Matches(new SelectionCandidate("b.cs", false, Now)).Should().BeFalse();

        var agent = SelectionRule.From(new SelectionTemplate { Mask = "*.cs", AgentChanged = true }, Now);
        agent.Matches(new SelectionCandidate("a.cs", false, Now, AgentChanged: true)).Should().BeTrue();
        agent.Matches(new SelectionCandidate("a.md", false, Now, AgentChanged: true)).Should().BeFalse();
        agent.Matches(new SelectionCandidate("b.cs", false, Now)).Should().BeFalse();
    }

    [Fact]
    public void Builtins_AllParse_AndHaveDistinctIds()
    {
        var builtins = SelectionTemplate.CreateBuiltins();
        builtins.Select(t => t.Builtin).Should().OnlyHaveUniqueItems();
        builtins.Should().OnlyContain(t => !SelectionRule.From(t, Now).IsEmpty);
        SelectionTemplate.CreateBuiltins().Should().NotBeSameAs(builtins, "every caller gets a list of its own");
    }

    [Fact]
    public void History_MovesARepeatToTheTop_AndKeepsTheCapacity()
    {
        var history = new List<string>();
        for (var i = 0; i < SelectionHistory.Capacity + 5; i++)
        {
            SelectionHistory.Push(history, $"*.{i}");
        }

        SelectionHistory.Push(history, "*.20");
        SelectionHistory.Push(history, "  ");

        history.Should().HaveCount(SelectionHistory.Capacity);
        history[0].Should().Be("*.20");
        history.Count(m => m == "*.20").Should().Be(1);
    }

    // ── The panel ─────────────────────────────────────────────────────────────

    private static async Task<FileListViewModel> CreateList(params (string Name, bool Folder, DateTime Time)[] entries)
    {
        var fs = new InMemoryFileSystem("file");
        foreach (var (name, folder, time) in entries)
        {
            if (folder)
            {
                fs.AddDirectory($"/C:/work/{name}");
            }
            else if (time == default)
            {
                fs.AddFile($"/C:/work/{name}", new byte[] { 1 });
            }
            else
            {
                fs.AddFile($"/C:/work/{name}", new byte[] { 1 }, time);
            }
        }

        var list = new FileListViewModel(fs, NullLogger<FileListViewModel>.Instance);
        foreach (var (name, _, _) in entries)
        {
            list.Files.Add(new FileEntryViewModel((await fs.GetEntryAsync(new VfsPath($"/C:/work/{name}")))!));
        }
        return list;
    }

    private static Task<FileListViewModel> CreateList(params string[] files) =>
        CreateList(files.Select(f => (f, false, default(DateTime))).ToArray());

    private static IEnumerable<string> Marked(FileListViewModel list) => list.SelectedFiles.Select(f => f.Name);

    [Fact]
    public async Task SelectByTemplate_OlderThan_MarksTheOldFilesOfTemp()
    {
        var now = DateTime.UtcNow;
        var list = await CreateList(
            ("old.tmp", false, now.AddDays(-5)),
            ("new.tmp", false, now.AddHours(-1)),
            ("olddir", true, default));

        var named = await list.SelectByTemplateAsync(new SelectionTemplate
        {
            Mask = "*", Age = SelectionAge.OlderThan, AgeAmount = 2, AgeUnit = SelectionAgeUnit.Days,
        }, select: true, nowUtc: now);

        named.Should().Be(1);
        Marked(list).Should().BeEquivalentTo("old.tmp");
    }

    [Fact]
    public async Task SelectByMask_FolderMask_MarksTheFolder()
    {
        var list = await CreateList(("bin", true, default), ("a.cs", false, default));

        list.SelectByMask(@"bin\", select: true);

        Marked(list).Should().BeEquivalentTo("bin");
    }

    [Fact]
    public async Task SelectSameExtension_MarksFilesLikeTheCursor_CaseIgnored()
    {
        var list = await CreateList("a.cs", "B.CS", "c.md", "README");
        list.SelectedItem = list.Files[0];

        list.SelectSameExtension(select: true).Should().BeTrue();
        Marked(list).Should().BeEquivalentTo("a.cs", "B.CS");

        list.SelectedItem = list.Files[3];
        list.SelectSameExtension(select: true).Should().BeTrue();
        Marked(list).Should().BeEquivalentTo("a.cs", "B.CS", "README");

        list.SelectedItem = list.Files[0];
        list.SelectSameExtension(select: false).Should().BeTrue();
        Marked(list).Should().BeEquivalentTo("README");
    }

    [Fact]
    public async Task RestoreSelection_BringsBackWhatDeselectAllTookAway()
    {
        var list = await CreateList("a.cs", "b.cs", "c.md");
        list.CanRestoreSelection.Should().BeFalse();

        list.SelectByMask("*.cs", select: true);
        list.DeselectAll();
        Marked(list).Should().BeEmpty();

        list.RestoreSelection().Should().Be(2);
        Marked(list).Should().BeEquivalentTo("a.cs", "b.cs");
    }

    [Fact]
    public async Task RestoreSelection_FollowsTheNames_IntoAnotherListing()
    {
        var first = await CreateList("a.cs", "b.cs", "c.md");
        first.SelectAll();
        first.PruneSelection([]);   // what loading another folder does to the marks

        // The same names elsewhere: what Num/ in the next folder finds.
        var second = await CreateList("a.cs", "c.md", "d.txt");
        foreach (var f in second.Files)
        {
            first.Files.Add(f);
        }
        foreach (var f in first.Files.Take(3).ToList())
        {
            first.Files.Remove(f);
        }

        first.RestoreSelection().Should().Be(2);
        Marked(first).Should().BeEquivalentTo("a.cs", "c.md");
    }

    // ── The dialog's templates ───────────────────────────────────────────────

    [Fact]
    public void Dialog_SaveTemplate_AddsAndThenUpdatesByName()
    {
        var templates = new List<SelectionTemplate>();
        var saves = 0;
        var vm = new SelectByMaskDialogViewModel(true, templates, [], false, () => saves++)
        {
            Mask = "*.log",
            AgeModeIndex = (int)SelectionAge.OlderThan,
            AgeAmountText = "7",
            TemplateName = "Old logs",
        };

        vm.SaveTemplateCommand.Execute(null);
        templates.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Name = "Old logs", Mask = "*.log", Age = SelectionAge.OlderThan, AgeAmount = 7,
        });

        vm.Mask = "*.log;*.txt";
        vm.SaveTemplateCommand.Execute(null);

        templates.Should().ContainSingle().Which.Mask.Should().Be("*.log;*.txt");
        saves.Should().Be(2);
        vm.SelectedTemplate!.Template.Should().BeSameAs(templates[0]);
    }

    [Fact]
    public void Dialog_PickingATemplate_FillsTheFields_AndDeleteRemovesIt()
    {
        var templates = new List<SelectionTemplate>
        {
            new() { Name = "Agent", Mask = "*.cs", AgentChanged = true, IncludeFolders = true },
        };
        var vm = new SelectByMaskDialogViewModel(false, templates, ["*.md"], false, () => { });
        vm.Mask.Should().Be("*.md", "the last mask comes first");

        vm.SelectedTemplate = vm.Templates[0];

        vm.BuildTemplate().SameRule(templates[0]).Should().BeTrue();
        vm.DeleteTemplateCommand.Execute(null);
        templates.Should().BeEmpty();
        vm.Templates.Should().BeEmpty();
    }

    [Fact]
    public void Dialog_InAWorkspaceWindow_TemplatesAreReadOnly()
    {
        var vm = new SelectByMaskDialogViewModel(true, SelectionTemplate.CreateBuiltins(), [], false, () => { },
            canEditTemplates: false)
        {
            TemplateName = "Mine",
        };
        vm.SelectedTemplate = vm.Templates[0];

        vm.SaveTemplateCommand.CanExecute(null).Should().BeFalse();
        vm.DeleteTemplateCommand.CanExecute(null).Should().BeFalse();
        vm.ConfirmCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void Dialog_AgeWithoutAPositiveAmount_CannotBeConfirmed()
    {
        var vm = new SelectByMaskDialogViewModel(true, [], [], false, () => { })
        {
            AgeModeIndex = (int)SelectionAge.OlderThan,
            AgeAmountText = "0",
        };

        vm.ConfirmCommand.CanExecute(null).Should().BeFalse();
        vm.AgeAmountText = "3";
        vm.ConfirmCommand.CanExecute(null).Should().BeTrue();
    }
}
