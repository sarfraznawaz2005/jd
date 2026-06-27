using System.Globalization;

namespace JustDownload.App.Formatting;

/// <summary>Pure, culture-invariant formatting of byte sizes and transfer rates for the UI (TASK-049).</summary>
public static class ByteFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>Formats a byte count like <c>52.5 MB</c> (binary units), or <c>0 B</c> for zero.</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // Whole bytes need no decimal; larger units show one.
        string number = unit == 0
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.0", CultureInfo.InvariantCulture);
        return $"{number} {Units[unit]}";
    }

    /// <summary>Formats a transfer rate like <c>442 KB/s</c>; a non-positive rate renders as <c>—</c>.</summary>
    public static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0)
        {
            return "—";
        }

        return FormatSize((long)Math.Round(bytesPerSecond)) + "/s";
    }
}
