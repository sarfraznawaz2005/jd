using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Reactive;
using Avalonia.VisualTree;
using JustDownload.App.Services;

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

        // Drive the responsive layout (sidebar auto-hide) from the window width (TASK-048).
        this.GetObservable(ClientSizeProperty).Subscribe(new AnonymousObserver<Size>(OnClientSizeChanged));

        // Drag a link/media onto the window to enqueue it (TASK-062 AC0).
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // Drag a completed item out to Finder/Explorer (TASK-062 AC1).
        DownloadsGrid.AddHandler(PointerMovedEvent, OnGridPointerMoved, RoutingStrategies.Tunnel);

        // Command palette (TASK-056): focus the search box when it opens, and handle its keys (tunnel so the
        // palette wins over the TextBox/ListBox for Enter/Escape/arrows).
        PaletteOverlay.AddHandler(KeyDownEvent, OnPaletteKeyDown, RoutingStrategies.Tunnel);
        PaletteResults.DoubleTapped += (_, _) => Palette()?.ExecuteCommand.Execute(null);
        PaletteOverlay.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && PaletteOverlay.IsVisible)
            {
                PaletteSearch.Focus();
                PaletteSearch.SelectAll();
            }
        };
    }

    private ViewModels.CommandPaletteViewModel? Palette() =>
        (DataContext as ViewModels.MainWindowViewModel)?.Palette;

    private void OnPaletteKeyDown(object? sender, KeyEventArgs e)
    {
        if (Palette() is not { } palette)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                palette.Close();
                e.Handled = true;
                break;
            case Key.Enter:
                palette.ExecuteCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Down:
                palette.MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                palette.MoveSelection(-1);
                e.Handled = true;
                break;
            default:
                break;
        }
    }

    // The classic drag-drop data API (DragEventArgs.Data) is the documented, working path; its DataTransfer
    // replacement can't be validated on this build host, so we keep the stable API for the drop handlers.
#pragma warning disable CS0618
    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        // Accept text/links; show a copy cursor only when there's something we can enqueue.
        bool hasLink = DroppedLinkParser.TryExtractUrl(e.Data.GetText()) is not null;
        e.DragEffects = hasLink ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not ViewModels.MainWindowViewModel vm)
        {
            return;
        }

        string? url = DroppedLinkParser.TryExtractUrl(e.Data.GetText());
        if (url is not null)
        {
            vm.RequestDownloadForUrl(url);
        }
    }
#pragma warning restore CS0618

    private async void OnGridPointerMoved(object? sender, PointerEventArgs e)
    {
        // Only start a drag when the primary button is held over a completed row with a file on disk.
        if (!e.GetCurrentPoint(DownloadsGrid).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.Source is not Visual visual || visual.FindAncestorOfType<DataGridRow>()?.DataContext
            is not ViewModels.DownloadRowViewModel { IsCompleted: true, FilePath: { } path })
        {
            return;
        }

        IStorageProvider? storage = StorageProvider;
        if (storage is null)
        {
            return;
        }

        IStorageFile? file = await storage.TryGetFileFromPathAsync(path);
        if (file is null)
        {
            return; // file moved/removed — nothing to drag out
        }

        // The classic DataObject/DoDragDrop API is the documented, working path for starting an OS file drag
        // (the file lands in Finder/Explorer). The newer DataTransfer replacement can't be validated on this
        // build host, so we keep the stable API here.
#pragma warning disable CS0618
        var data = new DataObject();
        data.Set(DataFormats.Files, new[] { file });
        await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
#pragma warning restore CS0618
    }

    private void OnClientSizeChanged(Size size)
    {
        if (DataContext is ViewModels.MainWindowViewModel vm)
        {
            vm.UpdateForWidth(size.Width);
        }
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
