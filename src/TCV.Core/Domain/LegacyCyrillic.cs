using System.Text;

namespace TCV.Core.Domain;

/// <summary>
/// Tells the three legacy Russian code pages apart — Windows-1251, CP866 (DOS) and KOI8-R — by how
/// natural the text looks when decoded with each one.
/// </summary>
/// <remarks>
/// A file that is not UTF-8 and has no byte-order mark used to be shown as Latin-1, which turns every
/// old Russian text file into mojibake until the user finds the encoding box. The three pages assign
/// the same letters to different bytes, so the bytes alone cannot decide; the decoded letters can:
/// Russian text is dominated by <c>о е а и н т с р в л</c>, and decoded with the wrong page it is full
/// of box-drawing characters, capital letters and rare ones. Each candidate is scored by the summed
/// frequency of the letters it produces, and the winner must beat the runner-up clearly — a coin-toss
/// stays Latin-1. Pure, BCL-only (the code pages come from the built-in provider).
/// </remarks>
public static class LegacyCyrillic
{
    private const int MinHighBytes = 6;          // a couple of letters prove nothing
    private const double MinCyrillicShare = 0.6; // of the non-ASCII characters
    private const double MinMargin = 1.15;       // winner over runner-up
    // Natural Russian averages about 6 per letter; the same bytes read as a Latin-1 text (é -> й, ü -> ь)
    // or random bytes average about 3, which is what the bar keeps out.
    private const double MinMeanScore = 4.5;
    private const int SampleBytes = 64 * 1024;

    // Approximate letter frequencies of Russian text (per cent); lowercase letters.
    private static readonly Dictionary<char, double> Frequency = new()
    {
        ['о'] = 10.97, ['е'] = 8.45, ['а'] = 8.01, ['и'] = 7.35, ['н'] = 6.70, ['т'] = 6.26, ['с'] = 5.47,
        ['р'] = 4.75, ['в'] = 4.54, ['л'] = 4.34, ['к'] = 3.49, ['м'] = 3.21, ['д'] = 2.98, ['п'] = 2.81,
        ['у'] = 2.62, ['я'] = 2.01, ['ы'] = 1.90, ['ь'] = 1.74, ['г'] = 1.70, ['з'] = 1.65, ['б'] = 1.59,
        ['ч'] = 1.44, ['й'] = 1.21, ['х'] = 0.97, ['ж'] = 0.94, ['ш'] = 0.72, ['ю'] = 0.64, ['ц'] = 0.48,
        ['щ'] = 0.36, ['э'] = 0.33, ['ф'] = 0.27, ['ё'] = 0.20, ['ъ'] = 0.04,
    };

    private static readonly int[] CodePages = [1251, 866, 20866];

    static LegacyCyrillic()
    {
        // Windows code pages are not built in to modern .NET; the provider ships in the shared framework.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// The most likely legacy Russian encoding for <paramref name="data"/>, or null when the text does
    /// not look Russian enough (too few high bytes, or no candidate is clearly better).
    /// </summary>
    public static Encoding? TryDetect(ReadOnlySpan<byte> data)
    {
        var sample = data.Length > SampleBytes ? data[..SampleBytes] : data;

        var high = 0;
        foreach (var b in sample)
        {
            if (b >= 0x80)
            {
                high++;
            }
        }

        if (high < MinHighBytes)
        {
            return null;
        }

        Encoding? best = null;
        double bestScore = double.MinValue, secondScore = double.MinValue;

        foreach (var codePage in CodePages)
        {
            Encoding encoding;
            try { encoding = Encoding.GetEncoding(codePage); }
            catch (Exception) { continue; }   // provider missing on an odd platform: no guess

            var (score, share, mean) = Score(encoding.GetString(sample));
            if (share < MinCyrillicShare || mean < MinMeanScore)
            {
                continue;
            }

            if (score > bestScore)
            {
                secondScore = bestScore;
                bestScore = score;
                best = encoding;
            }
            else if (score > secondScore)
            {
                secondScore = score;
            }
        }

        if (best is null)
        {
            return null;
        }

        return secondScore <= 0 || bestScore / secondScore >= MinMargin ? best : null;
    }

    /// <summary>Summed letter frequency of the text, and the share of its non-ASCII characters that are Cyrillic.</summary>
    private static (double Score, double CyrillicShare, double MeanScore) Score(string text)
    {
        double score = 0;
        int nonAscii = 0, cyrillic = 0;

        foreach (var c in text)
        {
            if (c < 0x80)
            {
                continue;
            }

            nonAscii++;

            var lower = char.ToLowerInvariant(c);
            if (lower is >= 'а' and <= 'я' or 'ё')
            {
                cyrillic++;
                var weight = Frequency.GetValueOrDefault(lower, 0.1);
                // Russian is mostly lowercase: a capital counts for less, so text decoded with the page
                // that turns lowercase bytes into capitals (KOI8-R read as 1251) scores badly.
                score += char.IsUpper(c) ? weight * 0.35 : weight;
            }
            else
            {
                score -= 2.0;   // box drawing, Latin accents, symbols: the sign of the wrong page
            }
        }

        return nonAscii == 0
            ? (score, 0, 0)
            : (score, (double)cyrillic / nonAscii, score / nonAscii);
    }
}
