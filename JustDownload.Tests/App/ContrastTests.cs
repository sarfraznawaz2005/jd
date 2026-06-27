using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using FluentAssertions;
using JustDownload.App.Accessibility;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>
/// WCAG 2.1 AA contrast checks over the real design tokens in both themes (TASK-059 AC1). Resolves the actual
/// brushes from the application's theme dictionaries and asserts the key text/surface pairs clear the AA
/// thresholds — 4.5:1 for normal text, 3:1 for large/secondary text and UI accents.
/// </summary>
public sealed class ContrastTests
{
    private static Color Brush(string key, ThemeVariant variant)
    {
        Application.Current!.TryGetResource(key, variant, out object? value).Should().BeTrue($"token '{key}' should exist");
        return ((ISolidColorBrush)value!).Color;
    }

    private static void AssertNormalText(string fg, string bg, ThemeVariant variant)
    {
        double ratio = ContrastRatio.Between(Brush(fg, variant), Brush(bg, variant));
        ratio.Should().BeGreaterThanOrEqualTo(
            ContrastRatio.AaNormalText, $"{fg} on {bg} ({variant}) must meet AA for normal text (got {ratio:0.00})");
    }

    private static void AssertLargeOrUi(string fg, string bg, ThemeVariant variant)
    {
        double ratio = ContrastRatio.Between(Brush(fg, variant), Brush(bg, variant));
        ratio.Should().BeGreaterThanOrEqualTo(
            ContrastRatio.AaLargeText, $"{fg} on {bg} ({variant}) must meet AA for large/UI (got {ratio:0.00})");
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void PrimaryText_MeetsAaNormal(string variantName)
    {
        ThemeVariant variant = variantName == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

        // Primary body text on every surface it appears on.
        AssertNormalText("TextBrush", "BgBrush", variant);
        AssertNormalText("TextBrush", "CardBrush", variant);
        AssertNormalText("TextBrush", "SidebarBrush", variant);
        AssertNormalText("TextBrush", "HeaderBrush", variant);

        // Secondary (dim) text is used at normal size in the status bar / sub-lines.
        AssertNormalText("TextDimBrush", "BgBrush", variant);
        AssertNormalText("TextDimBrush", "CardBrush", variant);
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void AccentAndStatusColors_MeetAa(string variantName)
    {
        ThemeVariant variant = variantName == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

        // Text on the accent (primary button label).
        AssertNormalText("OnAccentBrush", "AccentBrush", variant);

        // Status label colours are small text — hold them to the normal-text bar on the app background.
        AssertNormalText("GreenBrush", "BgBrush", variant);
        AssertNormalText("AmberBrush", "BgBrush", variant);
        AssertNormalText("RedBrush", "BgBrush", variant);
        AssertNormalText("AccentHoverBrush", "BgBrush", variant);

        // Faint is the lowest-emphasis tier but still appears at small sizes (sub-lines, hints) — hold it to
        // the normal-text bar so every piece of text in the app meets AA.
        AssertNormalText("TextFaintBrush", "BgBrush", variant);
        AssertNormalText("TextFaintBrush", "SidebarBrush", variant);
    }
}
