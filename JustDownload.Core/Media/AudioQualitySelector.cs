namespace JustDownload.Core.Media;

/// <summary>
/// Picks the best <see cref="AudioVariant"/> from a separate-streams source (TASK-167). Unlike video there is
/// no user-configurable default audio quality — the app always wants the original-language track at the
/// clearest audio it can mux. It therefore prefers the variant with the highest <see
/// cref="AudioVariant.LanguagePreference"/> first (a variant that never declares this, e.g. from a DASH/HLS/
/// Twitter source, is treated as lowest priority), and only falls back to the highest advertised <see
/// cref="AudioVariant.Bandwidth"/> to break a tie — including when every variant's preference is unknown, which
/// keeps today's bitrate-only behaviour unchanged for extractors that never set the field. A variant with an
/// unknown bandwidth is treated as lowest priority within that tie-break (any variant with a known bitrate wins
/// over one without). Pure and deterministic.
/// </summary>
public static class AudioQualitySelector
{
    /// <summary>Selects the original-language, highest-bitrate variant from <paramref name="variants"/>.</summary>
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
            if (IsBetter(variant, best))
            {
                best = variant;
            }
        }

        return best;
    }

    private static bool IsBetter(AudioVariant candidate, AudioVariant current)
    {
        int candidatePreference = candidate.LanguagePreference ?? int.MinValue;
        int currentPreference = current.LanguagePreference ?? int.MinValue;
        if (candidatePreference != currentPreference)
        {
            return candidatePreference > currentPreference;
        }

        return (candidate.Bandwidth ?? -1) > (current.Bandwidth ?? -1);
    }
}
