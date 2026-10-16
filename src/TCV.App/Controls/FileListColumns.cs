using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using TCV.App.Columns;
using TCV.App.Converters;
using TCV.App.Localization;
using TCV.App.Settings;
using TCV.App.ViewModels;

namespace TCV.App.Controls;

/// <summary>
/// The details-view columns half of <see cref="FileListControl"/> (6.19): building the grid's
/// columns from the saved layout, the header context menu that toggles them, and writing widths
/// back when the user drags a divider.
/// </summary>
public partial class FileListControl
{
    /// <summary>Guards the width write-back while columns are being rebuilt programmatically.</summary>
    private bool _rebuildingColumns;

    /// <summary>
    /// Rebuilds <c>Grid.Columns</c> from the ViewModel's layout. Called on attach and whenever the
    /// layout changes; the sort indicator is restored afterwards, since the column objects that
    /// carried it are gone.
    /// </summary>
    private void RebuildColumns()
    {
        var vm = ViewModel;
        if (vm is null) return;

        _rebuildingColumns = true;
        try
        {
            Grid.Columns.Clear();
            var lang = LocalizationManager.CurrentLanguage;

            foreach (var layout in vm.Columns)
            {
                if (!layout.Visible) continue;
                var descriptor = ColumnCatalog.Find(layout.Id);
                if (descriptor is null) continue;

                var column = BuildColumn(descriptor, layout, lang);
                if (column is not null) Grid.Columns.Add(column);
            }
        }
        finally
        {
            _rebuildingColumns = false;
        }

        RestoreSortIndicator();
    }

    private DataGridColumn? BuildColumn(ColumnDescriptor descriptor, ColumnLayout layout, string lang)
    {
        DataGridColumn column = descriptor.Id switch
        {
            ColumnCatalog.Name => new DataGridTemplateColumn
            {
                CellTemplate = (DataTemplate)Resources["NameCellTemplate"],
            },
            ColumnCatalog.Extension => Text(nameof(FileEntryViewModel.Extension)),
            // The row decides the folder's dash: a size of 0 is also an empty file.
            ColumnCatalog.Size => Text(nameof(FileEntryViewModel.SizeDisplay)),
            ColumnCatalog.Date => Text(nameof(FileEntryViewModel.LastModified), DateFormatConverter.Instance, (Style)Resources["DateCellText"]),
            ColumnCatalog.Attributes => Text(nameof(FileEntryViewModel.AttributesDisplay)),
            ColumnCatalog.Type => Text(nameof(FileEntryViewModel.TypeName)),
            ColumnCatalog.Lines => Text(nameof(FileEntryViewModel.LineCountDisplay)),
            ColumnCatalog.SizeOnDisk => Text(nameof(FileEntryViewModel.SizeOnDiskDisplay)),
            ColumnCatalog.Git => new DataGridTextColumn
            {
                Binding = new Binding(nameof(FileEntryViewModel.GitLabel)),
                ElementStyle = GitCellStyle,
            },
            _ => null!,
        };
        if (column is null) return null;

        column.Header = descriptor.Title(lang);
        column.MinWidth = descriptor.MinWidth;
        // A width of 0 marks the name column, which takes whatever the fixed ones leave over.
        column.Width = descriptor.DefaultWidth == 0
            ? new DataGridLength(1, DataGridLengthUnitType.Star)
            : new DataGridLength(layout.Width > 0 ? layout.Width : descriptor.DefaultWidth);

        // SortMemberPath doubles as the id the sorting handler maps back to a SortColumn, so an
        // unsortable column must leave it empty rather than name a property.
        column.SortMemberPath = descriptor.Sort is null ? string.Empty : SortMemberPath(descriptor.Sort.Value);
        column.CanUserSort = descriptor.Sort is not null;

        // Figures are right-aligned mono in the dim colour, captions too (the design's size column).
        if (descriptor.Alignment == ColumnAlignment.Right && column is DataGridTextColumn text)
        {
            text.ElementStyle = (Style)Resources["SizeCellText"];
            column.HeaderStyle = RightHeaderStyle;
        }

        return column;

        static DataGridTextColumn Text(string path, IValueConverter? converter = null, Style? style = null)
        {
            var column = new DataGridTextColumn { Binding = new Binding(path) { Converter = converter } };
            if (style is not null)
            {
                column.ElementStyle = style;
            }

            return column;
        }
    }

    /// <summary>
    /// Cell style for the Git column: a monospaced label whose colour comes from the theme tokens
    /// (never a literal colour, so the Light theme stays readable), plus a plain-language tooltip.
    /// Built in code because the column itself is — the catalog is the single source of columns.
    /// </summary>
    private static Style GitCellStyle { get; } = BuildGitCellStyle();

