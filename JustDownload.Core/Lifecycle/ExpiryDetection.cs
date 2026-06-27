using System.Globalization;

namespace JustDownload.Core.Lifecycle;

/// <summary>
/// Pure detection of download-link expiry (TASK-032, US-13). Two signals: an HTTP status that conventionally
/// means a time-limited link has lapsed (<c>403</c>/<c>410</c>), and an expiry timestamp embedded in a signed
/// URL's query (S3/CloudFront <c>Expires</c>, AWS SigV4 <c>X-Amz-Date</c>+<c>X-Amz-Expires</c>, GCS
/// <c>X-Goog-*</c>, Azure SAS <c>se</c>). Both are side-effect-free so they are exhaustively unit-testable.
/// </summary>
public static class ExpiryDetection
{
    /// <summary>Whether an HTTP status conventionally indicates an expired/withdrawn link.</summary>
    public static bool IsExpiryStatusCode(int statusCode) => statusCode is 403 or 410;

    /// <summary>
    /// The expiry instant encoded in a signed URL's query, or <see langword="null"/> if the URL carries no
    /// recognized expiry parameter. When several are present the earliest is returned.
    /// </summary>
    public static DateTimeOffset? GetSignedUrlExpiry(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        Dictionary<string, string> query = ParseQuery(url);
        if (query.Count == 0)
        {
            return null;
        }

        DateTimeOffset? earliest = null;

        // S3 (legacy) / CloudFront / GCS V2: Expires = absolute Unix epoch seconds.
        if (query.TryGetValue("Expires", out string? expires) &&
            long.TryParse(expires, NumberStyles.Integer, CultureInfo.InvariantCulture, out long epoch))
        {
            Consider(ref earliest, DateTimeOffset.FromUnixTimeSeconds(epoch));
        }

        // AWS SigV4 presigned: X-Amz-Date (yyyyMMddTHHmmssZ) + X-Amz-Expires (lifetime seconds).
        Consider(ref earliest, RelativeExpiry(query, "X-Amz-Date", "X-Amz-Expires"));

        // GCS V4 presigned: X-Goog-Date + X-Goog-Expires (same format).
        Consider(ref earliest, RelativeExpiry(query, "X-Goog-Date", "X-Goog-Expires"));

        // Azure SAS: se = ISO 8601 absolute expiry.
        if (query.TryGetValue("se", out string? se) && DateTimeOffset.TryParse(
                se, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset azure))
        {
            Consider(ref earliest, azure);
        }

        return earliest;
    }

    /// <summary>Whether <paramref name="url"/> carries a signed-URL expiry that is at or before <paramref name="now"/>.</summary>
    public static bool IsUrlExpired(Uri url, DateTimeOffset now) => GetSignedUrlExpiry(url) is { } expiry && expiry <= now;

    private static DateTimeOffset? RelativeExpiry(Dictionary<string, string> query, string dateKey, string lifetimeKey)
    {
        if (query.TryGetValue(dateKey, out string? date) &&
            query.TryGetValue(lifetimeKey, out string? lifetime) &&
            int.TryParse(lifetime, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) &&
            DateTimeOffset.TryParseExact(
                date, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset start))
        {
            return start.AddSeconds(seconds);
        }

        return null;
    }

    private static void Consider(ref DateTimeOffset? earliest, DateTimeOffset? candidate)
    {
        if (candidate is { } value && (earliest is null || value < earliest))
        {
            earliest = value;
        }
    }

    private static Dictionary<string, string> ParseQuery(Uri url)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string query = url.IsAbsoluteUri ? url.Query : string.Empty;
        if (query.Length <= 1)
        {
            return result;
        }

        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            string key = Uri.UnescapeDataString(pair[..eq]);
            string value = Uri.UnescapeDataString(pair[(eq + 1)..]);
            result[key] = value;
        }

        return result;
    }
}
