namespace TCV.Core.Domain;

/// <summary>
/// Where the program keeps its state (INV-03, ADR-0031). A dev build keeps it next to the exe. The
/// portable layout points it at <c>data\</c>, beside the <c>current\</c> folder that every update
/// replaces — which is why nothing that writes may use <c>AppContext.BaseDirectory</c> (TRAP-70).
/// </summary>
public static class AppPaths
{
    private static string? _dataDirectory;

    /// <summary>The folder every store writes into.</summary>
    public static string DataDirectory => _dataDirectory ?? AppContext.BaseDirectory;

    /// <summary>A file or folder inside <see cref="DataDirectory"/>.</summary>
    public static string Data(params string[] parts) => Path.Combine([DataDirectory, .. parts]);

    /// <summary>
    /// Moves every store to <paramref name="directory"/>, then creates it. Called once, before anything
    /// reads state. The move stands even when creating throws (a read-only medium): the stores then
    /// fail to write there, rather than writing into a folder an update would replace.
    /// </summary>
    public static void UseDataDirectory(string directory)
    {
        _dataDirectory = directory;
        Directory.CreateDirectory(directory);
    }
}
