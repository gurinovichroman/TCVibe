using System.IO;
using FluentAssertions;
using TCV.App.Services;

namespace TCV.App.Tests;

/// <summary>Covers <see cref="MultiRenameProfileStore"/> — JSON round-trip, upsert and delete.</summary>
public class MultiRenameProfileStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tcv-mrprofiles-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void LoadAll_MissingFile_ReturnsEmpty()
    {
        var store = new MultiRenameProfileStore(_path);
        store.LoadAll().Should().BeEmpty();
    }

    [Fact]
    public void Save_ThenGet_RoundTripsAllFields()
    {
        var store = new MultiRenameProfileStore(_path);
        var profile = new MultiRenameProfile
        {
            Name = "photos",
            TemplateText = "IMG_[C].[E]",
            CounterStart = 100,
            CounterStep = 5,
            ApplyTitleCase = true,
            RegexFind = "\\s+",
            RegexReplace = "_",
            RegexCaseSensitive = true,
            InsertText = "_v2",
            InsertPosition = -1,
            DeleteStart = 2,
            DeleteCount = 3,
            RangeRulesSkipExtension = false,
        };

        store.Save(profile);
        var loaded = store.Get("photos");

        loaded.Should().BeEquivalentTo(profile);
    }

    [Fact]
    public void Save_SameName_ReplacesRatherThanDuplicates()
    {
        var store = new MultiRenameProfileStore(_path);
        store.Save(new MultiRenameProfile { Name = "p", TemplateText = "a" });
        store.Save(new MultiRenameProfile { Name = "P", TemplateText = "b" }); // case-insensitive

        store.LoadAll().Should().ContainSingle();
        store.Get("p")!.TemplateText.Should().Be("b");
    }

    [Fact]
    public void Names_ReturnsSortedNames()
    {
        var store = new MultiRenameProfileStore(_path);
        store.Save(new MultiRenameProfile { Name = "zeta" });
        store.Save(new MultiRenameProfile { Name = "alpha" });

        store.Names().Should().ContainInOrder("alpha", "zeta");
    }

    [Fact]
    public void Delete_RemovesTheProfile()
    {
        var store = new MultiRenameProfileStore(_path);
        store.Save(new MultiRenameProfile { Name = "keep" });
        store.Save(new MultiRenameProfile { Name = "drop" });

        store.Delete("drop");

        store.Names().Should().BeEquivalentTo(new[] { "keep" });
    }

    [Fact]
    public void Save_BlankName_Throws()
    {
        var store = new MultiRenameProfileStore(_path);
        var act = () => store.Save(new MultiRenameProfile { Name = "  " });
        act.Should().Throw<ArgumentException>();
    }
}
