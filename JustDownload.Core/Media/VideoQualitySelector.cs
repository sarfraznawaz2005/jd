using JustDownload.Core.Settings;

namespace JustDownload.Core.Media;

/// <summary>
/// Picks the best variant for a desired default quality (TASK-042 AC1). The rule mirrors how players
/// honour a quality preference: choose the highest variant that does not exceed the requested height; if
/// every variant is higher than requested, fall back to the smallest available. Ties on height are broken
/// by the higher bandwidth. Pure and deterministic.
/// </summary>
public static class VideoQualitySelector
{
    /// <summary>Selects the variant matching <paramref name="desiredQuality"/> from <paramref name="variants"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="variants"/> is empty.</exception>
    public static VideoVariant Select(IReadOnlyList<VideoVariant> variants, VideoQuality desiredQuality)
    {
        ArgumentNullException.ThrowIfNull(variants);
        if (variants.Count == 0)
        {
            throw new ArgumentException("At least one variant is required.", nameof(variants));
        }

        int desiredHeight = (int)desiredQuality;

        VideoVariant? best = null;
        foreach (VideoVariant variant in variants)
        {
            if (variant.Height <= desiredHeight && IsBetterAtOrBelow(variant, best))
            {
                best = variant;
            }
        }

        // Every variant is above the requested quality → take the smallest (closest from above).
        return best ?? Smallest(variants);
    }

    private static bool IsBetterAtOrBelow(VideoVariant candidate, VideoVariant? current)
    {
        if (current is null)
        {
            return true;
        }

        if (candidate.Height != current.Height)
        {
            return candidate.Height > current.Height;
        }

        return (candidate.Bandwidth ?? 0) > (current.Bandwidth ?? 0);
    }

    private static VideoVariant Smallest(IReadOnlyList<VideoVariant> variants)
    {
        VideoVariant smallest = variants[0];
        foreach (VideoVariant variant in variants)
        {
            if (variant.Height < smallest.Height ||
                (variant.Height == smallest.Height && (variant.Bandwidth ?? 0) < (smallest.Bandwidth ?? 0)))
            {
                smallest = variant;
            }
        }

        return smallest;
    }
}
