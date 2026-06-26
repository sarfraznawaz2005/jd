namespace JustDownload.Core.Settings;

/// <summary>
/// The preferred default video resolution for media downloads. The underlying value is the vertical
/// pixel height so the values sort naturally from lowest to highest quality. The extractor selects
/// the closest available variant at or below this preference.
/// </summary>
public enum VideoQuality
{
    /// <summary>360p.</summary>
    P360 = 360,

    /// <summary>480p (SD).</summary>
    P480 = 480,

    /// <summary>720p (HD).</summary>
    P720 = 720,

    /// <summary>1080p (Full HD) — the default.</summary>
    P1080 = 1080,

    /// <summary>1440p (Quad HD).</summary>
    P1440 = 1440,

    /// <summary>2160p (4K Ultra HD).</summary>
    P2160 = 2160,
}
