using FluentAssertions;
using TCV.Ai.Mcp;

namespace TCV.Ai.Tests;

/// <summary>The addressing contract an agent is held to (§ 3.2): three forms, and clear refusals.</summary>
public class AgentPathRefTests
{
    [Theory]
    [InlineData(@"D:\repo\src\a.cs")]
    [InlineData(@"C:\Users\me\Downloads")]
    public void Parse_WindowsPath_IsLocal(string path)
    {
        var reference = AgentPathRef.Parse(path);

        reference.Kind.Should().Be(AgentPathKind.Local);
        reference.Container.Should().Be(path);
        reference.ToString().Should().Be(path);
    }

    [Fact]
    public void Parse_FileScheme_IsLocalToo()
    {
        var reference = AgentPathRef.Parse(@"file://D:\repo\a.cs");

        reference.Kind.Should().Be(AgentPathKind.Local);
        reference.Container.Should().Be(@"D:\repo\a.cs");
    }

    [Fact]
    public void Parse_ArchiveWithInnerPath_SplitsAtTheSeparator()
    {
        var reference = AgentPathRef.Parse(@"zip://D:\dist.zip!/logs/app.log");

        reference.Kind.Should().Be(AgentPathKind.Archive);
        reference.Scheme.Should().Be("zip");
        reference.Container.Should().Be(@"D:\dist.zip");
        reference.Inner.Should().Be("/logs/app.log");
    }

    [Fact]
    public void Parse_ArchiveWithoutInnerPath_PointsAtTheRoot()
    {
        var reference = AgentPathRef.Parse(@"7z://D:\dist.7z");

        reference.Kind.Should().Be(AgentPathKind.Archive);
        reference.Inner.Should().Be("/");
    }

    [Fact]
    public void Parse_ArchiveWithNoContainer_IsRejectedWithAHint()
    {
        var act = () => AgentPathRef.Parse("zip://");

        act.Should().Throw<McpToolException>()
            .Which.Error.Hint.Should().Contain("!/");
    }

    [Theory]
    [InlineData("panel://active/src/a.cs", "active", "/src/a.cs")]
    [InlineData("panel://opposite/pub", "opposite", "/pub")]
    [InlineData("panel://active", "active", "/")]
    [InlineData("panel://active/", "active", "/")]
    public void Parse_PanelPath_SplitsPanelAndInner(string path, string panel, string inner)
    {
        var reference = AgentPathRef.Parse(path);

        reference.Kind.Should().Be(AgentPathKind.Panel);
        reference.Container.Should().Be(panel);
        reference.Inner.Should().Be(inner);
    }

    [Fact]
    public void Parse_PanelPath_KnowsWhichPanelIsActive()
    {
        AgentPathRef.Parse("panel://active/x").IsActivePanel.Should().BeTrue();
        AgentPathRef.Parse("panel://opposite/x").IsActivePanel.Should().BeFalse();
    }

    [Fact]
    public void Parse_UnknownPanelName_IsRejected()
    {
        var act = () => AgentPathRef.Parse("panel://left/x");

        act.Should().Throw<McpToolException>()
            .Which.Error.Code.Should().Be(McpError.InvalidParams);
    }

    [Theory]
    [InlineData("sftp://user@host/pub/x")]
    [InlineData("ftp://host/pub")]
    [InlineData("s3://bucket/logs")]
    [InlineData("gdrive://me@example.com/docs")]
    [InlineData("dav://host/share")]
    public void Parse_RemoteScheme_IsRefusedAndPointsAtPanelAddressing(string path)
    {
        // Reconstructing a remote connection would mean the server holding credentials; the honest
        // answer is "open it in a panel and address that".
        var act = () => AgentPathRef.Parse(path);

        act.Should().Throw<McpToolException>()
            .Which.Error.Hint.Should().Contain("panel://active");
    }

    [Fact]
    public void Parse_UnknownScheme_IsRejected()
    {
        var act = () => AgentPathRef.Parse("gopher://host/x");

        act.Should().Throw<McpToolException>()
            .Which.Error.Code.Should().Be(McpError.InvalidParams);
    }

    [Fact]
    public void Parse_EmptyPath_IsRejected()
    {
        var act = () => AgentPathRef.Parse("   ");

        act.Should().Throw<McpToolException>();
    }

    [Fact]
    public void ToString_RoundTripsAnArchiveReference()
    {
        const string path = @"zip://D:\dist.zip!/logs/app.log";

        AgentPathRef.Parse(path).ToString().Should().Be(path);
    }

    [Fact]
    public void WithInner_RebasesTheReferenceOntoAChild()
    {
        var reference = AgentPathRef.Parse(@"zip://D:\dist.zip!/logs");

        reference.WithInner("/logs/app.log").ToString()
            .Should().Be(@"zip://D:\dist.zip!/logs/app.log");
    }

    [Fact]
    public void WithInner_NormalisesBackslashesAndMissingLeadingSlash()
    {
        var reference = AgentPathRef.Parse(@"zip://D:\dist.zip");

        reference.WithInner(@"logs\app.log").Inner.Should().Be("/logs/app.log");
    }
}