    /// <summary>The header of a right-aligned column: the caption over the figures.</summary>
    private Style RightHeaderStyle => _rightHeaderStyle ??= new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader), (Style)Resources["HeaderStyle"])
    {
        Setters = { new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Right) },
    };

    private Style? _rightHeaderStyle;

    private static Style BuildGitCellStyle()
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.FontFamilyProperty, new DynamicResourceExtension("MonoFontFamily")));
        style.Setters.Add(new Setter(TextBlock.FontSizeProperty, 10.0));
        style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(nameof(FileEntryViewModel.GitToolTip))));
        style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));

        void ForKind(TCV.Git.GitCellKind kind, string brushKey)
        {
            var trigger = new DataTrigger
            {
                Binding = new Binding(nameof(FileEntryViewModel.GitKind)),
                Value = kind,
            };
            trigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension(brushKey)));
            style.Triggers.Add(trigger);
        }

        ForKind(TCV.Git.GitCellKind.Conflict, "DangerBrush");
        ForKind(TCV.Git.GitCellKind.Modified, "WarningBrush");
        ForKind(TCV.Git.GitCellKind.Staged, "SuccessBrush");
        // The design: a change is amber, an addition green; untracked is a change git does not know yet.
        ForKind(TCV.Git.GitCellKind.Untracked, "WarningBrush");
        return style;
    }

    /// <summary>Property name the grid sorts a column by; also the key the sorting handler reads.</summary>
    private static string SortMemberPath(SortColumn column) => column switch
    {
        SortColumn.Name => nameof(FileEntryViewModel.Name),
        SortColumn.Extension => nameof(FileEntryViewModel.Extension),
        SortColumn.Size => nameof(FileEntryViewModel.Size),
        SortColumn.Date => nameof(FileEntryViewModel.LastModified),
        SortColumn.Attributes => nameof(FileEntryViewModel.Attributes),
        _ => nameof(FileEntryViewModel.Name),
    };

    /// <summary>
    /// Copies the grid's current widths back into the layout. Pulled by the ViewModel whenever the
    /// layout is about to be persisted (<c>ColumnWidthsRequested</c>) rather than pushed on every
    /// drag: WPF has no "divider released" event, and asking at save time is what actually
    /// guarantees the next session opens with the columns the user arranged.
    /// </summary>
    private void CaptureColumnWidths()
    {
        var vm = ViewModel;
        if (vm is null || _rebuildingColumns) return;

        var byId = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var visible = vm.Columns.Where(c => c.Visible).ToList();
        for (var i = 0; i < visible.Count && i < Grid.Columns.Count; i++)
        {
            var column = Grid.Columns[i];
            // The star-sized name column has no width worth storing — it is whatever is left.
            if (column.Width.IsStar) continue;
            byId[visible[i].Id] = column.ActualWidth;
        }

        foreach (var layout in vm.Columns)
            if (byId.TryGetValue(layout.Id, out var width) && width > 0)
                layout.Width = width;
    }

    // ── Header context menu ───────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds the "which columns" menu each time it opens: one checkable item per catalog entry
    /// plus a reset. Measured columns are marked so the cost of switching them on is not a
    /// surprise.
    /// </summary>
    private void OnColumnsMenuOpened(object sender, RoutedEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null) return;

        ColumnsMenu.Items.Clear();
        var lang = LocalizationManager.CurrentLanguage;

        foreach (var layout in vm.Columns)
        {
            var descriptor = ColumnCatalog.Find(layout.Id);
            if (descriptor is null) continue;

            var item = new MenuItem
            {
                Header = descriptor.Title(lang) + (descriptor.IsComputed ? "  ·  " + LocalizationManager.Get("Col_Computed") : string.Empty),
                IsCheckable = true,
                IsChecked = layout.Visible,
                // The name column is the one thing a details view cannot do without.
                IsEnabled = !string.Equals(descriptor.Id, ColumnCatalog.Name, StringComparison.Ordinal),
            };
            var id = layout.Id;
            item.Click += (_, _) => ToggleColumn(id);
            ColumnsMenu.Items.Add(item);
        }

        // How the rows are shown: these sat in a toolbar above the list until the design moved the view
        // buttons into the status line and left no room for them (ADR-0030).
        ColumnsMenu.Items.Add(new Separator());
        ColumnsMenu.Items.Add(DisplayToggle("Files_ShowHidden", vm.ShowHiddenFiles, () => vm.ShowHiddenFiles = !vm.ShowHiddenFiles));
        ColumnsMenu.Items.Add(DisplayToggle("Files_ShowExtensions", vm.ShowExtensions, () => vm.ShowExtensions = !vm.ShowExtensions));
        ColumnsMenu.Items.Add(DisplayToggle("Files_FoldersFirst", vm.FoldersFirst, () => vm.FoldersFirst = !vm.FoldersFirst));

        ColumnsMenu.Items.Add(new Separator());
        var reset = new MenuItem { Header = LocalizationManager.Get("Col_Reset") };
        reset.Click += (_, _) =>
        {
            var panel = ViewModel;
            if (panel is not null) panel.Columns = ColumnCatalog.DefaultLayout();
        };
        ColumnsMenu.Items.Add(reset);
    }

    private static MenuItem DisplayToggle(string key, bool isOn, Action toggle)
    {
        var item = new MenuItem { Header = LocalizationManager.Get(key), IsCheckable = true, IsChecked = isOn };
        item.Click += (_, _) => toggle();
        return item;
    }

    private void ToggleColumn(string id)
    {
        var vm = ViewModel;
        if (vm is null) return;

        CaptureColumnWidths();
        var layout = vm.Columns.ToList();
        var target = layout.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
        if (target is null) return;

        target.Visible = !target.Visible;
        vm.Columns = layout;   // the setter normalises, raises ColumnsChanged and starts measuring
    }
}
