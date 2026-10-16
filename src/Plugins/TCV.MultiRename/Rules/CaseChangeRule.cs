using System.Globalization;

namespace TCV.MultiRename.Rules;

/// <summary>The type of case transformation to apply.</summary>
public enum CaseChangeMode
{
    /// <summary>Convert all characters to upper case.</summary>
    Uppercase,

    /// <summary>Convert all characters to lower case.</summary>
    Lowercase,

    /// <summary>Capitalise the first letter of each word.</summary>
    TitleCase,
}

/// <summary>Changes the case of the entire file name (including extension).</summary>
public sealed class CaseChangeRule : IRenameRule
{
    private readonly CaseChangeMode _mode;

    public CaseChangeRule(CaseChangeMode mode)
    {
        _mode = mode;
    }

    public string Description => _mode switch
    {
        CaseChangeMode.Uppercase => "UPPERCASE",
        CaseChangeMode.Lowercase => "lowercase",
        CaseChangeMode.TitleCase => "Title Case",
        _ => _mode.ToString(),
    };

    public string Apply(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;

        return _mode switch
        {
            CaseChangeMode.Uppercase => name.ToUpperInvariant(),
            CaseChangeMode.Lowercase => name.ToLowerInvariant(),
            CaseChangeMode.TitleCase => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(name.ToLowerInvariant()),
            _ => name,
        };
    }
}
