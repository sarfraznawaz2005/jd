namespace JustDownload.Core.Media;

/// <summary>
/// Picks the best-bitrate <see cref="AudioVariant"/> from a separate-streams source (TASK-167). Unlike video
/// there is no user-configurable default audio quality — the app always wants the clearest audio it can mux,
/// so this simply prefers the highest advertised <see cref="AudioVariant.Bandwidth"/>. A variant with an
/// unknown bandwidth is treated as lowest priority (any variant with a known bitrate wins over one without).
/// Pure and deterministic.
/// </summary>
public static class AudioQualitySelector
{
    /// <summary>Selects the highest-bitrate variant from <paramref name="variants"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="variants"/> is empty.</exception>
    public static AudioVariant Select(IReadOnlyList<AudioVariant> variants)
    {
        ArgumentNullException.ThrowIfNull(variants);
        if (variants.Count == 0)
        {
            throw new ArgumentException("At least one variant is required.", nameof(variants));
        }

        AudioVariant best = variants[0];
        foreach (AudioVariant variant in variants)
        {
            if ((variant.Bandwidth ?? -1) > (best.Bandwidth ?? -1))
            {
                best = variant;
            }
        }

        return best;
    }
}
