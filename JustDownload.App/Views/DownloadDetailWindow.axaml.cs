using Avalonia.Controls;

namespace JustDownload.App.Views;

/// <summary>
/// The detached per-download detail window (TASK-054 AC0): hosts the shared <see cref="DownloadDetailView"/>
/// so the detail can be popped out of the main window while the inline panel keeps working.
/// </summary>
public partial class DownloadDetailWindow : Window
{
    public DownloadDetailWindow() => InitializeComponent();
}
