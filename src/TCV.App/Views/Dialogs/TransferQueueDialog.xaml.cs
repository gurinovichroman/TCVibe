using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using TCV.Network;

namespace TCV.App.Views.Dialogs;

/// <summary>
/// Live view of the background FTP/SFTP/WebDAV transfer queue. The window is non-modal —
/// transfers keep running while the user works in the panels. Each button is enabled only when
/// it can do something to the selected transfer.
/// </summary>
public partial class TransferQueueDialog : TcvDialog
{
    private readonly TransferQueue _queue;
    private TransferItem? _watched;

    public TransferQueueDialog(TransferQueue queue)
    {
        InitializeComponent();
        _queue = queue;
        DataContext = queue.Items;

        queue.Items.CollectionChanged += OnItemsChanged;
        Closed += (_, _) => queue.Items.CollectionChanged -= OnItemsChanged;

        // "Clear finished" depends on every transfer, not just the selected one: without a timer
        // it stayed disabled after the last transfer ended until a row was clicked.
        var poll = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        poll.Tick += (_, _) => RefreshState();
        poll.Start();
        Closed += (_, _) => poll.Stop();

        RefreshState();
    }

    private TransferItem? Selected => TransferGrid.SelectedItem as TransferItem;

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshState();

    private void TransferGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Follow the selected transfer's status so a button enables the moment it becomes valid.
        if (_watched is not null)
        {
            _watched.PropertyChanged -= OnWatchedChanged;
        }

        _watched = Selected;
        if (_watched is not null)
        {
            _watched.PropertyChanged += OnWatchedChanged;
        }

        RefreshState();
    }

    private void OnWatchedChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TransferItem.Status))
        {
            Dispatcher.InvokeAsync(RefreshState);
        }
    }

    private void RefreshState()
    {
        var status = Selected?.Status;
        PauseButton.IsEnabled = status == TransferStatus.Transferring;
        ResumeButton.IsEnabled = status == TransferStatus.Paused;
        CancelTransferButton.IsEnabled = status is TransferStatus.Pending or TransferStatus.Transferring or TransferStatus.Paused;
        RetryButton.IsEnabled = status is TransferStatus.Failed or TransferStatus.Cancelled;
        ClearButton.IsEnabled = _queue.Items.Any(i => i.IsFinished);
        EmptyText.Visibility = _queue.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } item)
        {
            _queue.Pause(item.Id);
        }
    }

    private void ResumeButton_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } item)
        {
            _ = _queue.ResumeAsync(item.Id);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } item)
        {
            _queue.Cancel(item.Id);
        }
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } item)
        {
            _queue.Retry(item.Id);
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _queue.ClearFinished();
        RefreshState();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
