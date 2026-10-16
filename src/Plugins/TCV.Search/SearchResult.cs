using TCV.Core.Interfaces;

namespace TCV.Search;

/// <summary>A single file or folder that matched the search criteria.</summary>
/// <param name="Entry">The matched entry as its file system reported it.</param>
/// <param name="FullPath">Path inside that file system.</param>
/// <param name="MatchLineNumber">1-based line of a content hit, null for a name-only match.</param>
/// <param name="MatchLineText">The whole line the hit sits on, untrimmed.</param>
/// <param name="MatchColumn">0-based character offset of the hit within <paramref name="MatchLineText"/>.</param>
/// <param name="MatchLength">Length of the matched text, for highlighting.</param>
/// <param name="ContextBefore">Lines preceding the hit when context was requested.</param>
/// <param name="ContextAfter">Lines following the hit when context was requested.</param>
public sealed record SearchResult(
    IFileEntry Entry,
    string FullPath,
    int? MatchLineNumber = null,
    string? MatchLineText = null,
    int MatchColumn = 0,
    int MatchLength = 0,
    IReadOnlyList<string>? ContextBefore = null,
    IReadOnlyList<string>? ContextAfter = null)
{
    /// <summary>True when this row is a hit inside a file rather than a name match.</summary>
    public bool IsContentMatch => MatchLineNumber.HasValue;
}
