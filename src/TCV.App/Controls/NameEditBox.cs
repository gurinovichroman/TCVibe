using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TCV.App.ViewModels;

namespace TCV.App.Controls;

/// <summary>
/// The box a file's name is edited in, in place of the name in the list (F2): in the Details name cell,
/// a Brief row and a Thumbnails tile. Shown while its entry's <see cref="FileEntryViewModel.IsEditingName"/>
/// is set. Enter takes the name, Esc drops it, and focus moving elsewhere in the window takes it too, as
/// in Explorer. The keys are its own: MainWindow and the list leave them to it while it has focus.
/// </summary>
public sealed class NameEditBox : TextBox
{
    // Made when there is an error to show. Brief and Thumbnails do not virtualize (TRAP-52): every
    // file in the folder gets a box, almost none of which ever opens.
    private ToolTip? _errorTip;

    // The entry this box opened the edit of when it took the focus. A recycled row hands the box
    // another entry's context, and focus leaving must still end the edit it belonged to.
    private FileEntryViewModel? _editing;

    private FileEntryViewModel? _observed;

    public NameEditBox()
    {
        // A subclass does not get the theme's implicit TextBox style by itself (TRAP-68).
        SetResourceReference(StyleProperty, typeof(TextBox));
        Padding = new Thickness(3, 1, 3, 1);
        MinWidth = 40;
        VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(this, "NameEditBox");

        SetBinding(TextProperty, new Binding(nameof(FileEntryViewModel.EditedName))
        {
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
        });
        SetBinding(VisibilityProperty, new Binding(nameof(FileEntryViewModel.IsEditingName))
        {
            Converter = Converters.BoolToVisibilityConverter.Instance,
        });

        IsVisibleChanged += OnIsVisibleChanged;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!(bool)e.NewValue)
        {
            CloseErrorTip();
            return;
        }

        // After the list has settled: moving the cursor to the row focuses its cell a moment later
        // (FileListControl.ApplyGridFocus), and that must not take the keys back from the box.
        Dispatcher.InvokeAsync(TakeFocus, DispatcherPriority.ContextIdle);
    }

    private void TakeFocus()
    {
        if (!IsVisible || DataContext is not FileEntryViewModel { IsEditingName: true } item)
        {
            return;
        }

        Focus();
        _editing = item;
        if (item.SelectBaseNameOnEdit)
        {
            item.SelectBaseNameOnEdit = false;
            Select(0, item.BaseNameLength);
        }
        else
        {
            CaretIndex = Text.Length;
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_observed is not null)
        {
            _observed.PropertyChanged -= OnEntryPropertyChanged;
        }

        _observed = e.NewValue as FileEntryViewModel;
        if (_observed is not null)
        {
            _observed.PropertyChanged += OnEntryPropertyChanged;
        }
    }

    private void OnEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FileEntryViewModel.NameEditError) || sender is not FileEntryViewModel item)
        {
            return;
        }

        if (item.NameEditError is null || !IsVisible)
        {
            CloseErrorTip();
            return;
        }

        if (_errorTip is null)
        {
            _errorTip = new ToolTip { Placement = PlacementMode.Bottom, PlacementTarget = this };
            _errorTip.SetResourceReference(ForegroundProperty, "DangerBrush");
        }

        _errorTip.Content = item.NameEditError;
        _errorTip.IsOpen = true;
    }

    private void CloseErrorTip()
    {
        if (_errorTip is not null)
        {
            _errorTip.IsOpen = false;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
            case Key.Tab:
                // A refused name keeps the box open and says why (the error tip).
                if (Owner?.EndNameEdit(commit: true) ?? true)
                {
                    ReturnToList();
                }

                e.Handled = true;
                return;
            case Key.Escape:
                Owner?.EndNameEdit(commit: false);
                ReturnToList();
                e.Handled = true;
                return;
        }

        base.OnKeyDown(e);

        // Up, Down and the page keys would move the list's cursor away from the box.
        if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown)
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Focus left the box. Into the box's own menu (Cut, Copy, Paste) or out of an inactive window it
    /// comes back, so the edit stays open; anywhere else in the window — another row, the other panel —
    /// the name is taken as Explorer takes it, and dropped if Windows would refuse it.
    /// </summary>
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (_editing is not { } item)
        {
            return;
        }

        // Read now: a row being rebuilt takes the box out of the window a moment later.
        var owner = Owner;
        var window = Window.GetWindow(this) ?? Application.Current?.MainWindow;
        Dispatcher.InvokeAsync(() =>
        {
            if (owner is null || !ReferenceEquals(owner.NameEditTarget, item) || IsFocusBackInEdit(item))
            {
                return;
            }

            if (window is { IsActive: false } || FocusIsInPopup())
            {
                return;
            }

            _editing = null;
            if (!owner.EndNameEdit(commit: true))
            {
                owner.EndNameEdit(commit: false);
            }
        }, DispatcherPriority.Input);
    }

    private static bool IsFocusBackInEdit(FileEntryViewModel item) =>
        Keyboard.FocusedElement is NameEditBox { DataContext: var context } && ReferenceEquals(context, item);

    /// <summary>A menu or other popup has the keys (its visual root is not a window).</summary>
    private static bool FocusIsInPopup()
    {
        if (Keyboard.FocusedElement is not DependencyObject focused)
        {
            return false;
        }

        DependencyObject current = focused;
        while (VisualTreeHelper.GetParent(current) is { } parent)
        {
            current = parent;
        }

        return current is not Window;
    }

    /// <summary>The panel's list this box belongs to.</summary>
    private FileListViewModel? Owner => FindListControl()?.DataContext as FileListViewModel;

    private FileListControl? FindListControl()
    {
        DependencyObject? current = this;
        while (current is not null)
        {
            if (current is FileListControl list)
            {
                return list;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    /// <summary>After Enter or Esc the keys go back to the list, on the cursor's row.</summary>
    private void ReturnToList()
    {
        _editing = null;
        CloseErrorTip();
        var list = FindListControl();
        Dispatcher.InvokeAsync(() => list?.FocusList(), DispatcherPriority.Input);
    }
}
