using TCV.App.Settings;

namespace TCV.App.Commands;

/// <summary>A palette-visible command: the id routed through <see cref="MainWindow"/>'s dispatcher,
/// with a localised title and category.</summary>
public sealed record CommandDescriptor(
    string Id, string TitleEn, string TitleRu, string CategoryEn, string CategoryRu)
{
    public string Title(string lang) => lang == "ru" ? TitleRu : TitleEn;
    public string Category(string lang) => lang == "ru" ? CategoryRu : CategoryEn;
}

/// <summary>A ready-to-show palette row: localised title/category plus the current key gesture.</summary>
/// <param name="Rank">Breaks ties in the ranking, higher first: the recent connections lead an empty query, the most recent on top.</param>
public sealed record CommandPaletteItem(string Id, string Title, string Category, string Gesture, int Rank = 0);

/// <summary>
/// Static registry of the commands the command palette can run. Ids match the cases in
/// <c>MainWindow.TryExecuteCommand</c>; gestures are resolved live from <see cref="HotkeyManager"/>
/// so a rebind shows through immediately. Titles are kept here (rather than in the XAML string
/// dictionaries) because a command registry is code-side data, and this keeps the palette
/// self-contained and easy to extend.
/// </summary>
public static class CommandCatalog
{
    private const string CatFiles = "Files";
    private const string CatFilesRu = "Файлы";
    private const string CatView = "View";
    private const string CatViewRu = "Просмотр";
    private const string CatSelection = "Selection";
    private const string CatSelectionRu = "Выделение";
    private const string CatClipboard = "Clipboard";
    private const string CatClipboardRu = "Буфер обмена";
    private const string CatNavigation = "Navigation";
    private const string CatNavigationRu = "Навигация";
    private const string CatTabs = "Tabs";
    private const string CatTabsRu = "Вкладки";
    private const string CatTools = "Tools";
    private const string CatToolsRu = "Инструменты";
    private const string CatGit = "Git";
    private const string CatGitRu = "Git";
    private const string CatNetwork = "Network";
    private const string CatNetworkRu = "Сеть";
    private const string CatApp = "App";
    private const string CatAppRu = "Программа";
    private const string CatAgent = "Agent layer";
    private const string CatAgentRu = "Агентский слой";

