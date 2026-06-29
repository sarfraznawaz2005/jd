using System.Globalization;

namespace JustDownload.Core.Lifecycle;

/// <summary>
/// Parses and formats the per-category concurrent-download caps (TASK-141) stored in
/// <c>AppSettings.CategoryConcurrencyLimits</c> as the canonical string <c>Video=2;Compressed=1</c>. Only
/// positive caps are kept; a category that is absent (or non-positive) is uncapped. Category names are the
/// <c>FileCategory</c> enum names, matched case-insensitively.
/// </summary>
public static class CategoryConcurrency
{
    /// <summary>Parses the canonical string into a category&#8594;cap map (only positive caps), ignoring malformed entries.</summary>
    public static IReadOnlyDictionary<string, int> Parse(string? value)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value))
        {
            return map;
        }

        foreach (string entry in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separator = entry.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            string name = entry[..separator].Trim();
            string rawCap = entry[(separator + 1)..].Trim();
            if (name.Length > 0
                && int.TryParse(rawCap, NumberStyles.Integer, CultureInfo.InvariantCulture, out int cap)
                && cap > 0)
            {
                map[name] = cap;
            }
        }

        return map;
    }

    /// <summary>Formats a category&#8594;cap map into the canonical string, dropping non-positive caps, ordered by name.</summary>
    public static string Format(IReadOnlyDictionary<string, int> caps)
    {
        ArgumentNullException.ThrowIfNull(caps);

        IEnumerable<string> entries = caps
            .Where(kv => kv.Value > 0)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => string.Create(CultureInfo.InvariantCulture, $"{kv.Key}={kv.Value}"));

        return string.Join(';', entries);
    }
}
