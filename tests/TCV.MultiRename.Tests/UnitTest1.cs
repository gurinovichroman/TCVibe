using System;
using System.IO;
using FluentAssertions;
using Moq;
using TCV.Core.Domain;
using TCV.Core.Interfaces;
using TCV.MultiRename;
using TCV.MultiRename.Rules;

namespace TCV.MultiRename.Tests;

public class RenameTemplateTests
{
    private static IFileEntry MakeEntry(string name, string parentPath = "/home/user/docs")
    {
        var mock = new Mock<IFileEntry>();
        var path = new VfsPath(parentPath + "/" + name);
        mock.Setup(e => e.Name).Returns(name);
        mock.Setup(e => e.Path).Returns(path);
        return mock.Object;
    }

    private static RenameContext MakeContext(string name, int counter = 1,
        DateTime? date = null, string parentPath = "/home/user/docs")
        => new(MakeEntry(name, parentPath), counter, date ?? new DateTime(2026, 3, 14, 9, 5, 30));

    // ── [N] placeholder ───────────────────────────────────────────────────────

    [Fact]
    public void N_ReturnsNameWithoutExtension()
    {
        var tpl = new RenameTemplate("[N]");
        tpl.Evaluate(MakeContext("photo.jpg")).Should().Be("photo");
    }

    [Fact]
    public void N_FileWithNoExtension_ReturnsFullName()
    {
        var tpl = new RenameTemplate("[N]");
        tpl.Evaluate(MakeContext("README")).Should().Be("README");
    }

    // ── [E] placeholder ───────────────────────────────────────────────────────

    [Fact]
    public void E_ReturnsExtensionWithoutDot()
    {
        var tpl = new RenameTemplate("[E]");
        tpl.Evaluate(MakeContext("photo.jpg")).Should().Be("jpg");
    }

    [Fact]
    public void E_NoExtension_ReturnsEmpty()
    {
        var tpl = new RenameTemplate("[E]");
        tpl.Evaluate(MakeContext("README")).Should().Be(string.Empty);
    }

    // ── [N].[E] combined ──────────────────────────────────────────────────────

    [Fact]
    public void NAndE_CombinedProducesOriginalName()
    {
        var tpl = new RenameTemplate("[N].[E]");
        tpl.Evaluate(MakeContext("photo.jpg")).Should().Be("photo.jpg");
    }

    // ── [C] counter ───────────────────────────────────────────────────────────

    [Fact]
    public void C_DefaultCounter_StartsAt1()
    {
        var tpl = new RenameTemplate("file_[C]");
        tpl.Evaluate(MakeContext("a.txt", counter: 1)).Should().Be("file_1");
    }

    [Fact]
    public void C_DefaultCounter_IncrementsByOne()
    {
        var tpl = new RenameTemplate("file_[C]");
        tpl.Evaluate(MakeContext("a.txt", counter: 3)).Should().Be("file_3");
    }

    [Fact]
    public void C_WithExplicitStart_UsesStart()
    {
        // [C10] means start=10, step=1 -> first file = 10
        var tpl = new RenameTemplate("file_[C10]");
        tpl.Evaluate(MakeContext("a.txt", counter: 1)).Should().Be("file_10");
    }

    [Fact]
    public void C_WithStartAndStep_ComputesCorrectly()
    {
        // [C10+2] start=10 step=2; counter=1 -> 10, counter=2 -> 12
        var tpl = new RenameTemplate("file_[C10+2]");
        tpl.Evaluate(MakeContext("a.txt", counter: 1)).Should().Be("file_10");
        tpl.Evaluate(MakeContext("a.txt", counter: 2)).Should().Be("file_12");
        tpl.Evaluate(MakeContext("a.txt", counter: 3)).Should().Be("file_14");
    }

    // ── [D] date ──────────────────────────────────────────────────────────────

    [Fact]
    public void D_DefaultFormat_IsIso()
    {
        var tpl = new RenameTemplate("[D]");
        var ctx = MakeContext("x.txt", date: new DateTime(2026, 3, 14));
        tpl.Evaluate(ctx).Should().Be("2026-03-14");
    }

    [Fact]
    public void D_CustomFormat_UsesFormat()
    {
        var tpl = new RenameTemplate("[D:dd.MM.yyyy]");
        var ctx = MakeContext("x.txt", date: new DateTime(2026, 3, 14));
        tpl.Evaluate(ctx).Should().Be("14.03.2026");
    }

    // ── [T] time ──────────────────────────────────────────────────────────────

    [Fact]
    public void T_ReturnsHyphenSeparatedTime()
    {
        var tpl = new RenameTemplate("[T]");
        var ctx = MakeContext("x.txt", date: new DateTime(2026, 3, 14, 9, 5, 30));
        tpl.Evaluate(ctx).Should().Be("09-05-30");
    }

    // ── [P] parent folder ─────────────────────────────────────────────────────

    [Fact]
    public void P_ReturnsParentFolderName()
    {
        var tpl = new RenameTemplate("[P]");
        tpl.Evaluate(MakeContext("file.txt", parentPath: "/home/user/docs")).Should().Be("docs");
    }

    // ── Empty template ────────────────────────────────────────────────────────

    [Fact]
    public void EmptyTemplate_ReturnsOriginalName()
    {
        var tpl = new RenameTemplate(string.Empty);
        tpl.Evaluate(MakeContext("photo.jpg")).Should().Be("photo.jpg");
    }

    // ── Combined example ──────────────────────────────────────────────────────

    [Fact]
    public void ComplexTemplate_ProducesExpectedResult()
    {
        var tpl = new RenameTemplate("[D:yyyy-MM-dd]_[N]_[C10+5].[E]");
        var ctx = MakeContext("holiday.jpg", counter: 2, date: new DateTime(2026, 3, 14));
        // counter=2: 10 + (2-1)*5 = 15
        tpl.Evaluate(ctx).Should().Be("2026-03-14_holiday_15.jpg");
    }
}

