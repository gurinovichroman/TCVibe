namespace TCV.App.Tests;

/// <summary>What a paired measurement judged: the two floors, the verdict, and the rounds it took.</summary>
internal sealed record PairedMeasurement(bool Fits, double YardstickMs, double SubjectMs, int Rounds);

/// <summary>
/// Times a piece of work against a yardstick so that the verdict is about the work, not about what
/// else the machine is doing (TRAP-112).
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>Yardstick and subject <b>alternate</b>, so both meet the same machine. Timed as two series
///   one after the other, a burst lands on one of them only, and it reads as a regression.</item>
///   <item>Each is judged by its <b>floor</b>, the best of the last few rounds. Load only ever adds
///   time, and a regression is paid by every run, so a floor that fits is proof and a median is not.
///   The window slides: a burst that started halfway through the first round is out of it soon.</item>
///   <item>A window that does not fit is <b>measured again</b> until the rounds run out. Noise passes;
///   a regression fails every window, so repeating cannot let it through.</item>
/// </list>
/// The yardstick must be about as long as the subject: see <see cref="ListingBudget.WalksPerYardstick"/>.
/// </remarks>
internal static class PairedBenchmark
{
    /// <param name="yardstickMs">Times one run of the yardstick.</param>
    /// <param name="subjectMs">Times one run of the work being judged.</param>
    /// <param name="fits">The verdict on a window: yardstick floor, subject floor.</param>
    /// <param name="window">Rounds a verdict rests on; also the fewest rounds there can be, so that one
    /// slow yardstick cannot buy the subject a pass on the first round.</param>
    /// <param name="maxRounds">Rounds after which a window that does not fit is the answer.</param>
    /// <param name="betweenRounds">Runs after a window that did not fit, to let a burst pass.</param>
    public static PairedMeasurement Run(
        Func<double> yardstickMs,
        Func<double> subjectMs,
        Func<double, double, bool> fits,
        int window,
        int maxRounds,
        Action? betweenRounds = null)
    {
        var yardstick = new Queue<double>();
        var subject = new Queue<double>();
        for (var round = 1; ; round++)
        {
            Remember(yardstick, yardstickMs(), window);
            Remember(subject, subjectMs(), window);
            if (round < window)
            {
                continue;
            }

            var yardstickFloor = yardstick.Min();
            var subjectFloor = subject.Min();
            var verdict = fits(yardstickFloor, subjectFloor);
            if (verdict || round >= maxRounds)
            {
                return new PairedMeasurement(verdict, yardstickFloor, subjectFloor, round);
            }

            betweenRounds?.Invoke();
        }
    }

    private static void Remember(Queue<double> samples, double sample, int window)
    {
        samples.Enqueue(sample);
        if (samples.Count > window)
        {
            samples.Dequeue();
        }
    }
}
