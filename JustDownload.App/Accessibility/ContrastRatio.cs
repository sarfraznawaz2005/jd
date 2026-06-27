using Avalonia.Media;

namespace JustDownload.App.Accessibility;

/// <summary>
/// Pure WCAG 2.1 contrast-ratio maths (TASK-059) used to assert the design tokens meet AA in both themes.
/// Computes relative luminance per the WCAG definition and the contrast ratio between two colours; the
/// AA thresholds are 4.5:1 for normal text and 3:1 for large text and UI components.
/// </summary>
public static class ContrastRatio
{
    /// <summary>The WCAG AA threshold for normal-size text.</summary>
    public const double AaNormalText = 4.5;

    /// <summary>The WCAG AA threshold for large text and UI component boundaries.</summary>
    public const double AaLargeText = 3.0;

    /// <summary>The contrast ratio between two opaque colours, in the range [1, 21].</summary>
    public static double Between(Color a, Color b)
    {
        double la = RelativeLuminance(a);
        double lb = RelativeLuminance(b);
        double lighter = Math.Max(la, lb);
        double darker = Math.Min(la, lb);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>The WCAG relative luminance of a colour (alpha ignored — tokens are opaque surfaces/text).</summary>
    public static double RelativeLuminance(Color color)
    {
        double r = Linearize(color.R / 255.0);
        double g = Linearize(color.G / 255.0);
        double b = Linearize(color.B / 255.0);
        return (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
    }

    private static double Linearize(double channel) =>
        channel <= 0.03928 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
}
