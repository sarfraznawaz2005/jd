using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using FluentAssertions;
using JustDownload.App.ViewModels;
using JustDownload.App.Views;

namespace JustDownload.Tests.App;

/// <summary>
/// A headless multi-DPI layout-snapshot harness (TASK-084, KPI K7). For each of the supported DPI scales it
/// lays the shell out at the logical viewport a 4K screen yields at that scale, captures a snapshot of the
/// key panes' geometry, and asserts the layout has no breaks — so a regression that overflows, collapses, or
/// produces invalid bounds at any scale fails the build. The headless platform renders without a pixel
/// backend, so the snapshot is the realized layout geometry (more robust than brittle pixel diffs); a frame
/// is also captured when a drawing backend is available.
/// </summary>
internal static class DpiSnapshotHarness
{
    /// <summary>The DPI scales K7 requires support for (100% – 300%).</summary>
    public static readonly double[] Scales = [1.0, 1.25, 1.5, 2.0, 3.0];

    // A 4K physical panel — the realistic high-DPI device. The logical viewport at each scale is panel/scale.
    private const double PhysicalWidth = 3840;
    private const double PhysicalHeight = 2160;

    /// <summary>A snapshot of the realized layout at one scale.</summary>
    public sealed record LayoutSnapshot(
        double Scale,
        double WindowWidth,
        double WindowHeight,
        bool SidebarVisible,
        Rect Toolbar,
        Rect StatusBar,
        Rect List,
        Rect Detail,
        bool FrameCaptured);

    /// <summary>The logical window size a 4K screen presents at <paramref name="scale"/>.</summary>
    public static Size LogicalSize(double scale) => new(PhysicalWidth / scale, PhysicalHeight / scale);

    /// <summary>Lays out the shell at <paramref name="scale"/> and returns its layout snapshot.</summary>
    public static LayoutSnapshot Capture(MainWindow window, MainWindowViewModel vm, double scale)
    {
        Size size = LogicalSize(scale);
        window.Width = size.Width;
        window.Height = size.Height;
        window.Show();
        vm.UpdateForWidth(size.Width);
        window.UpdateLayout();

        Rect Bounds(string name) => window.FindControl<Control>(name)?.Bounds ?? default;
        bool sidebarVisible = window.FindControl<Border>("Sidebar")?.IsVisible ?? false;

        bool frame = false;
        try
        {
            WriteableBitmap? bitmap = window.CaptureRenderedFrame();
            frame = bitmap is not null;
        }
        catch (Exception)
        {
            frame = false; // no pixel backend in this headless config — layout snapshot still applies
        }

        return new LayoutSnapshot(
            scale, window.Width, window.Height, sidebarVisible,
            Bounds("Toolbar"), Bounds("StatusBar"), Bounds("ListPane"), Bounds("DetailPane"), frame);
    }

    /// <summary>Asserts the snapshot shows a valid, unbroken layout (a regression here fails the build).</summary>
    public static void AssertNoLayoutBreaks(LayoutSnapshot s)
    {
        string at = $"at {s.Scale * 100:0}% scale ({s.WindowWidth:0}x{s.WindowHeight:0})";

        foreach ((string name, Rect r) in new[]
                 {
                     ("toolbar", s.Toolbar), ("status bar", s.StatusBar), ("list", s.List), ("detail", s.Detail),
                 })
        {
            IsFinite(r).Should().BeTrue($"the {name} has finite bounds {at}");
            (r.Width >= 0 && r.Height >= 0).Should().BeTrue($"the {name} has non-negative size {at}");
        }

        s.Toolbar.Height.Should().BeGreaterThan(0, $"the toolbar is laid out {at}");
        s.StatusBar.Height.Should().BeGreaterThan(0, $"the status bar is laid out {at}");
        s.List.Width.Should().BeGreaterThan(300, $"the list keeps a usable width {at}");

        // Panes must fit within the window (no horizontal overflow) and not overlap each other.
        (s.List.Width + s.Detail.Width).Should().BeLessThanOrEqualTo(s.WindowWidth + 1, $"panes fit the window {at}");
        if (s.Detail.Width > 0)
        {
            s.Detail.X.Should().BeGreaterThanOrEqualTo(s.List.X + s.List.Width - 1, $"list and detail don't overlap {at}");
        }
    }

    private static bool IsFinite(Rect r) =>
        double.IsFinite(r.X) && double.IsFinite(r.Y) && double.IsFinite(r.Width) && double.IsFinite(r.Height);
}
