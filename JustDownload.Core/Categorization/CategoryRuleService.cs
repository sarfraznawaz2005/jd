namespace JustDownload.Core.Categorization;

/// <summary>
/// Default <see cref="ICategoryRuleService"/>. Holds the shared <see cref="CategorizationRules"/> singleton
/// and the persistent <see cref="ICategoryRuleStore"/>, deduplicating overrides by (scope, normalised key)
/// so re-setting the same key replaces rather than appends. Mutations are guarded by a lock because the
/// rule set's maps are not themselves thread-safe (TASK-085).
/// </summary>
internal sealed class CategoryRuleService : ICategoryRuleService
{
    private readonly CategorizationRules _rules;
    private readonly ICategoryRuleStore _store;
    private readonly object _gate = new();
    private readonly Dictionary<string, CategoryRuleOverride> _overrides =
        new(StringComparer.OrdinalIgnoreCase);

    public CategoryRuleService(CategorizationRules rules, ICategoryRuleStore store)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(store);
        _rules = rules;
        _store = store;
    }

    public IReadOnlyList<CategoryRuleOverride> AppliedOverrides
    {
        get
        {
            lock (_gate)
            {
                return _overrides.Values.ToArray();
            }
        }
    }

    public async Task ApplyPersistedOverridesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CategoryRuleOverride> loaded =
            await _store.LoadAsync(cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            foreach (CategoryRuleOverride ruleOverride in loaded)
            {
                _rules.ApplyOverride(ruleOverride);
                _overrides[DedupKey(ruleOverride)] = ruleOverride;
            }
        }
    }

    public async Task SetOverrideAsync(
        CategoryRuleOverride ruleOverride,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ruleOverride);

        CategoryRuleOverride[] snapshot;
        lock (_gate)
        {
            // Validate-and-apply first so a bad key/category throws before we persist anything.
            _rules.ApplyOverride(ruleOverride);
            _overrides[DedupKey(ruleOverride)] = ruleOverride;
            snapshot = _overrides.Values.ToArray();
        }

        await _store.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the dedup key for an override. Extensions and MIME types are matched case-insensitively
    /// and a leading dot on an extension is irrelevant, mirroring how <see cref="CategorizationRules"/>
    /// normalises lookups — so "MP4", ".mp4" and "mp4" collapse to one override.
    /// </summary>
    private static string DedupKey(CategoryRuleOverride ruleOverride) =>
        $"{ruleOverride.Scope}:{ruleOverride.Key.Trim().TrimStart('.').ToLowerInvariant()}";
}
