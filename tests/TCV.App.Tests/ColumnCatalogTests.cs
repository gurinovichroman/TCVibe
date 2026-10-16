using FluentAssertions;
using TCV.App.Columns;
using TCV.App.Settings;

namespace TCV.App.Tests;

public class ColumnCatalogTests
{
    [Fact]
    public void All_IdsAreUniqueAndStable()
    {
        ColumnCatalog.All.Select(c => c.Id).Should().OnlyHaveUniqueItems();
        ColumnCatalog.All.Should().OnlyContain(c => c.Id == c.Id.ToLowerInvariant());
    }

    [Fact]
    public void All_EveryColumnHasBothTitles()
    {
        foreach (var column in ColumnCatalog.All)
        {
            column.Title("en").Should().NotBeNullOrWhiteSpace();
            column.Title("ru").Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void DefaultLayout_ShowsTheClassicColumnsAndNoMeasuredOnes()
    {
        var layout = ColumnCatalog.DefaultLayout();

        layout.Should().OnlyContain(c => c.Visible);
        layout.Select(c => c.Id).Should().Contain(ColumnCatalog.Name);
        // Measuring costs IO, so LOC and size-on-disk must be opt-in.
        layout.Select(c => c.Id).Should().NotContain([ColumnCatalog.Lines, ColumnCatalog.SizeOnDisk]);
    }

    [Fact]
    public void Normalize_Null_FallsBackToDefaults()
    {
        var layout = ColumnCatalog.Normalize(null);

        layout.Where(c => c.Visible).Select(c => c.Id)
            .Should().BeEquivalentTo(ColumnCatalog.DefaultVisible);
    }

    [Fact]
    public void Normalize_AlwaysCoversEveryKnownColumnExactlyOnce()
    {
        var layout = ColumnCatalog.Normalize([new ColumnLayout { Id = ColumnCatalog.Name, Width = 0, Visible = true }]);

        layout.Select(c => c.Id).Should().BeEquivalentTo(ColumnCatalog.All.Select(c => c.Id));
        layout.Select(c => c.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Normalize_DropsUnknownIds()
    {
        var layout = ColumnCatalog.Normalize(
        [
            new ColumnLayout { Id = ColumnCatalog.Name, Visible = true },
            new ColumnLayout { Id = "column-from-a-future-version", Visible = true },
        ]);

        layout.Should().NotContain(c => c.Id == "column-from-a-future-version");
    }

    [Fact]
    public void Normalize_DropsDuplicates()
    {
        var layout = ColumnCatalog.Normalize(
        [
            new ColumnLayout { Id = ColumnCatalog.Size, Width = 90, Visible = true },
            new ColumnLayout { Id = ColumnCatalog.Size, Width = 40, Visible = false },
        ]);

        var sizes = layout.Where(c => c.Id == ColumnCatalog.Size).ToList();
        sizes.Should().ContainSingle();
        sizes[0].Width.Should().Be(90);   // the first wins
    }

    [Fact]
    public void Normalize_ZeroWidthOnAFixedColumn_TakesTheDefault()
    {
        var layout = ColumnCatalog.Normalize([new ColumnLayout { Id = ColumnCatalog.Date, Width = 0, Visible = true }]);

        layout.First(c => c.Id == ColumnCatalog.Date).Width
            .Should().Be(ColumnCatalog.Find(ColumnCatalog.Date)!.DefaultWidth);
    }

    [Fact]
    public void Normalize_EverythingHidden_FallsBackToDefaults()
    {
        // An all-hidden layout renders an empty grid the user cannot recover from through the UI.
        var layout = ColumnCatalog.Normalize(
            ColumnCatalog.All.Select(c => new ColumnLayout { Id = c.Id, Visible = false }).ToList());

        layout.Where(c => c.Visible).Select(c => c.Id)
            .Should().BeEquivalentTo(ColumnCatalog.DefaultVisible);
    }

    [Fact]
    public void Normalize_KeepsTheStoredOrder()
    {
        var layout = ColumnCatalog.Normalize(
        [
            new ColumnLayout { Id = ColumnCatalog.Size, Visible = true },
            new ColumnLayout { Id = ColumnCatalog.Name, Visible = true },
        ]);

        layout[0].Id.Should().Be(ColumnCatalog.Size);
        layout[1].Id.Should().Be(ColumnCatalog.Name);
    }

    [Fact]
    public void Normalize_AppendsColumnsAddedSinceTheFileWasWritten_Hidden()
    {
        var layout = ColumnCatalog.Normalize(
            ColumnCatalog.DefaultVisible.Select(id => new ColumnLayout { Id = id, Visible = true }).ToList());

        layout.First(c => c.Id == ColumnCatalog.Lines).Visible.Should().BeFalse();
    }
}
