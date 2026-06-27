namespace JustDownload.Core.Categorization;

/// <summary>
/// The user-facing seam for customising categorisation and keeping those customisations across restarts
/// (TASK-085). It owns the live <see cref="CategorizationRules"/> the engine resolves against: it applies
/// persisted overrides over the seeded defaults at startup, and persists each new edit through the data
/// layer so it is restored next launch.
/// </summary>
public interface ICategoryRuleService
{
    /// <summary>The overrides currently applied on top of the seeded defaults.</summary>
    IReadOnlyList<CategoryRuleOverride> AppliedOverrides { get; }

    /// <summary>
    /// Loads the persisted overrides and applies them over the seeded defaults. Hosts call this once at
    /// startup (see <c>InitializeJustDownloadCoreAsync</c>) so a user's saved tweaks take effect.
    /// </summary>
    Task ApplyPersistedOverridesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds or replaces an override: applies it to the live rules immediately and persists the full
    /// override set so it survives a restart.
    /// </summary>
    /// <param name="ruleOverride">The override to apply and persist.</param>
    Task SetOverrideAsync(CategoryRuleOverride ruleOverride, CancellationToken cancellationToken = default);
}
