using FluentAssertions;
using TCV.MultiRename.Rules;

namespace TCV.MultiRename.Tests;

public class NameOnlyRuleTests
{
    [Fact]
    public void Insert_AtStart_LeavesExtensionAlone()
    {
        var rule = new NameOnlyRule(new InsertRule("IMG_", 0));
        rule.Apply("photo.jpg").Should().Be("IMG_photo.jpg");
    }

    [Fact]
    public void Insert_AtEndOfStem_LandsBeforeTheDot()
    {
        var rule = new NameOnlyRule(new InsertRule("_v2", 99));
        rule.Apply("photo.jpg").Should().Be("photo_v2.jpg");
    }

    [Fact]
    public void Insert_NegativePosition_CountsBackFromEndOfStem()
    {
        var rule = new NameOnlyRule(new InsertRule("-", -2));
        rule.Apply("report.txt").Should().Be("repo-rt.txt");
    }

    [Fact]
    public void Delete_RemovesFromStemOnly()
    {
        var rule = new NameOnlyRule(new DeleteRule(0, 4));
        rule.Apply("IMG_photo.jpg").Should().Be("photo.jpg");
    }

    [Fact]
    public void Delete_CountBeyondStem_StopsAtTheDot()
    {
        var rule = new NameOnlyRule(new DeleteRule(2, 100));
        rule.Apply("photo.jpg").Should().Be("ph.jpg");
    }

    [Fact]
    public void FileWithoutExtension_IsTreatedAsAllStem()
    {
        var rule = new NameOnlyRule(new InsertRule("_", 6));
        rule.Apply("README").Should().Be("README_");
    }

    [Fact]
    public void DotfileHasNoExtension_SoTheWholeNameIsTheStem()
    {
        var rule = new NameOnlyRule(new InsertRule("x", 0));
        rule.Apply(".gitignore").Should().Be("x.gitignore");
    }

    [Fact]
    public void MultipleDots_OnlyTheLastOneStartsTheExtension()
    {
        var rule = new NameOnlyRule(new DeleteRule(0, 8));
        rule.Apply("archive.tar.gz").Should().Be("tar.gz");
    }

    [Fact]
    public void DotfileSplit_AgreesWithTheTemplatePlaceholders()
    {
        // Both sides of the dialog must share one notion of "extension": the [N]/[E] tokens
        // and the "leave the extension alone" rules run over the same files in one pass.
        var entry = new Moq.Mock<TCV.Core.Interfaces.IFileEntry>();
        entry.Setup(e => e.Name).Returns(".gitignore");
        entry.Setup(e => e.Path).Returns(new TCV.Core.Domain.VfsPath("/home/.gitignore"));
        var context = new RenameContext(entry.Object, 1, new DateTime(2026, 3, 14));

        new RenameTemplate("[N]").Evaluate(context).Should().Be(".gitignore");
        new RenameTemplate("[E]").Evaluate(context).Should().BeEmpty();
        new NameOnlyRule(new InsertRule("x", 0)).Apply(".gitignore").Should().Be("x.gitignore");
    }

    [Fact]
    public void EmptyName_PassesThroughUnchanged()
    {
        var rule = new NameOnlyRule(new InsertRule("x", 0));
        rule.Apply(string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void WithoutTheDecorator_TheRuleSeesTheExtension()
    {
        new InsertRule("_v2", 99).Apply("photo.jpg").Should().Be("photo.jpg_v2");
    }

    [Fact]
    public void RulesCompose_DeleteThenInsert()
    {
        var delete = new NameOnlyRule(new DeleteRule(0, 4));
        var insert = new NameOnlyRule(new InsertRule("2026_", 0));

        insert.Apply(delete.Apply("IMG_holiday.png")).Should().Be("2026_holiday.png");
    }
}
