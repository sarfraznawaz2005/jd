using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using FluentAssertions;
using JustDownload.App.ViewModels;
using JustDownload.App.Views;

namespace JustDownload.Tests.App;

/// <summary>
/// A headless multi-DPI layout-snapshot harness (TASK-084/TASK-114, KPI K7). For each supported DPI scale it
/// lays the shell out at the logical viewport a 4K screen yields at that scale <b>and</b> drives the window's
/// <see cref="TopLevel.RenderScaling"/> to that scale (TASK-114), then snapshots the key panes' geometry and
/// asserts the layout has no breaks, the scaling is genuinely engaged, and geometry is snapped to the
/// physical-pixel grid at that DPI — so a regression that overflows, collapses, produces invalid bounds, or
/// bypasses the DPI layout path at any scale fails the build. Realized layout geometry (in DIPs) is more
/// robust than brittle pixel diffs and works without a drawing backend.
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
        double RenderScaling,
        double WindowWidth,
        double WindowHeight,
        bool SidebarVisible,
        Rect Toolbar,
        Rect StatusBar,
        Rect List,
        Rect Detail);

    /// <summary>The logical window size a 4K screen presents at <paramref name="scale"/>.</summary>
    public static Size LogicalSize(double scale) => new(PhysicalWidth / scale, PhysicalHeight / scale);

    /// <summary>Lays out the shell at <paramref name="scale"/> (logical viewport <b>and</b> DPI render scaling)
    /// and returns its layout snapshot.</summary>
    public static LayoutSnapshot Capture(MainWindow window, MainWindowViewModel vm, double scale)
    {
        Size size = LogicalSize(scale);
        window.Width = size.Width;
        window.Height = size.Height;
        window.Show();

        // Drive the actual DPI render scaling, then re-lay-out so the layout-rounding path snaps geometry to
        // the physical-pixel grid at this scale (TASK-114). Without this the test would only vary window size.
        ApplyRenderScaling(window, scale);
        vm.UpdateForWidth(size.Width);
        window.UpdateLayout();

        Rect Bounds(string name) => window.FindControl<Control>(name)?.Bounds ?? default;
        bool sidebarVisible = window.FindControl<Border>("Sidebar")?.IsVisible ?? false;

        return new LayoutSnapshot(
            scale, window.RenderScaling, window.Width, window.Height, sidebarVisible,
            Bounds("Toolbar"), Bounds("StatusBar"), Bounds("ListPane"), Bounds("DetailPane"));
    }

    /// <summary>
    /// Forces the headless window to report <paramref name="scale"/> as its <see cref="TopLevel.RenderScaling"/>
    /// and re-lays-out, so the DPI layout path is genuinely exercised. Avalonia.Headless has no public scaling
    /// API, so this reaches the platform impl by reflection; it throws (fails the build loudly) if the internal
    /// shape ever changes, rather than silently testing at scale 1.
    /// </summary>
    private static void ApplyRenderScaling(MainWindow window, double scale)
    {
        object impl = typeof(TopLevel)
                .GetField("<PlatformImpl>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(window)
            ?? throw new InvalidOperationException("Could not reach the headless window's PlatformImpl.");

        FieldInfo renderScaling = impl.GetType()
                .GetField("<RenderScaling>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("HeadlessWindowImpl.RenderScaling backing field not found.");
        renderScaling.SetValue(impl, scale);

        // Notify the visual tree so it re-reads LayoutScaling from RenderScaling and re-lays-out.
        var scalingChanged = (Delegate?)impl.GetType()
            .GetField("<ScalingChanged>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(impl);
        scalingChanged?.DynamicInvoke(scale);
        window.UpdateLayout();
    }

    /// <summary>Asserts the snapshot shows a valid, unbroken layout (a regression here fails the build).</summary>
    public static void AssertNoLayoutBreaks(LayoutSnapshot s)
    {
        string at = $"at {s.Scale * 100:0}% scale ({s.WindowWidth:0}x{s.WindowHeight:0})";

        // The DPI scaling is genuinely engaged (not stuck at 1) — the regression the harness was missing.
        s.RenderScaling.Should().BeApproximately(s.Scale, 1e-9, $"the window renders at the DPI scale {at}");

        foreach ((string name, Rect r) in new[]
                 {
                     ("toolbar", s.Toolbar), ("status bar", s.StatusBar), ("list", s.List), ("detail", s.Detail),
                 })
        {
            IsFinite(r).Should().BeTrue($"the {name} has finite bounds {at}");
            (r.Width >= 0 && r.Height >= 0).Should().BeTrue($"the {name} has non-negative size {at}");

            // Layout rounding snaps geometry to the physical-pixel grid at this DPI: a size laid out at
            // scale S is a multiple of 1/S DIPs, i.e. size*S is integral. This only holds if the layout pass
            // actually used scale S (it would fail if it had laid out at scale 1).
            PixelAligned(r.Width, s.Scale).Should().BeTrue($"the {name} width is pixel-snapped {at}");
            PixelAligned(r.Height, s.Scale).Should().BeTrue($"the {name} height is pixel-snapped {at}");
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

    /// <summary>Whether <paramref name="value"/> DIPs lands on the physical-pixel grid at <paramref name="scale"/>.</summary>
    private static bool PixelAligned(double value, double scale)
    {
        double physical = value * scale;
        return Math.Abs(physical - Math.Round(physical)) < 0.01;
    }
}
