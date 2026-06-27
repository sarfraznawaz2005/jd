using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace JustDownload.App.Views;

/// <summary>The application's primary window (TASK-047 shell, TASK-051 downloads list).</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Right-clicking a row should select it first, so the shared context menu acts on the row under the
        // cursor rather than the previously-selected one. Tunnel so this runs before the menu opens.
        DownloadsGrid.AddHandler(PointerPressedEvent, OnGridPointerPressed, RoutingStrategies.Tunnel);

        // Once the headers are realised, give each a context menu that toggles which columns are shown.
        DownloadsGrid.Loaded += OnDownloadsGridLoaded;
    }

    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            return;
        }

        if (e.Source is Visual visual && visual.FindAncestorOfType<DataGridRow>() is { } row)
        {
            DownloadsGrid.SelectedItem = row.DataContext;
        }
    }

    private void OnDownloadsGridLoaded(object? sender, RoutedEventArgs e)
    {
        foreach (DataGridColumnHeader header in DownloadsGrid.GetVisualDescendants().OfType<DataGridColumnHeader>())
        {
            // A ContextMenu is a control and cannot be shared across parents, so each header gets its own.
            header.ContextMenu = CreateColumnsMenu(DownloadsGrid);
        }
    }

    /// <summary>
    /// Builds a "show/hide columns" menu: one checkable item per column, two-way bound to the column's
    /// visibility so the list's columns are hideable (TASK-051 AC0). Exposed for headless testing.
    /// </summary>
    public static ContextMenu CreateColumnsMenu(DataGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);

        var items = new List<MenuItem>(grid.Columns.Count);
        foreach (DataGridColumn column in grid.Columns)
        {
            var item = new MenuItem
            {
                Header = column.Header?.ToString() ?? "Column",
                ToggleType = MenuItemToggleType.CheckBox,
            };
            item.Bind(MenuItem.IsCheckedProperty, new Binding(nameof(DataGridColumn.IsVisible))
            {
                Source = column,
                Mode = BindingMode.TwoWay,
            });
            items.Add(item);
        }

        return new ContextMenu { ItemsSource = items };
    }
}
