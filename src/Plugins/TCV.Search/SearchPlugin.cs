using TCV.Core.PluginSdk;

namespace TCV.Search;

/// <summary>
/// Search tool plugin. The actual UI is handled by TCV.App (SearchDialog).
/// This plugin registers the tool and exposes the SearchEngine.
/// </summary>
public sealed class SearchPlugin : IToolPlugin
{
    public string Id => "tcv.search";
    public string Name => "Search";
    public Version Version => new(2, 0, 0);
    public string MenuTitle => "Поиск файлов";
    public HotkeyGesture? DefaultHotkey => new("F", Ctrl: true);

    public void Initialize(IPluginHost host) { }

    /// <summary>
    /// Called by the app when the user triggers search (Ctrl+F or menu).
    /// The app intercepts this to show the SearchDialog.
    /// Setting the SearchRequested event allows TCV.App to hook in without circular deps.
    /// </summary>
    public event EventHandler<SearchRequestedEventArgs>? SearchRequested;

    public void Execute(IToolContext context)
    {
        SearchRequested?.Invoke(this, new SearchRequestedEventArgs(context));
    }

    /// <summary>Creates a SearchEngine for the given virtual file system.</summary>
    public SearchEngine CreateEngine(TCV.Core.Interfaces.IVirtualFileSystem vfs) =>
        new SearchEngine(vfs);
}

public sealed class SearchRequestedEventArgs : EventArgs
{
    public IToolContext Context { get; }
    public SearchRequestedEventArgs(IToolContext context) => Context = context;
}
