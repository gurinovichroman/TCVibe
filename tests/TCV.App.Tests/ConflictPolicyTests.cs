using System.IO;
using FluentAssertions;
using TCV.App.Operations.Conflict;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Tests;

/// <summary>
/// The answer to «a file is already there», shared by the copy between file systems, the extraction
/// from an archive and adding to one (6.47): "only if newer" decides alone, the dialog's choice otherwise.
/// </summary>
public sealed class ConflictPolicyTests
{
    private static readonly DateTime Noon = new(2025, 3, 1, 12, 0, 0, DateTimeKind.Local);

    private static IFileEntry Entry(DateTime modified, bool directory = false) =>
        new FileEntry(new VfsPath("/x/a.txt"), "a.txt", directory, 10, modified,
            directory ? FileAttributes.Directory : FileAttributes.Normal);

    private static ConflictInfo Conflict(DateTime source, DateTime existing, bool directory = false) =>
        new(Entry(source, directory), Entry(existing), new VfsPath("/y/a.txt"));

    private static Func<ConflictInfo, CancellationToken, Task<ConflictResolution>> Answer(
        ConflictResolution resolution, List<ConflictInfo>? asked = null) => (conflict, _) =>
    {
        asked?.Add(conflict);
        return Task.FromResult(resolution);
    };

    private static Task<ConflictResolution> NeverAsked(ConflictInfo conflict, CancellationToken ct) =>
        throw new InvalidOperationException("only if newer decides without asking");

    [Fact]
    public async Task ResolveAsync_OnlyIfNewerAndTheSourceIsNewer_OverwritesWithoutAsking()
    {
        var resolution = await ConflictPolicy.ResolveAsync(
            Conflict(Noon.AddHours(1), Noon), onlyIfNewer: true, NeverAsked, CancellationToken.None);

        resolution.Should().Be(ConflictResolution.Overwrite);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-60)]
    public async Task ResolveAsync_OnlyIfNewerAndTheSourceIsNotNewer_SkipsWithoutAsking(int minutes)
    {
        var resolution = await ConflictPolicy.ResolveAsync(
            Conflict(Noon.AddMinutes(minutes), Noon), onlyIfNewer: true, NeverAsked, CancellationToken.None);

        resolution.Should().Be(ConflictResolution.Skip);
    }

    [Fact]
    public async Task ResolveAsync_OnlyIfNewerForAFolder_AsksTheStrategy()
    {
        var asked = new List<ConflictInfo>();

        var resolution = await ConflictPolicy.ResolveAsync(
            Conflict(Noon.AddDays(-1), Noon, directory: true), onlyIfNewer: true,
            Answer(ConflictResolution.RenameAuto, asked), CancellationToken.None);

        asked.Should().ContainSingle();
        resolution.Should().Be(ConflictResolution.RenameAuto);
    }

    [Theory]
    [InlineData(ConflictResolution.Overwrite)]
    [InlineData(ConflictResolution.Skip)]
    [InlineData(ConflictResolution.RenameAuto)]
    [InlineData(ConflictResolution.Cancel)]
    public async Task ResolveAsync_WithoutOnlyIfNewer_ReturnsTheStrategysAnswer(ConflictResolution answer)
    {
        var resolution = await ConflictPolicy.ResolveAsync(
            Conflict(Noon, Noon.AddDays(1)), onlyIfNewer: false, Answer(answer), CancellationToken.None);

        resolution.Should().Be(answer);
    }

    [Theory]
    [InlineData(60, ConflictResolution.Overwrite)]
    [InlineData(0, ConflictResolution.Skip)]
    [InlineData(-60, ConflictResolution.Skip)]
    public async Task ResolveAsync_OverwriteIfNewerAnswer_IsSettledByTheTimes(int minutes, ConflictResolution expected)
    {
        var resolution = await ConflictPolicy.ResolveAsync(
            Conflict(Noon.AddMinutes(minutes), Noon), onlyIfNewer: false,
            Answer(ConflictResolution.OverwriteIfNewer), CancellationToken.None);

        resolution.Should().Be(expected);
    }

    [Fact]
    public async Task ResolveAsync_ServerTimeInUtc_IsComparedAsAMomentNotAsANumber()
    {
        // TRAP-48: the same moment, once in UTC (a server) and once local (the disk), is not "newer".
        var local = Noon;
        var utc = local.ToUniversalTime();

        var resolution = await ConflictPolicy.ResolveAsync(
            Conflict(utc, local), onlyIfNewer: true, NeverAsked, CancellationToken.None);

        resolution.Should().Be(ConflictResolution.Skip);
    }

    [Fact]
    public async Task ResolveAsync_DifferenceWithinTheTolerance_IsNotNewer()
    {
        // A ZIP rounds times down to two seconds: the file one second "newer" than its own entry is the same file.
        var resolution = await ConflictPolicy.ResolveAsync(
            Conflict(Noon.AddSeconds(1), Noon), onlyIfNewer: true, NeverAsked, CancellationToken.None,
            tolerance: TimeSpan.FromSeconds(2));

        resolution.Should().Be(ConflictResolution.Skip);
    }
}
