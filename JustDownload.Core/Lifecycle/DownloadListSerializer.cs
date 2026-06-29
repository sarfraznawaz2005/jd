using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JustDownload.Core.Lifecycle;

/// <summary>The interchange formats for a download list (TASK-140).</summary>
public enum DownloadListFormat
{
    /// <summary>Extended M3U playlist (<c>#EXTM3U</c> / <c>#EXTINF</c>) — one URL per line.</summary>
    M3u = 0,

    /// <summary>Comma-separated values with a <c>url,filename</c> header.</summary>
    Csv = 1,

    /// <summary>A JSON array of <c>{ url, fileName }</c> objects.</summary>
    Json = 2,
}

/// <summary>One entry in an exported download list (TASK-140): the source URL and an optional file name.</summary>
public sealed record DownloadListEntry
{
    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("fileName")]
    public string? FileName { get; init; }
}

/// <summary>
/// Serializes a download list to M3U/CSV/JSON and parses the URLs back out (TASK-140), so a queue can be
/// exported as a reusable list and a URL list imported. Parsing is lenient — it extracts the URLs it can and
/// ignores blank/comment/garbage lines — because imported lists come from many tools.
/// </summary>
public static class DownloadListSerializer
{
    /// <summary>Picks the format from a file extension (<c>.m3u</c>/<c>.m3u8</c>, <c>.csv</c>, else JSON).</summary>
    public static DownloadListFormat DetectFormat(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        string ext = Path.GetExtension(filePath);
        if (ext.Equals(".m3u", StringComparison.OrdinalIgnoreCase) || ext.Equals(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            return DownloadListFormat.M3u;
        }

        return ext.Equals(".csv", StringComparison.OrdinalIgnoreCase) ? DownloadListFormat.Csv : DownloadListFormat.Json;
    }

    public static string Serialize(IReadOnlyList<DownloadListEntry> entries, DownloadListFormat format)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return format switch
        {
            DownloadListFormat.M3u => SerializeM3u(entries),
            DownloadListFormat.Csv => SerializeCsv(entries),
            _ => JsonSerializer.Serialize(entries, DownloadListJsonContext.Default.IReadOnlyListDownloadListEntry),
        };
    }

    /// <summary>Extracts the source URLs from list <paramref name="content"/> in the given format.</summary>
    public static IReadOnlyList<string> ParseUrls(string content, DownloadListFormat format)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        return format switch
        {
            DownloadListFormat.M3u => ParseM3u(content),
            DownloadListFormat.Csv => ParseCsv(content),
            _ => ParseJson(content),
        };
    }

    private static string SerializeM3u(IReadOnlyList<DownloadListEntry> entries)
    {
        var sb = new StringBuilder("#EXTM3U\n");
        foreach (DownloadListEntry entry in entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.FileName))
            {
                sb.Append("#EXTINF:-1,").Append(entry.FileName).Append('\n');
            }

            sb.Append(entry.Url).Append('\n');
        }

        return sb.ToString();
    }

    private static string SerializeCsv(IReadOnlyList<DownloadListEntry> entries)
    {
        var sb = new StringBuilder("url,filename\n");
        foreach (DownloadListEntry entry in entries)
        {
            sb.Append(CsvField(entry.Url)).Append(',').Append(CsvField(entry.FileName ?? string.Empty)).Append('\n');
        }

        return sb.ToString();
    }

    private static string CsvField(string value)
    {
        // Quote when the value contains a comma, quote, or newline; escape embedded quotes by doubling (RFC 4180).
        if (value.IndexOfAny([',', '"', '\n', '\r']) < 0)
        {
            return value;
        }

        return string.Create(CultureInfo.InvariantCulture, $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"");
    }

    private static List<string> ParseM3u(string content)
    {
        var urls = new List<string>();
        foreach (string raw in content.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length > 0 && !line.StartsWith('#'))
            {
                urls.Add(line);
            }
        }

        return urls;
    }

    private static List<string> ParseCsv(string content)
    {
        var urls = new List<string>();
        bool first = true;
        foreach (string raw in content.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (first)
            {
                first = false;
                if (line.StartsWith("url", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // skip the header row
                }
            }

            string url = FirstCsvColumn(line);
            if (url.Length > 0)
            {
                urls.Add(url);
            }
        }

        return urls;
    }

    private static string FirstCsvColumn(string line)
    {
        if (line.StartsWith('"'))
        {
            int end = line.IndexOf('"', 1);
            return end > 0 ? line[1..end].Replace("\"\"", "\"", StringComparison.Ordinal) : line[1..];
        }

        int comma = line.IndexOf(',', StringComparison.Ordinal);
        return comma >= 0 ? line[..comma] : line;
    }

    private static List<string> ParseJson(string content)
    {
        try
        {
            IReadOnlyList<DownloadListEntry>? entries =
                JsonSerializer.Deserialize(content, DownloadListJsonContext.Default.IReadOnlyListDownloadListEntry);
            return entries is null
                ? []
                : entries.Where(e => !string.IsNullOrWhiteSpace(e.Url)).Select(e => e.Url).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(IReadOnlyList<DownloadListEntry>))]
internal sealed partial class DownloadListJsonContext : JsonSerializerContext;
