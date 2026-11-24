namespace TCV.App.Selection;

/// <summary>Which way <see cref="SelectionTemplate.Age"/> bounds the modification time.</summary>
public enum SelectionAge
{
    /// <summary>No bound on the age.</summary>
    Any,

    /// <summary>Modified longer ago than the amount: the old files of <c>%TEMP%</c>.</summary>
    OlderThan,

    /// <summary>Modified within the amount: what changed in the last hour.</summary>
    NewerThan,
}

public enum SelectionAgeUnit { Minutes, Hours, Days }

/// <summary>
/// A named way to mark files (Num+ and its templates): a mask plus optional conditions on the age,
/// the Git state and the agent that changed the file. Serialized as-is into <c>settings.json</c>.
/// </summary>
/// <remarks>
/// A shipped template has a <see cref="Builtin"/> id and no <see cref="Name"/>: its name comes from the
/// dictionary (<c>SelTpl_&lt;Builtin&gt;</c>), so it reads in the interface language. Saving one under
/// a name of its own turns it into an ordinary template.
/// </remarks>
public sealed class SelectionTemplate
{
    /// <summary>The user's name for the template; empty for a shipped one, which takes <see cref="Builtin"/>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The id of a shipped template (<c>Code</c>, <c>Temp</c>…); empty for the user's own.</summary>
    public string Builtin { get; set; } = string.Empty;

    /// <summary>The mask in <see cref="SelectionMask"/> syntax: <c>*.cs;*.md | *.g.cs</c>, <c>bin\</c>.</summary>
    public string Mask { get; set; } = "*";

    /// <summary>Plain masks match folders as well as files (a mask ending in <c>\</c> always matches folders).</summary>
    public bool IncludeFolders { get; set; }

    public SelectionAge Age { get; set; }
    public int AgeAmount { get; set; } = 2;
    public SelectionAgeUnit AgeUnit { get; set; } = SelectionAgeUnit.Days;

    /// <summary>Only what Git reports as changed: modified, staged, untracked or in conflict.</summary>
    public bool GitChanged { get; set; }

    /// <summary>Only what an agent changed, by the project's «Agent changes» verdicts.</summary>
    public bool AgentChanged { get; set; }

    /// <summary>The template's own gesture (<c>Alt+Shift+1</c>); it only gets one nothing else owns.</summary>
    public string? Hotkey { get; set; }

    /// <summary>True when the template needs to know who changed what, which only a disk folder can say.</summary>
    public bool NeedsDisk => GitChanged || AgentChanged;

    /// <summary>The identity used for lookups and the palette id: the shipped id, else the name.</summary>
    public string Key => Name.Length > 0 ? Name : Builtin;

    public SelectionTemplate Clone() => (SelectionTemplate)MemberwiseClone();

    /// <summary>True when the two select the same things (the name and the gesture aside).</summary>
    public bool SameRule(SelectionTemplate other) =>
        string.Equals(Mask.Trim(), other.Mask.Trim(), StringComparison.OrdinalIgnoreCase)
        && IncludeFolders == other.IncludeFolders
        && Age == other.Age
        && (Age == SelectionAge.Any || (AgeAmount == other.AgeAmount && AgeUnit == other.AgeUnit))
        && GitChanged == other.GitChanged
        && AgentChanged == other.AgentChanged;

    /// <summary>
    /// What a fresh portable copy offers: the file kinds a developer marks most, the leftovers to clean
    /// up, and the three questions this file manager can answer that others cannot — what Git, an agent
    /// or the last day changed. A new list each call: the caller owns and edits it.
    /// </summary>
    public static List<SelectionTemplate> CreateBuiltins() =>
    [
        new() { Builtin = "Code", Mask = "*.cs;*.xaml;*.ts;*.tsx;*.js;*.jsx;*.mjs;*.py;*.go;*.rs;*.java;*.kt;*.c;*.cpp;*.h;*.hpp;*.swift;*.rb;*.php;*.sql;*.sh;*.ps1" },
        new() { Builtin = "Config", Mask = "*.json;*.jsonc;*.yml;*.yaml;*.toml;*.ini;*.xml;*.config;*.props;*.targets;*.csproj;*.sln;*.slnx;.editorconfig;.gitignore;.gitattributes;.env*" },
        new() { Builtin = "Docs", Mask = "*.md;*.txt;*.rst;*.adoc;*.pdf;*.doc;*.docx;*.odt;*.rtf" },
        new() { Builtin = "Images", Mask = "*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp;*.svg;*.ico;*.tif;*.tiff;*.heic;*.avif" },
        new() { Builtin = "Archives", Mask = "*.zip;*.7z;*.rar;*.tar;*.gz;*.tgz;*.bz2;*.xz;*.zst;*.cab;*.iso" },
        new() { Builtin = "Junk", Mask = "*.tmp;*.temp;*.bak;*.old;*.log;*.swp;*~;~$*;*.dmp" },
        new() { Builtin = "BuildOutput", Mask = @"bin\;obj\;node_modules\;dist\;build\;out\;target\;.vs\;__pycache__\;.next\" },
        new() { Builtin = "GitChanged", Mask = "*", GitChanged = true },
        new() { Builtin = "AgentChanged", Mask = "*", AgentChanged = true },
        new() { Builtin = "LastDay", Mask = "*", Age = SelectionAge.NewerThan, AgeAmount = 24, AgeUnit = SelectionAgeUnit.Hours },
        new() { Builtin = "OlderThan2Days", Mask = "*", IncludeFolders = true, Age = SelectionAge.OlderThan, AgeAmount = 2, AgeUnit = SelectionAgeUnit.Days },
    ];
}
