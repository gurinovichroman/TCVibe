using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Help;
using TCV.App.Localization;

namespace TCV.App.ViewModels.Dialogs;

/// <summary>An entry of the help's left-hand list: a guide article or a command category.</summary>
/// <param name="Group">The heading the entry is listed under ("Guide", "Commands").</param>
public sealed record HelpSection(string Key, string Title, HelpTopic? Topic, int Count, string Group)
{
    public bool IsTopic => Topic is not null;

    /// <summary>"12" next to a category; nothing next to an article.</summary>
    public string CountText => IsTopic ? string.Empty : Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// The help window: a guide (how the parts of the program fit together) and every command grouped
/// by category, with its live shortcut, where it sits in the menus, what it does and a button that
/// runs it. Typing searches all of it at once. The lists are built from the command registry
/// (<see cref="HelpCatalog"/>), so the help cannot describe a program that no longer exists.
/// </summary>
public partial class HelpDialogViewModel : ObservableObject
{
    private readonly IReadOnlyList<HelpCommand> _commands;
    private readonly Action<string>? _run;

    public HelpDialogViewModel(
        IReadOnlyList<HelpTopic> topics, IReadOnlyList<HelpCommand> commands, Action<string>? run)
    {
        _commands = commands;
        _run = run;

        var guide = LocalizationManager.Get("Help_GroupGuide");
        var commandsGroup = LocalizationManager.Get("Help_GroupCommands");
        TopicSections = topics.Select(t => new HelpSection("topic:" + t.Id, t.Title, t, 0, guide)).ToList();
        var categories = commands
            .GroupBy(c => c.CategoryKey, StringComparer.Ordinal)
            .Select(g => new HelpSection("cat:" + g.Key, g.First().Category, null, g.Count(), commandsGroup));
        Sections = [.. TopicSections, .. categories];

        _selectedSection = Sections.FirstOrDefault();
        Refilter();
    }

    /// <summary>The guide's articles, in reading order.</summary>
    public IReadOnlyList<HelpSection> TopicSections { get; }

    /// <summary>The left-hand list: the articles, then the command categories in the palette's order.</summary>
    public IReadOnlyList<HelpSection> Sections { get; }

    /// <summary>The section on the right when nothing is being searched.</summary>
    [ObservableProperty]
    private HelpSection? _selectedSection;

    [ObservableProperty]
    private string _query = string.Empty;

    /// <summary>The article shown on the right, or null.</summary>
    [ObservableProperty]
    private HelpTopic? _article;

    /// <summary>Articles that match the search (titles only, each opens on click).</summary>
    public ObservableCollection<HelpTopic> MatchingTopics { get; } = new();

    public ObservableCollection<HelpCommand> VisibleCommands { get; } = new();

    /// <summary>What the right-hand side is showing: the section's title, or the search.</summary>
    [ObservableProperty]
    private string _heading = string.Empty;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private bool _nothingFound;

    partial void OnSelectedSectionChanged(HelpSection? value)
    {
        // Picking a section is a way out of a search: the list on the left would otherwise point at
        // something the right-hand side is not showing.
        if (value is not null && Query.Length > 0)
        {
            Query = string.Empty;
            return;   // the query change refilters
        }

        Refilter();
    }

    partial void OnQueryChanged(string value) => Refilter();

    /// <summary>Opens an article found by the search.</summary>
    [RelayCommand]
    private void OpenTopic(HelpTopic? topic)
    {
        if (topic is null)
        {
            return;
        }

        var section = TopicSections.FirstOrDefault(s => s.Topic?.Id == topic.Id);
        if (section is not null)
        {
            Query = string.Empty;
            SelectedSection = section;
            Refilter();
        }
    }

    [RelayCommand]
    private void Run(HelpCommand? command)
    {
        if (command is not null)
        {
            _run?.Invoke(command.Id);
        }
    }

    private void Refilter()
    {
        MatchingTopics.Clear();
        VisibleCommands.Clear();

        var words = HelpSearch.Words(Query);
        IsSearching = words.Count > 0;

        if (IsSearching)
        {
            Article = null;
            Heading = Query.Trim();
            foreach (var topic in TopicSections.Select(s => s.Topic!).Where(t => HelpSearch.Matches(t, words)))
            {
                MatchingTopics.Add(topic);
            }

            foreach (var command in _commands.Where(c => HelpSearch.Matches(c, words)))
            {
                VisibleCommands.Add(command);
            }

            NothingFound = MatchingTopics.Count == 0 && VisibleCommands.Count == 0;
            return;
        }

        NothingFound = false;
        var section = SelectedSection;
        Heading = section?.Title ?? string.Empty;
        Article = section?.Topic;
        if (section is { IsTopic: false })
        {
            var key = section.Key["cat:".Length..];
            foreach (var command in _commands.Where(c => c.CategoryKey == key))
            {
                VisibleCommands.Add(command);
            }
        }
    }
}
