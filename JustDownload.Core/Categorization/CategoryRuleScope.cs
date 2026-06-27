namespace JustDownload.Core.Categorization;

/// <summary>
/// Which dimension of <see cref="CategorizationRules"/> a persisted user override targets (TASK-085).
/// Mirrors the three map-by methods on the rule set so an override round-trips to the right map.
/// </summary>
public enum CategoryRuleScope
{
    /// <summary>A file-extension override, e.g. <c>"dat" → Audio</c>.</summary>
    Extension = 0,

    /// <summary>An exact MIME-type override, e.g. <c>"application/x-pack" → Compressed</c>.</summary>
    MimeType = 1,

    /// <summary>A MIME top-level override, e.g. <c>"model" → Other</c>.</summary>
    MimeTopLevelType = 2,
}
