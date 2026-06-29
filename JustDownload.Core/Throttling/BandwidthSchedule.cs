using System.Globalization;

namespace JustDownload.Core.Throttling;

/// <summary>
/// One time-of-day bandwidth rule (TASK-145): between <see cref="Start"/> and <see cref="End"/> (local wall
/// clock) the global cap is <see cref="BytesPerSecond"/> (<c>0</c> = unlimited). A rule whose start equals its
/// end is "all day"; a rule whose start is after its end wraps past midnight (e.g. 22:00–06:00).
/// </summary>
public sealed record BandwidthRule(TimeOnly Start, TimeOnly End, long BytesPerSecond)
{
    public bool IsActiveAt(TimeOnly now)
    {
        if (Start == End)
        {
            return true; // all-day rule
        }

        return Start < End
            ? now >= Start && now < End
            : now >= Start || now < End; // wraps past midnight
    }
}

/// <summary>
/// Parses/formats the time-of-day bandwidth rules (TASK-145) stored in
/// <c>AppSettings.BandwidthSchedule</c> as <c>HH:mm-HH:mm=bytes;…</c>, and resolves the effective cap for a
/// given time: the first matching rule's cap, else the manual global limit. Pure and culture-invariant.
/// </summary>
public static class BandwidthSchedule
{
    public static IReadOnlyList<BandwidthRule> Parse(string? value)
    {
        var rules = new List<BandwidthRule>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return rules;
        }

        foreach (string entry in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = entry.IndexOf('=', StringComparison.Ordinal);
            int dash = entry.IndexOf('-', StringComparison.Ordinal);
            if (eq <= 0 || dash <= 0 || dash >= eq)
            {
                continue;
            }

            if (TimeOnly.TryParseExact(entry[..dash].Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly start)
                && TimeOnly.TryParseExact(entry[(dash + 1)..eq].Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly end)
                && long.TryParse(entry[(eq + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long bytes)
                && bytes >= 0)
            {
                rules.Add(new BandwidthRule(start, end, bytes));
            }
        }

        return rules;
    }

    public static string Format(IReadOnlyList<BandwidthRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return string.Join(';', rules.Select(r => string.Create(
            CultureInfo.InvariantCulture,
            $"{r.Start:HH\\:mm}-{r.End:HH\\:mm}={r.BytesPerSecond}")));
    }

    /// <summary>The cap in effect at <paramref name="now"/>: the first active rule's cap, else <paramref name="manualLimit"/>.</summary>
    public static long EffectiveLimit(IReadOnlyList<BandwidthRule> rules, TimeOnly now, long manualLimit)
    {
        ArgumentNullException.ThrowIfNull(rules);
        foreach (BandwidthRule rule in rules)
        {
            if (rule.IsActiveAt(now))
            {
                return rule.BytesPerSecond;
            }
        }

        return manualLimit;
    }
}
