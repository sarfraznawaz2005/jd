using System.Globalization;
using System.Text.RegularExpressions;

namespace JustDownload.Core.Lifecycle;

/// <summary>
/// Expands a pasted block of URLs into the concrete list to enqueue (TASK-074, US-16 AC3). It splits the
/// text on any whitespace (so newline- or space-separated lists both work) and ignores blank lines and
/// <c>#</c> comments, then expands numeric range patterns like <c>[001-100]</c> — preserving zero-padding
/// from the start token — into one URL per value. Multiple ranges in one URL expand as a cartesian product.
/// Pure and deterministic; the total expansion is capped to guard against an accidental explosion.
/// </summary>
public static partial class BatchUrlExpander
{
    /// <summary>The maximum number of URLs a single pattern may expand to before it is rejected.</summary>
    public const int MaxExpansionsPerUrl = 100_000;

    [GeneratedRegex(@"\[(\d+)-(\d+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex RangePattern();

    /// <summary>Expands a whole pasted block into individual URLs, in order, with ranges expanded.</summary>
    public static IReadOnlyList<string> Expand(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var result = new List<string>();

        // Comments are line-based, so split into lines first; each non-comment line may then hold several
        // whitespace-separated URLs.
        foreach (string line in text.Split('\n', '\r'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            foreach (string token in trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                result.AddRange(ExpandUrl(token));
            }
        }

        return result;
    }

    /// <summary>
    /// Expands a single URL's range patterns into one URL per value (the cartesian product across all
    /// ranges). A URL with no range pattern is returned unchanged.
    /// </summary>
    /// <exception cref="ArgumentException">The expansion would exceed <see cref="MaxExpansionsPerUrl"/>.</exception>
    public static IReadOnlyList<string> ExpandUrl(string url)
    {
        ArgumentNullException.ThrowIfNull(url);

        var frontier = new List<string> { url };
        while (true)
        {
            // Find the first remaining range token across the frontier; if none, we're done.
            bool anyMatch = frontier.Exists(s => RangePattern().IsMatch(s));
            if (!anyMatch)
            {
                return frontier;
            }

            var next = new List<string>();
            foreach (string s in frontier)
            {
                Match m = RangePattern().Match(s);
                if (!m.Success)
                {
                    next.Add(s);
                    continue;
                }

                string prefix = s[..m.Index];
                string suffix = s[(m.Index + m.Length)..];
                foreach (string value in RangeValues(m.Groups[1].Value, m.Groups[2].Value))
                {
                    next.Add(prefix + value + suffix);
                    if (next.Count > MaxExpansionsPerUrl)
                    {
                        throw new ArgumentException(
                            $"URL pattern '{url}' expands to more than {MaxExpansionsPerUrl} URLs.", nameof(url));
                    }
                }
            }

            frontier = next;
        }
    }

    private static List<string> RangeValues(string startToken, string endToken)
    {
        long start = long.Parse(startToken, NumberStyles.Integer, CultureInfo.InvariantCulture);
        long end = long.Parse(endToken, NumberStyles.Integer, CultureInfo.InvariantCulture);
        (long lo, long hi) = start <= end ? (start, end) : (end, start);
        bool ascending = start <= end;

        // Zero-pad to the wider token's width when the start token carries a leading zero (wget-style).
        int width = startToken.Length > 1 && startToken[0] == '0'
            ? Math.Max(startToken.Length, endToken.Length)
            : 0;

        var values = new List<string>();
        for (long i = lo; i <= hi; i++)
        {
            values.Add(i.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0'));
        }

        if (!ascending)
        {
            values.Reverse();
        }

        return values;
    }
}
