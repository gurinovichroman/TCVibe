using FluentAssertions;
using TCV.Core.Domain;

namespace TCV.Core.Tests;

public sealed class AppPathsTests
{
    [Fact]
    public void Data_SeveralParts_AreJoinedUnderTheDataDirectory()
    {
        AppPaths.Data("logs", "tcv-.log").Should().Be(Path.Combine(AppPaths.DataDirectory, "logs", "tcv-.log"));
    }

    [Fact]
    public void DataDirectory_ByDefault_IsTheExeFolder()
    {
        // Nothing in the test host calls UseDataDirectory: a dev build keeps its state next to the exe.
        AppPaths.DataDirectory.Should().Be(AppContext.BaseDirectory);
    }
}
