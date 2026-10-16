namespace TCV.App.Operations;

/// <summary>
/// How far an operation that counts items rather than bytes has got — a delete, where the size of
/// what goes away says nothing about how long it takes (6.36). <see cref="Total"/> is 0 while the
/// operation is still finding out how many items there are.
/// </summary>
public sealed record ItemProgress(int Done, int Total, string CurrentItem, TimeSpan Elapsed)
{
    public double Percentage => Total > 0 ? Math.Min(100.0, (double)Done / Total * 100.0) : 0;

    /// <summary>"120 / 5,000": what the progress window shows where a copy shows its bytes.</summary>
    public string CountText => Total > 0 ? $"{Done:N0} / {Total:N0}" : string.Empty;

    /// <summary>Time left at the pace so far; null until there is a pace to go by.</summary>
    public TimeSpan? Remaining =>
        Done > 0 && Done < Total && Elapsed > TimeSpan.Zero
            ? TimeSpan.FromSeconds((Total - Done) * Elapsed.TotalSeconds / Done)
            : null;
}
