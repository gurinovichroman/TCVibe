using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace TCV.App;

/// <summary>
/// The window's own frame (ADR-0030): caption buttons, the maximised overhang, and the chrome's few
/// handlers that are not commands (the rail's workspace menu, the command line's shell box and keys).
/// </summary>
public partial class MainWindow
{
    private void InitializeChrome()
    {
        StateChanged += (_, _) => ApplyWindowState();
        SourceInitialized += (_, _) => ApplyWindowState();
        // Agents edit files while this window is in the background: look at the project again on return.
        Activated += (_, _) => _viewModel.RefreshProjectStatus();
        // The list of background operations hangs from the indicator's right edge: the indicator sits at
        // the right of the status line, and a list running off to the right would leave the window.
        BackgroundButton.SizeChanged += (_, _) =>
            BackgroundList.HorizontalOffset = BackgroundButton.ActualWidth - BackgroundListCard.Width;
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void OnCloseWindowClick(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    /// <summary>
    /// A maximised WindowChrome window is as large as the screen plus its invisible frame, so its edges
    /// would be cut off (TRAP-69). The frame is measured (window rectangle against the monitor's work
    /// area) and given back as padding; restored, the 1px outline returns.
    /// </summary>
    private void ApplyWindowState()
    {
        MaximizeIcon.Data = (System.Windows.Media.Geometry)FindResource(
            WindowState == WindowState.Maximized ? "IconCopy" : "IconSquare");

        if (WindowState != WindowState.Maximized)
        {
            WindowRoot.BorderThickness = new Thickness(1);
            WindowRoot.Padding = new Thickness(0);
            return;
        }

        WindowRoot.BorderThickness = new Thickness(0);
        WindowRoot.Padding = MaximizedOverhang();
    }

    private Thickness MaximizedOverhang()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var window))
        {
            return new Thickness(7);
        }

        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return new Thickness(7);
        }

        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var work = info.Work;
        return new Thickness(
            Math.Max(0, work.Left - window.Left) / dpi.DpiScaleX,
            Math.Max(0, work.Top - window.Top) / dpi.DpiScaleY,
            Math.Max(0, window.Right - work.Right) / dpi.DpiScaleX,
            Math.Max(0, window.Bottom - work.Bottom) / dpi.DpiScaleY);
    }

    /// <summary>A rail or side-bar button that runs a command by id (its Tag), like the F-key row.</summary>
    private void OnRunCommandClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id })
        {
            RunCommand(id);
        }
    }

    /// <summary>
    /// A right-click on a rail workspace offers what the Workspaces menu offers for it. Built on every
    /// opening, as that menu is: the pin mark, the notes dot and the shortcut follow the workspace.
    /// </summary>
    private void OnRailWorkspaceContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Workspaces.Workspace ws } button)
        {
            return;
        }

        var menu = new System.Windows.Controls.ContextMenu
        {
            PlacementTarget = button,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Right,
        };
        AddWorkspaceActions(menu.Items, ws);
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>
    /// The workspace box in the title bar drops the workspaces down under itself, the active one checked:
    /// a click switches. Built on every opening, as the Workspaces menu is, so names, icons and the Alt+N
    /// shortcuts follow; the searchable switcher, «new workspace» and «save as workspace» close the list.
    /// </summary>
    private void OnWorkspaceBoxClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement box)
        {
            return;
        }

        var commands = _viewModel.Workspaces;
        var menu = new System.Windows.Controls.ContextMenu
        {
            PlacementTarget = box,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            VerticalOffset = 3,
            MinWidth = box.ActualWidth,
        };

        var list = commands.Items;
        for (var i = 0; i < list.Count; i++)
        {
            var ws = list[i];
            menu.Items.Add(new System.Windows.Controls.MenuItem
            {
                Header           = Controls.MenuHeaderText.Literal(ws.DisplayName),
                IsChecked        = ws.Name.Equals(commands.ActiveWorkspaceName, StringComparison.OrdinalIgnoreCase),
                InputGestureText = WorkspaceGesture(i),
                Command          = commands.SwitchWorkspaceCommand,
                CommandParameter = ws,
            });
        }

        if (list.Count > 0)
        {
            menu.Items.Add(new System.Windows.Controls.Separator());
            var switcher = new System.Windows.Controls.MenuItem
            {
                Header           = Localization.LocalizationManager.Get("Menu_Ws_Switch"),
                InputGestureText = GestureFor("WorkspaceSwitcher"),
            };
            switcher.Click += (_, _) => OpenWorkspaceSwitcher();
            menu.Items.Add(switcher);
        }

        menu.Items.Add(new System.Windows.Controls.MenuItem
        {
            Header           = Localization.LocalizationManager.Get("Menu_Ws_New"),
            InputGestureText = GestureFor("NewWorkspace"),
            Command          = commands.NewWorkspaceCommand,
        });
        menu.Items.Add(new System.Windows.Controls.MenuItem
        {
            Header           = Localization.LocalizationManager.Get("Menu_Ws_Save"),
            InputGestureText = GestureFor("SaveWorkspace"),
            Command          = commands.SaveWorkspaceCommand,
        });
        menu.IsOpen = true;
    }

    /// <summary>
    /// Enter runs the line; Esc clears it and gives the keyboard back to the list; Alt+↓ drops the shells
    /// down, as it opens a combo box (Alt chords arrive as <c>SystemKey</c>, TRAP-01).
    /// </summary>
    private void OnCommandLineKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return && Keyboard.Modifiers == ModifierKeys.None)
        {
            // While the last line is still starting, the box is already empty: a second Enter would
            // open the shell itself on top of it.
            if (_viewModel.RunCommandLineCommand.CanExecute(null))
            {
                _viewModel.RunCommandLineCommand.Execute(null);
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _viewModel.CommandText = string.Empty;
            FocusActivePanelList();
            e.Handled = true;
        }
        else if (e.Key == Key.System && e.SystemKey == Key.Down && Keyboard.Modifiers == ModifierKeys.Alt)
        {
            _ = OpenShellMenuAsync();
            e.Handled = true;
        }
    }

    private void OnShellBoxClick(object sender, RoutedEventArgs e) => _ = OpenShellMenuAsync();

    /// <summary>
    /// The shell box drops the shells down above itself, the one in use checked; a pick is where the next
    /// command runs, and the keyboard goes to the line to type it. Built on every opening: the list is
    /// the settings' or this machine's, and either may have changed. «Configure shells…» closes it.
    /// </summary>
    private async Task OpenShellMenuAsync()
    {
        var shells = await _viewModel.GetShellsAsync();
        var current = Shells.ShellCatalog.Pick(shells, _viewModel.ShellName);
        var menu = new System.Windows.Controls.ContextMenu
        {
            PlacementTarget = ShellBox,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Top,
            VerticalOffset = -3,
            MinWidth = ShellBox.ActualWidth,
        };

        var picked = false;
        foreach (var shell in shells)
        {
            var item = new System.Windows.Controls.MenuItem
            {
                Header    = Controls.MenuHeaderText.Literal(shell.Name),
                IsChecked = ReferenceEquals(shell, current),
                ToolTip   = shell.Command,
            };
            item.Click += (_, _) =>
            {
                _viewModel.SelectShellCommand.Execute(shell);
                picked = true;
            };
            menu.Items.Add(item);
        }

        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(new System.Windows.Controls.MenuItem
        {
            Header  = Controls.MenuHeaderText.Literal(Localization.LocalizationManager.Format("Shell_OpenShell", current.Name)),
            Command = _viewModel.OpenShellCommand,
        });
        menu.Items.Add(new System.Windows.Controls.MenuItem
        {
            Header           = Localization.LocalizationManager.Get("Shell_ShellsManage"),
            InputGestureText = GestureFor("ShellsManager"),
            Command          = _viewModel.Services.OpenShellsManagerCommand,
        });

        // After the menu has given the keyboard back to where it was, or the line would lose it again.
        menu.Closed += (_, _) =>
        {
            if (picked)
            {
                Dispatcher.BeginInvoke(FocusCommandLine, System.Windows.Threading.DispatcherPriority.Input);
            }
        };
        menu.IsOpen = true;
    }

    private void FocusCommandLine()
    {
        CommandLineBox.Focus();
        CommandLineBox.CaretIndex = CommandLineBox.Text.Length;
    }

    private void FocusActivePanelList()
    {
        var view = _viewModel.LeftPanel.IsActive ? LeftPanelView : RightPanelView;
        view.FileListView.FocusList();
    }

    // ── Win32: where the maximised window really is ──────────────────────────

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
