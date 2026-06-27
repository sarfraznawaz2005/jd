using Avalonia.Controls;

namespace JustDownload.App.Views;

/// <summary>
/// The per-download detail surface (TASK-054): Download/Options/Connections tabs plus per-item actions. A
/// plain <see cref="UserControl"/> so the same view (and view-model) backs both the inline pane and the
/// detached window.
/// </summary>
public partial class DownloadDetailView : UserControl
{
    public DownloadDetailView() => InitializeComponent();
}
