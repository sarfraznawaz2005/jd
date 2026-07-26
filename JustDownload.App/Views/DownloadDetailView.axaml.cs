using Avalonia;
using Avalonia.Controls;

namespace JustDownload.App.Views;

/// <summary>
/// The per-download detail surface (TASK-054): Download/Options/Connections tabs plus per-item actions. A
/// plain <see cref="UserControl"/> so the same view (and view-model) backs both the inline pane and the
/// detached window.
/// </summary>
public partial class DownloadDetailView : UserControl
{
    /// <summary>
    /// Lays the Download tab out for a wide host (the detached progress window) rather than the narrow docked
    /// pane: the speed chart spans the full width like the segment strip, and the four stats sit on one row.
    /// The docked pane has neither the width for four columns nor a reason to grow its chart.
    /// </summary>
    public static readonly StyledProperty<bool> IsWideProperty =
        AvaloniaProperty.Register<DownloadDetailView, bool>(nameof(IsWide));

    public DownloadDetailView() => InitializeComponent();

    public bool IsWide
    {
        get => GetValue(IsWideProperty);
        set => SetValue(IsWideProperty, value);
    }
}