    public static IReadOnlyList<CommandDescriptor> All { get; } = new CommandDescriptor[]
    {
        // ── Files ─────────────────────────────────────────────────────────────
        new("Copy",            "Copy",                    "Копировать",              CatFiles, CatFilesRu),
        new("Move",            "Move",                    "Переместить",             CatFiles, CatFilesRu),
        new("NewFolder",       "New folder",              "Создать папку",           CatFiles, CatFilesRu),
        new("NewFile",         "New file…",               "Создать файл…",           CatFiles, CatFilesRu),
        new("Delete",          "Delete",                  "Удалить",                 CatFiles, CatFilesRu),
        new("DeletePermanent", "Delete permanently",      "Удалить безвозвратно",    CatFiles, CatFilesRu),
        new("Rename",          "Rename…",                  "Переименовать…",           CatFiles, CatFilesRu),
        new("Pack",            "Pack…",                   "Упаковать…",              CatFiles, CatFilesRu),
        new("Unpack",          "Unpack…",                 "Распаковать…",            CatFiles, CatFilesRu),

        // ── View / open ───────────────────────────────────────────────────────
        new("OpenFile",        "Open",                    "Открыть",                 CatView, CatViewRu),
        new("View",            "View",                    "Просмотр",                CatView, CatViewRu),
        new("Edit",            "Edit",                    "Правка",                  CatView, CatViewRu),
        new("Properties",      "Properties",              "Свойства",                CatView, CatViewRu),
        new("CalculateFolderSizes", "Folder sizes",       "Размеры папок",           CatView, CatViewRu),

        // ── Selection ─────────────────────────────────────────────────────────
        new("SelectToggle",    "Mark / unmark and move down", "Отметить / снять и вниз", CatSelection, CatSelectionRu),
        new("SelectToggleSize", "Mark / unmark, count folder size and move down", "Отметить / снять, посчитать размер папки и вниз", CatSelection, CatSelectionRu),
        new("SelectAll",       "Select all",              "Выделить всё",            CatSelection, CatSelectionRu),
        new("DeselectAll",     "Deselect all",            "Снять выделение",         CatSelection, CatSelectionRu),
        new("InvertSelection", "Invert selection",        "Инвертировать выделение", CatSelection, CatSelectionRu),
        new("SelectByMask",    "Select by mask…",         "Выделить по маске…",      CatSelection, CatSelectionRu),
        new("UnselectByMask",  "Deselect by mask…",       "Снять по маске…",         CatSelection, CatSelectionRu),

        // ── Clipboard ─────────────────────────────────────────────────────────
        new("ClipboardCopy",   "Copy to clipboard",       "Копировать в буфер",      CatClipboard, CatClipboardRu),
        new("ClipboardCut",    "Cut to clipboard",        "Вырезать в буфер",        CatClipboard, CatClipboardRu),
        new("ClipboardPaste",  "Paste from clipboard",    "Вставить из буфера",      CatClipboard, CatClipboardRu),
        new("CopyFullPath",    "Copy full path",          "Копировать полный путь",        CatClipboard, CatClipboardRu),
        new("CopyNames",       "Copy names",              "Копировать имена",       CatClipboard, CatClipboardRu),
        new("CopyReadLink",    "Copy link to file (1 hour)", "Копировать ссылку на файл (1 час)", CatClipboard, CatClipboardRu),

        // ── Navigation & panels ───────────────────────────────────────────────
        new("NavigateUp",      "Go up",                   "На уровень вверх",        CatNavigation, CatNavigationRu),
        new("NavigateBack",    "Back",                    "Назад",                   CatNavigation, CatNavigationRu),
        new("NavigateForward", "Forward",                 "Вперёд",                  CatNavigation, CatNavigationRu),
        new("GoToRoot",        "Go to root",              "В корневой каталог",                CatNavigation, CatNavigationRu),
        new("GoHome",          "Go to home folder",       "В домашнюю папку",          CatNavigation, CatNavigationRu),
        new("GoToClipboardPath", "Go to path from clipboard", "Перейти по пути из буфера", CatNavigation, CatNavigationRu),
        new("EditPath",        "Edit path",               "Изменить путь",           CatNavigation, CatNavigationRu),
        new("OpenInExplorer",  "Open in Explorer",        "Открыть в проводнике",    CatNavigation, CatNavigationRu),
        new("SwitchPanel",     "Switch panel",            "Переключить панель",      CatNavigation, CatNavigationRu),
        new("ToggleSplit",     "Toggle split orientation","Ориентация панелей",      CatNavigation, CatNavigationRu),
        new("SwapPanels",      "Swap panels",             "Поменять панели местами", CatNavigation, CatNavigationRu),
        new("FindFiles",       "Find file in project…",   "Найти файл в проекте…",   CatNavigation, CatNavigationRu),
        new("QuickFilter",     "Quick filter",            "Быстрый фильтр",          CatNavigation, CatNavigationRu),
        new("NameSearch",      "Find file anywhere…",     "Найти файл на диске…",    CatNavigation, CatNavigationRu),
        new("RebuildNameIndex","Rebuild the name index",  "Перестроить индекс имён", CatNavigation, CatNavigationRu),
        new("FolderTree",      "Toggle folder tree",      "Показать/скрыть дерево папок", CatNavigation, CatNavigationRu),
        new("ToggleIgnored",   "Hide gitignored files",   "Скрывать gitignored-файлы", CatView, CatViewRu),
        new("ToggleHiddenFiles", "Show hidden files",     "Показывать скрытые файлы", CatView, CatViewRu),
        new("ToggleExtensions", "Show file extensions",   "Показывать расширения",   CatView, CatViewRu),
        new("ToggleFoldersFirst", "Folders first",        "Папки сверху",            CatView, CatViewRu),
        new("ColumnSettings",  "Columns…",                "Колонки…",                CatView, CatViewRu),
        new("ToggleTheme",     "Toggle light / dark theme", "Светлая / тёмная тема", CatView, CatViewRu),
        new("ViewModeDetails", "View: details",           "Вид: подробный",          CatView, CatViewRu),
        new("ViewModeBrief",   "View: brief",             "Вид: краткий",            CatView, CatViewRu),
        new("ViewModeThumbnails", "View: thumbnails",     "Вид: эскизы",             CatView, CatViewRu),
        new("ViewModeTree",    "View: tree",              "Вид: дерево",             CatView, CatViewRu),

        // ── Tabs ──────────────────────────────────────────────────────────────
        new("NewTab",          "New tab",                 "Новая вкладка",           CatTabs, CatTabsRu),
        new("CloseTab",        "Close tab",               "Закрыть вкладку",         CatTabs, CatTabsRu),
        new("NextTab",         "Next tab",                "Следующая вкладка",       CatTabs, CatTabsRu),
        new("PreviousTab",     "Previous tab",            "Предыдущая вкладка",      CatTabs, CatTabsRu),
        new("RefreshTab",      "Refresh",                 "Обновить",                CatTabs, CatTabsRu),
        new("DuplicateTab",    "Duplicate tab",           "Дублировать вкладку",     CatTabs, CatTabsRu),
        new("CloseOtherTabs",  "Close other tabs",        "Закрыть остальные вкладки", CatTabs, CatTabsRu),
        new("ToggleLockTab",   "Lock / unlock tab",       "Закрепить / открепить вкладку", CatTabs, CatTabsRu),
        new("RenameTab",       "Rename tab",              "Переименовать вкладку",   CatTabs, CatTabsRu),

        // ── Tools ─────────────────────────────────────────────────────────────
        new("Search",          "Find files…",                 "Поиск файлов…",                  CatTools, CatToolsRu),
        new("MultiRename",     "Multi-rename…",           "Массовое переименование…",CatTools, CatToolsRu),
        new("OperationLog",    "Operation log…",          "Журнал операций…",        CatTools, CatToolsRu),
        new("AddBookmark",     "Add bookmark…",            "Добавить закладку…",       CatTools, CatToolsRu),
        new("BookmarksManager", "Manage bookmarks…",      "Управление закладками…",  CatTools, CatToolsRu),
        new("Terminal",        "Open terminal here",      "Открыть терминал здесь",  CatTools, CatToolsRu),
        new("ShellsManager",   "Command-line shells…",    "Оболочки командной строки…", CatTools, CatToolsRu),
        new("OpenInEditor",    "Open in editor",          "Открыть в редакторе",     CatTools, CatToolsRu),
        new("OpenInEditorPicker","Open in editor…",       "Открыть в редакторе…",    CatTools, CatToolsRu),
        new("RunAgentHere",    "Run agent here",          "Запустить агента здесь",  CatTools, CatToolsRu),
        new("RunAgentPicker",  "Run agent here…",         "Запустить агента здесь…", CatTools, CatToolsRu),
        new("RunGadget",       "Run a gadget…",           "Запустить гаджет…",       CatTools, CatToolsRu),
        new("GadgetsManager",  "Manage gadgets…",         "Управление гаджетами…",   CatTools, CatToolsRu),
        new("SavedOperations", "Saved operations…",       "Сохранённые операции…",   CatTools, CatToolsRu),
        new("SavedOperationsManager", "Manage saved operations…", "Управление сохранёнными операциями…", CatTools, CatToolsRu),
        new("DropStackAdd",    "Drop stack: add selection", "Стек: добавить выделенное", CatTools, CatToolsRu),
        new("DropStackShow",   "Drop stack…",             "Стек файлов…",            CatTools, CatToolsRu),
        new("CompareDirectories", "Compare directories…", "Сравнить каталоги…",      CatTools, CatToolsRu),
        new("Checksums",       "Checksums…",              "Контрольные суммы…",      CatTools, CatToolsRu),
        new("UserToolsManager", "User tools…",            "Пользовательские инструменты…", CatTools, CatToolsRu),
        new("EditRemoteFile",  "Edit remote file",        "Править удалённый файл",  CatNetwork, CatNetworkRu),

        // ── Git ───────────────────────────────────────────────────────────────
        new("GitDiff",         "Git: show diff",          "Git: показать diff",       CatGit, CatGitRu),
        new("GitWorktrees",    "Git: worktree dashboard…",         "Git: worktree-дэшборд…",   CatGit, CatGitRu),
        new("GitNewWorktree",  "Git: new worktree for a task…", "Git: новый worktree под задачу…", CatGit, CatGitRu),
        new("GitCommit",       "Git: commit…",            "Git: коммит…",             CatGit, CatGitRu),
        new("GitBranch",       "Git: branches…",          "Git: ветки…",              CatGit, CatGitRu),
        new("GitStage",        "Git: stage selection",    "Git: добавить в индекс",   CatGit, CatGitRu),
        new("GitUnstage",      "Git: unstage selection",  "Git: убрать из индекса",   CatGit, CatGitRu),
        new("GitStash",        "Git: stash…",             "Git: спрятать (stash)…",   CatGit, CatGitRu),
        new("GitStashPop",     "Git: stash pop",          "Git: вернуть из stash",    CatGit, CatGitRu),
        new("GitClone",        "Git: clone from URL…",    "Git: клонировать по ссылке…", CatGit, CatGitRu),

        // ── Agent-aware layer (6D) ────────────────────────────────────────────
        new("AgentChanges",    "Agent changes…",          "Изменения агента…",        CatAgent, CatAgentRu),
        new("AgentLaunch",     "Run an agent on a task…", "Запустить агента над задачей…", CatAgent, CatAgentRu),
        new("AiRename",        "AI rename…",              "ИИ-переименование…",       CatAgent, CatAgentRu),
        new("SemanticSearch",  "Semantic search…",        "Семантический поиск…",     CatAgent, CatAgentRu),

        // ── Network ───────────────────────────────────────────────────────────
        new("ConnectionManager","Connection manager…",    "Менеджер подключений…",    CatNetwork, CatNetworkRu),
        new("Disconnect",      "Disconnect",              "Отключиться",             CatNetwork, CatNetworkRu),
        new("Reconnect",       "Reconnect",               "Переподключиться",        CatNetwork, CatNetworkRu),
        new("QuickConnect",    "Quick connect by address…", "Быстрое подключение по адресу…", CatNetwork, CatNetworkRu),
        new("QueueTransfer",   "Add to transfer queue",          "В очередь передачи",      CatNetwork, CatNetworkRu),
        new("TransferQueue",   "Transfer queue…",         "Очередь передачи…",  CatNetwork, CatNetworkRu),

        // ── Workspaces ────────────────────────────────────────────────────────
        new("WorkspaceSwitcher","Switch workspace…",      "Переключить воркспейс…",  "Workspaces", "Воркспейсы"),
        new("NewWorkspace",    "New workspace…",          "Новый воркспейс…",        "Workspaces", "Воркспейсы"),
        new("SaveWorkspace",   "Save current as workspace…","Сохранить как воркспейс…","Workspaces", "Воркспейсы"),
        new("WorkspaceNotes",  "Workspace notes…",        "Заметки воркспейса…",     "Workspaces", "Воркспейсы"),
        new("WorkspaceTerminal","Run workspace terminal", "Терминал воркспейса",     "Workspaces", "Воркспейсы"),
        new("WorkspaceNewWindow","Open workspace in a new window","Открыть воркспейс в новом окне","Workspaces","Воркспейсы"),
        new("WorkspaceSidebar","Show/hide workspace sidebar","Показать/скрыть панель воркспейсов","Workspaces","Воркспейсы"),
        new("WorkspaceOpenDefinition","Open project file (.tcvproj)…","Открыть файл проекта (.tcvproj)…","Workspaces","Воркспейсы"),
        new("WorkspaceExportDefinition","Save project file (.tcvproj)…","Сохранить файл проекта (.tcvproj)…","Workspaces","Воркспейсы"),
        new("Workspace1", "Switch to workspace 1", "Перейти к воркспейсу 1", "Workspaces", "Воркспейсы"),
        new("Workspace2", "Switch to workspace 2", "Перейти к воркспейсу 2", "Workspaces", "Воркспейсы"),
        new("Workspace3", "Switch to workspace 3", "Перейти к воркспейсу 3", "Workspaces", "Воркспейсы"),
        new("Workspace4", "Switch to workspace 4", "Перейти к воркспейсу 4", "Workspaces", "Воркспейсы"),
        new("Workspace5", "Switch to workspace 5", "Перейти к воркспейсу 5", "Workspaces", "Воркспейсы"),
        new("Workspace6", "Switch to workspace 6", "Перейти к воркспейсу 6", "Workspaces", "Воркспейсы"),
        new("Workspace7", "Switch to workspace 7", "Перейти к воркспейсу 7", "Workspaces", "Воркспейсы"),
        new("Workspace8", "Switch to workspace 8", "Перейти к воркспейсу 8", "Workspaces", "Воркспейсы"),
        new("Workspace9", "Switch to workspace 9", "Перейти к воркспейсу 9", "Workspaces", "Воркспейсы"),
        new("WorkspaceProperties", "Workspace properties: name, icon, colour, group…", "Свойства воркспейса: имя, значок, цвет, группа…", "Workspaces", "Воркспейсы"),
        new("WorkspacePin",    "Pin / unpin workspace",   "Закрепить / открепить воркспейс", "Workspaces", "Воркспейсы"),
        new("WorkspaceDelete", "Delete workspace",       "Удалить воркспейс",      "Workspaces", "Воркспейсы"),

        // ── App ───────────────────────────────────────────────────────────────
        new("CommandPalette",  "Command palette…",        "Палитра команд…",         CatApp, CatAppRu),
        new("Help",            "Help: what every function does", "Справка: что делает каждая функция", CatApp, CatAppRu),
        new("Settings",        "Settings…",               "Настройки…",              CatApp, CatAppRu),
        new("HotkeySettings",  "Keyboard shortcuts…",     "Горячие клавиши…",        CatApp, CatAppRu),
        new("PluginManager",   "Plugin manager…",         "Менеджер плагинов…",      CatApp, CatAppRu),
        new("CheckForUpdates", "Check for updates…",      "Проверить обновления…",   CatApp, CatAppRu),
        new("ReportBug",       "Report a bug…",           "Сообщить об ошибке…",     CatApp, CatAppRu),
        new("Discussions",     "Questions and ideas…",    "Вопросы и идеи…",         CatApp, CatAppRu),
        new("OpenLogFolder",   "Open the log folder",     "Открыть папку логов",     CatApp, CatAppRu),
        new("ExportSettings",  "Export all settings…",    "Экспорт всех настроек…",  CatApp, CatAppRu),
        new("ImportSettings",  "Import all settings…",    "Импорт всех настроек…",   CatApp, CatAppRu),
        new("Quit",            "Exit",                    "Выход",                   CatApp, CatAppRu),
        new("Menu",            "Open the menu bar",       "Открыть меню",            CatApp, CatAppRu),
    };

    /// <summary>Builds palette rows for the given language, attaching each command's current gesture.</summary>
    public static IReadOnlyList<CommandPaletteItem> BuildItems(HotkeyManager hotkeys, string lang)
    {
        var items = new List<CommandPaletteItem>(All.Count);
        foreach (var d in All)
        {
            // What is printed on the keys ("Ctrl+/", "Num +"), as the menus show it — not the
            // stored WPF names ("Ctrl+OemQuestion", "Add").
            var gesture = GestureText.Format(hotkeys.GetBinding(d.Id));
            items.Add(new CommandPaletteItem(d.Id, d.Title(lang), d.Category(lang), gesture));
        }
        return items;
    }
}
