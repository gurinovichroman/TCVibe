namespace TCV.App.Selection;

/// <summary>One row as a <see cref="SelectionRule"/> sees it.</summary>
/// <param name="LastModified">Any <see cref="DateTimeKind"/>: FTP lists in UTC, the disk in local time (TRAP-48). <c>default</c> means unknown.</param>
/// <param name="GitChanged">Git reports the entry modified, staged, untracked or in conflict.</param>
/// <param name="AgentChanged">The project's verdicts say an agent changed it.</param>
public readonly record struct SelectionCandidate(
    string Name, bool IsDirectory, DateTime LastModified, bool GitChanged = false, bool AgentChanged = false);

/// <summary>
/// A <see cref="SelectionTemplate"/> compiled against one moment: the mask, then every condition the
/// template sets, all of which must hold.
/// </summary>
public sealed class SelectionRule
{
    private readonly SelectionMask _mask;
    private readonly bool _includeFolders;
    private readonly SelectionAge _age;
    private readonly DateTime _boundaryUtc;
    private readonly bool _gitChanged;
    private readonly bool _agentChanged;

    private SelectionRule(SelectionTemplate template, DateTime nowUtc)
    {
        _mask = SelectionMask.Parse(template.Mask);
        _includeFolders = template.IncludeFolders;
        _age = template.AgeAmount > 0 ? template.Age : SelectionAge.Any;
        _boundaryUtc = _age == SelectionAge.Any ? default : nowUtc - Span(template.AgeAmount, template.AgeUnit);
        _gitChanged = template.GitChanged;
        _agentChanged = template.AgentChanged;
    }

    /// <summary>Compiles <paramref name="template"/>; the age counts back from <paramref name="nowUtc"/>.</summary>
    public static SelectionRule From(SelectionTemplate template, DateTime nowUtc) =>
        new(template, nowUtc.Kind == DateTimeKind.Utc ? nowUtc : nowUtc.ToUniversalTime());

    /// <summary>True when the mask names nothing, so the rule can match nothing.</summary>
    public bool IsEmpty => _mask.IsEmpty;

    /// <summary>The rule asks Git, so the caller has to fetch the folder's status first.</summary>
    public bool NeedsGit => _gitChanged;

    public bool Matches(in SelectionCandidate entry)
    {
        if (!_mask.Matches(entry.Name, entry.IsDirectory, _includeFolders))
        {
            return false;
        }
        if (_gitChanged && !entry.GitChanged)
        {
            return false;
        }
        if (_agentChanged && !entry.AgentChanged)
        {
            return false;
        }

        if (_age != SelectionAge.Any)
        {
            // An unknown time is neither old nor new: an age rule that guessed would mark files to delete.
            if (entry.LastModified == default)
            {
                return false;
            }

            var modified = entry.LastModified.ToUniversalTime();
            return _age == SelectionAge.OlderThan ? modified < _boundaryUtc : modified >= _boundaryUtc;
        }

        return true;
    }

    // Capped at 1000 years: a typed 2000000000 days would overflow TimeSpan and DateTime.
    private const long MaxMinutes = 1000L * 366 * 24 * 60;

    private static TimeSpan Span(int amount, SelectionAgeUnit unit)
    {
        long perUnit = unit switch
        {
            SelectionAgeUnit.Minutes => 1,
            SelectionAgeUnit.Hours => 60,
            _ => 24 * 60,
        };
        return TimeSpan.FromMinutes(Math.Min((long)amount * perUnit, MaxMinutes));
    }

}
