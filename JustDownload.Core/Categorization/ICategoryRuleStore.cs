namespace JustDownload.Core.Categorization;

/// <summary>
/// Persists the user's categorisation overrides (TASK-085) through the data layer so they survive
/// restarts. Only the deltas from the seeded defaults are stored — the defaults themselves live in
/// <see cref="CategorizationRules.CreateDefault"/>, so an app upgrade that improves the defaults still
/// reaches users while their explicit tweaks continue to win.
/// </summary>
public interface ICategoryRuleStore
{
    /// <summary>Loads the persisted overrides, or an empty list when none have been saved.</summary>
    Task<IReadOnlyList<CategoryRuleOverride>> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Replaces the persisted override set with <paramref name="ruleOverrides"/> (full rewrite).</summary>
    Task SaveAsync(
        IReadOnlyList<CategoryRuleOverride> ruleOverrides,
        CancellationToken cancellationToken = default);
}
