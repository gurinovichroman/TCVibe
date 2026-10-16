using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using TCV.App.Localization;
using TCV.App.ViewModels;
using TCV.Network;

namespace TCV.App.Controls;

/// <summary>
/// The drive strip of a panel: one button per drive, then the server and archive sources, the file
/// system's name and Refresh. Its data context is the panel (<see cref="PanelViewModel"/>).
/// </summary>
public partial class DriveBar : UserControl
{
    public DriveBar()
    {
        InitializeComponent();
    }

    private static readonly TimeSpan HoverRefreshInterval = TimeSpan.FromSeconds(10);
    private DateTime _lastHoverRefresh;

    /// <summary>The list may have changed since the last notice (a card into a reader); hovering the strip reads it again.</summary>
    private void OnMouseEnterStrip(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // Not on every pass of the pointer: a read probes every drive (a disconnected network or optical
        // drive blocks a pool thread for seconds), and arrivals and removals are watched anyway.
        var now = DateTime.UtcNow;
        if (now - _lastHoverRefresh < HoverRefreshInterval)
        {
            return;
        }

        _lastHoverRefresh = now;
        if (DataContext is PanelViewModel vm)
        {
            vm.DriveBar.RefreshCommand.Execute(null);
        }
    }

    /// <summary>
    /// A click on a drive button selects it. The buttons are not focusable, so the keyboard stays in the
    /// file list, and a <see cref="ListBoxItem"/> that cannot take focus ignores the mouse: it only
    /// selects itself after a successful <c>Focus()</c> (TRAP-73).
    /// </summary>
    private void OnDriveMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is ItemsControl list
            && e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(list, source) is ListBoxItem item)
        {
            item.IsSelected = true;
            e.Handled = true;
        }
    }

    /// <summary>
    /// The chip's drop-down. On a connection it starts with what concerns it — Disconnect, Reconnect,
    /// Edit, Copy address — then, always, the saved connections (the ones open in this panel ticked),
    /// Quick connect and the manager. Built on each click: the book and the open tabs change.
    /// </summary>
    private void OnConnectionButtonClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not PanelViewModel panel
            || Window.GetWindow(this) is not MainWindow { DataContext: MainViewModel shell } window)
        {
            return;
        }

        var mounts = shell.Mounts;
        var menu = new ContextMenu { PlacementTarget = ConnectionButton, Placement = PlacementMode.Bottom };

        MenuItem Command(string headerKey, string commandId)
        {
            var item = new MenuItem
            {
                Header = LocalizationManager.Get(headerKey),
                InputGestureText = window.GestureFor(commandId),
            };
            item.Click += (_, _) => window.RunCommand(commandId);
            return item;
        }

        MenuItem Action(string headerKey, Action run)
        {
            var item = new MenuItem { Header = LocalizationManager.Get(headerKey) };
            item.Click += (_, _) => run();
            return item;
        }

        if (panel.IsConnecting)
        {
            menu.Items.Add(Action("Chip_Cancel", () => mounts.CancelConnectCommand.Execute(null)));
            menu.Items.Add(new Separator());
        }
        else if (panel.ChipConnection is { } current)
        {
            menu.Items.Add(new MenuItem
            {
                Header = new TextBlock { Text = panel.ChipToolTip, TextWrapping = TextWrapping.NoWrap },
                IsEnabled = false,
            });
            menu.Items.Add(new Separator());
            if (panel.IsReconnecting)
            {
                // Reconnecting by itself (6.82): Reconnect skips the pause, and it can be stopped.
                menu.Items.Add(Command("Conn_ReconnectNow", "Reconnect"));
                menu.Items.Add(Action("Conn_ReconnectStop", () => mounts.StopReconnectCommand.Execute(panel)));
                menu.Items.Add(Command("Menu_Disconnect", "Disconnect"));
            }
            else
            {
                menu.Items.Add(Command("Menu_Disconnect", "Disconnect"));
                menu.Items.Add(Command("Menu_Reconnect", "Reconnect"));
            }

            if (mounts.IsSaved(current))
            {
                menu.Items.Add(Action("Chip_Edit", () => mounts.ShowConnectionManager(current)));
            }
            else
            {
                menu.Items.Add(Action("Chip_Save", () => mounts.SaveConnection(current)));
            }

            var address = current.IsSmb
                ? panel.CurrentPath
                : current.DescribeAddress(panel.FileList.ActiveConnectionPath ?? "/");
            menu.Items.Add(Action("Chip_CopyAddress", () => FileCommands.TrySetClipboardText(address)));
            menu.Items.Add(new Separator());
        }

        // The saved connections, the ones open in this panel ticked.
        var open = panel.OpenConnections().Select(c => c.Connection.Id).ToHashSet();
        if (panel.ShareConnectionFor?.Invoke(panel.CurrentPath) is { } share)
        {
            open.Add(share.Id);
        }

        var saved = mounts.SavedConnections
            .OrderBy(PanelViewModel.ConnectionTitle, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        foreach (var info in saved.Take(MaxListed))
        {
            var item = new MenuItem
            {
                Header = ConnectionHeader(info),
                IsCheckable = false,
                IsChecked = open.Contains(info.Id),
            };
            item.Click += (_, _) => mounts.ConnectCommand.Execute(info);
            menu.Items.Add(item);
        }

        if (saved.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = LocalizationManager.Get("Chip_NoneSaved"), IsEnabled = false });
        }
        else if (saved.Count > MaxListed)
        {
            menu.Items.Add(new MenuItem
            {
                Header = string.Format(LocalizationManager.Get("Chip_More"), saved.Count - MaxListed),
                IsEnabled = false,
            });
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Command("Menu_QuickConnect", "QuickConnect"));
        menu.Items.Add(Command("Menu_ConnectionManager", "ConnectionManager"));
        menu.IsOpen = true;
    }

    /// <summary>A long book would turn the drop-down into a wall; the manager has search for the rest.</summary>
    private const int MaxListed = 20;

    /// <summary>Name in the text colour, then the address quietly — its scheme says the kind: "prod   sftp://deploy@prod.example".</summary>
    private static object ConnectionHeader(ConnectionInfo info)
    {
        var text = new TextBlock();
        text.Inlines.Add(new System.Windows.Documents.Run(PanelViewModel.ConnectionTitle(info)));
        var detail = new System.Windows.Documents.Run(
            "   " + info.DescribeAddress(info.IsObjectStorage ? "/" : info.RemotePath))
        {
            FontSize = 10,
        };
        detail.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "ForegroundDimBrush");
        text.Inlines.Add(detail);
        return text;
    }

    /// <summary>Stops the connect in progress — a slow server, a sign-in left waiting in the browser.</summary>
    private void OnCancelConnectClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow { DataContext: MainViewModel shell })
        {
            shell.Mounts.CancelConnectCommand.Execute(null);
        }
    }

    /// <summary>Stops this panel's automatic reconnect; the error stays, with Reconnect and Disconnect under it.</summary>
    private void OnStopReconnectClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is PanelViewModel panel && Window.GetWindow(this) is MainWindow { DataContext: MainViewModel shell })
        {
            shell.Mounts.StopReconnectCommand.Execute(panel);
        }
    }

    /// <summary>Server and Refresh act on the active panel: the click activated this one first.</summary>
    private void OnRunCommand(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id } && Window.GetWindow(this) is MainWindow window)
        {
            window.RunCommand(id);
        }
    }
}