public class CaseChangeRuleTests
{
    [Fact]
    public void Uppercase_ConvertsAllToUpper()
    {
        var rule = new CaseChangeRule(CaseChangeMode.Uppercase);
        rule.Apply("hello World.txt").Should().Be("HELLO WORLD.TXT");
    }

    [Fact]
    public void Lowercase_ConvertsAllToLower()
    {
        var rule = new CaseChangeRule(CaseChangeMode.Lowercase);
        rule.Apply("HELLO WORLD.TXT").Should().Be("hello world.txt");
    }

    [Fact]
    public void TitleCase_CapitalisesWords()
    {
        var rule = new CaseChangeRule(CaseChangeMode.TitleCase);
        rule.Apply("hello world.txt").Should().Be("Hello World.Txt");
    }
}

public class RegexReplaceRuleTests
{
    [Fact]
    public void Apply_ReplacesMatchingPattern()
    {
        var rule = new RegexReplaceRule("\\s+", "_");
        rule.Apply("my file name.txt").Should().Be("my_file_name.txt");
    }

    [Fact]
    public void Apply_CaseInsensitiveByDefault()
    {
        var rule = new RegexReplaceRule("abc", "X");
        rule.Apply("ABCdef.txt").Should().Be("Xdef.txt");
    }

    [Fact]
    public void Apply_CaseSensitiveWhenRequested()
    {
        var rule = new RegexReplaceRule("abc", "X", caseSensitive: true);
        rule.Apply("ABCdef.txt").Should().Be("ABCdef.txt"); // no match
    }
}

public class InsertRuleTests
{
    [Fact]
    public void Apply_InsertsAtGivenPosition()
    {
        var rule = new InsertRule("PREFIX_", 0);
        rule.Apply("file.txt").Should().Be("PREFIX_file.txt");
    }

    [Fact]
    public void Apply_InsertsInMiddle()
    {
        var rule = new InsertRule("_NEW_", 4);
        rule.Apply("filexyz.txt").Should().Be("file_NEW_xyz.txt");
    }

    [Fact]
    public void Apply_PositionBeyondEnd_Appends()
    {
        var rule = new InsertRule("_SUFFIX", 100);
        rule.Apply("file.txt").Should().Be("file.txt_SUFFIX");
    }

    [Fact]
    public void Apply_NegativePosition_CountsFromEnd()
    {
        // position -1 inserts before the last character
        var rule = new InsertRule("X", -1);
        rule.Apply("abcd").Should().Be("abcXd");
    }
}

public class DeleteRuleTests
{
    [Fact]
    public void Apply_DeletesRangeOfChars()
    {
        var rule = new DeleteRule(0, 4);
        rule.Apply("IMG_photo.jpg").Should().Be("photo.jpg");
    }

    [Fact]
    public void Apply_StartBeyondLength_ReturnsUnchanged()
    {
        var rule = new DeleteRule(100, 3);
        rule.Apply("file.txt").Should().Be("file.txt");
    }

    [Fact]
    public void Apply_CountExceedsLength_ClampsToEnd()
    {
        var rule = new DeleteRule(4, 999);
        rule.Apply("file.txt").Should().Be("file");
    }
}

public class RenameEnginePreviewTests
{
    private static IFileEntry MakeEntry(string name, string parentPath = "/docs")
    {
        var mock = new Mock<IFileEntry>();
        mock.Setup(e => e.Name).Returns(name);
        mock.Setup(e => e.Path).Returns(new VfsPath(parentPath + "/" + name));
        return mock.Object;
    }

    private static IVirtualFileSystem MakeFakeFs() => Mock.Of<IVirtualFileSystem>();

    [Fact]
    public void Preview_SimpleTemplate_ProducesExpectedNames()
    {
        var files = new[] { MakeEntry("a.jpg"), MakeEntry("b.jpg") };
        var engine = new RenameEngine(MakeFakeFs());
        var template = new RenameTemplate("photo_[C].[E]");

        var previews = engine.Preview(files, template);

        previews.Should().HaveCount(2);
        previews[0].NewName.Should().Be("photo_1.jpg");
        previews[1].NewName.Should().Be("photo_2.jpg");
    }

    [Fact]
    public void Preview_DuplicateNewNames_MarksConflicts()
    {
        // Static template produces same name for all files -> conflict on 2nd+
        var files = new[] { MakeEntry("a.jpg"), MakeEntry("b.jpg") };
        var engine = new RenameEngine(MakeFakeFs());
        var template = new RenameTemplate("same_name.jpg");

        var previews = engine.Preview(files, template);

        previews[0].HasConflict.Should().BeFalse();
        previews[1].HasConflict.Should().BeTrue();
    }

    [Fact]
    public void Preview_NoConflicts_AllMarkedClean()
    {
        var files = new[] { MakeEntry("a.txt"), MakeEntry("b.txt") };
        var engine = new RenameEngine(MakeFakeFs());
        var template = new RenameTemplate("[N]_renamed.[E]");

        var previews = engine.Preview(files, template);

        previews.Should().OnlyContain(p => !p.HasConflict);
    }

    [Fact]
    public void Preview_WithCaseChangeRule_TransformsNames()
    {
        var files = new[] { MakeEntry("hello.txt") };
        var engine = new RenameEngine(MakeFakeFs());
        var template = new RenameTemplate("[N].[E]");
        var rules = new IRenameRule[] { new CaseChangeRule(CaseChangeMode.Uppercase) };

        var previews = engine.Preview(files, template, rules);

        previews[0].NewName.Should().Be("HELLO.TXT");
    }
}
