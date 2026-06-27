using System.Text;
using JustDownload.Core.Data.Repositories;

namespace JustDownload.Core.Categorization;

/// <summary>
/// <see cref="ICategoryRuleStore"/> backed by the key/value <c>settings</c> table (TASK-085, over
/// TASK-020/021). The override set is stored under a single settings key as newline-separated
/// <c>scope\tkey\tcategory</c> records — a deliberately tiny, dependency-free encoding (no JSON
/// serializer, trimming/AOT-safe) that suits the handful of tweaks a user is likely to make and keeps
/// the light-&amp;-fast budget. All persistence goes through the data layer (architecture §6).
/// </summary>
internal sealed class SettingsCategoryRuleStore : ICategoryRuleStore
{
    /// <summary>The <c>settings</c> key the override records are stored under.</summary>
    internal const string StorageKey = "categorization.rule_overrides";

    private const char FieldSeparator = '\t';
    private const char RecordSeparator = '\n';

    private readonly ISettingsRepository _settings;

    public SettingsCategoryRuleStore(ISettingsRepository settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    public async Task<IReadOnlyList<CategoryRuleOverride>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        string? raw = await _settings.GetAsync(StorageKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(raw))
        {
            return [];
        }

        var overrides = new List<CategoryRuleOverride>();
        foreach (string line in raw.Split(
            RecordSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] fields = line.Split(FieldSeparator);
            if (fields.Length != 3)
            {
                continue; // Skip malformed records rather than failing the whole load.
            }

            if (!Enum.TryParse(fields[0], out CategoryRuleScope scope) || !Enum.IsDefined(scope))
            {
                continue;
            }

            if (!Enum.TryParse(fields[2], out FileCategory category) || !Enum.IsDefined(category))
            {
                continue;
            }

            string key = fields[1];
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            overrides.Add(new CategoryRuleOverride(scope, key, category));
        }

        return overrides;
    }

    public async Task SaveAsync(
        IReadOnlyList<CategoryRuleOverride> ruleOverrides,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ruleOverrides);

        var builder = new StringBuilder();
        foreach (CategoryRuleOverride ruleOverride in ruleOverrides)
        {
            // Keys are file extensions or MIME types, which never contain the separators; guard anyway
            // so a malformed key fails loud rather than corrupting the stored record stream.
            if (ruleOverride.Key.Contains(FieldSeparator) || ruleOverride.Key.Contains(RecordSeparator))
            {
                throw new ArgumentException(
                    $"Category rule key '{ruleOverride.Key}' contains a reserved separator character.",
                    nameof(ruleOverrides));
            }

            builder.Append(ruleOverride.Scope)
                .Append(FieldSeparator)
                .Append(ruleOverride.Key)
                .Append(FieldSeparator)
                .Append(ruleOverride.Category)
                .Append(RecordSeparator);
        }

        await _settings.SetAsync(StorageKey, builder.ToString(), cancellationToken)
            .ConfigureAwait(false);
    }
}
