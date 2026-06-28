using System.Globalization;

namespace JustDownload.Core.Media.Hls;

/// <summary>
/// A pure, deterministic parser for HLS playlists (TASK-037, RFC 8216): master playlists into variant
/// streams (AC0) and media playlists into ordered segments with their AES-128 key info (AC2). All URIs are
/// resolved to absolute against the playlist's own URI. No I/O — given the text it returns the structure,
/// so it is fully unit-testable (CLAUDE.md §3).
/// </summary>
public static class M3U8Parser
{
    private const string StreamInfTag = "#EXT-X-STREAM-INF:";
    private const string KeyTag = "#EXT-X-KEY:";
    private const string InfTag = "#EXTINF:";
    private const string MediaSequenceTag = "#EXT-X-MEDIA-SEQUENCE:";
    private const string TargetDurationTag = "#EXT-X-TARGETDURATION:";
    private const string EndListTag = "#EXT-X-ENDLIST";

    /// <summary>Whether <paramref name="content"/> is a master playlist (has variant streams).</summary>
    public static bool IsMaster(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return content.Contains(StreamInfTag, StringComparison.Ordinal);
    }

    /// <summary>Parses a master playlist's variant streams, resolved against <paramref name="baseUri"/>.</summary>
    public static HlsMasterPlaylist ParseMaster(string content, Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(baseUri);

        var variants = new List<HlsVariant>();
        string[] lines = SplitLines(content);

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (!line.StartsWith(StreamInfTag, StringComparison.Ordinal))
            {
                continue;
            }

            Dictionary<string, string> attributes = ParseAttributes(line[StreamInfTag.Length..]);

            // The URI is on the next non-blank, non-comment line.
            Uri? uri = null;
            for (int j = i + 1; j < lines.Length; j++)
            {
                string candidate = lines[j];
                if (candidate.Length == 0 || candidate.StartsWith('#'))
                {
                    continue;
                }

                uri = ResolveUri(candidate, baseUri);
                i = j;
                break;
            }

            if (uri is null)
            {
                continue;
            }

            long bandwidth = GetLong(attributes, "BANDWIDTH") ?? GetLong(attributes, "AVERAGE-BANDWIDTH") ?? 0;
            (int? width, int? height) = ParseResolution(attributes);
            attributes.TryGetValue("CODECS", out string? codecs);

            variants.Add(new HlsVariant(uri, bandwidth, width, height, codecs));
        }

        return new HlsMasterPlaylist(variants);
    }

    /// <summary>Parses a media playlist's ordered segments, resolved against <paramref name="baseUri"/>.</summary>
    public static HlsMediaPlaylist ParseMedia(string content, Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(baseUri);

        var segments = new List<HlsSegment>();
        int targetDuration = 0;
        long startSequence = 0;
        bool isEndList = false;

        HlsEncryption currentKey = HlsEncryption.None;
        double pendingDuration = 0;
        bool haveInf = false;

        string[] lines = SplitLines(content);
        long sequence = 0;
        bool sequenceInitialised = false;

        foreach (string line in lines)
        {
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith(TargetDurationTag, StringComparison.Ordinal))
            {
                targetDuration = (int)(GetLongValue(line[TargetDurationTag.Length..]) ?? 0);
            }
            else if (line.StartsWith(MediaSequenceTag, StringComparison.Ordinal))
            {
                startSequence = GetLongValue(line[MediaSequenceTag.Length..]) ?? 0;
                sequence = startSequence;
                sequenceInitialised = true;
            }
            else if (line.StartsWith(KeyTag, StringComparison.Ordinal))
            {
                currentKey = ParseKey(line[KeyTag.Length..], baseUri);
            }
            else if (line.StartsWith(EndListTag, StringComparison.Ordinal))
            {
                isEndList = true;
            }
            else if (line.StartsWith(InfTag, StringComparison.Ordinal))
            {
                pendingDuration = ParseDuration(line[InfTag.Length..]);
                haveInf = true;
            }
            else if (!line.StartsWith('#'))
            {
                if (!sequenceInitialised)
                {
                    sequence = startSequence;
                    sequenceInitialised = true;
                }

                Uri uri = ResolveUri(line, baseUri);
                segments.Add(new HlsSegment(uri, haveInf ? pendingDuration : 0, sequence, currentKey));
                sequence++;
                pendingDuration = 0;
                haveInf = false;
            }
        }

        return new HlsMediaPlaylist(segments, targetDuration, startSequence, isEndList);
    }

    private static HlsEncryption ParseKey(string attributeText, Uri baseUri)
    {
        Dictionary<string, string> attributes = ParseAttributes(attributeText);
        attributes.TryGetValue("METHOD", out string? method);

        HlsKeyMethod keyMethod = method switch
        {
            "AES-128" => HlsKeyMethod.Aes128,
            "SAMPLE-AES" => HlsKeyMethod.SampleAes,
            _ => HlsKeyMethod.None,
        };

        if (keyMethod == HlsKeyMethod.None)
        {
            return HlsEncryption.None;
        }

        Uri? keyUri = attributes.TryGetValue("URI", out string? uriValue) && uriValue.Length > 0
            ? ResolveUri(uriValue, baseUri)
            : null;

        IReadOnlyList<byte>? iv = attributes.TryGetValue("IV", out string? ivValue)
            ? ParseHex(ivValue)
            : null;

        return new HlsEncryption(keyMethod, keyUri, iv);
    }

    /// <summary>Parses a hex IV (e.g. <c>0x0123…</c>) into bytes, or <see langword="null"/> if malformed.</summary>
    public static byte[]? ParseHex(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string hex = value.Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            hex.StartsWith("0X", StringComparison.OrdinalIgnoreCase))
        {
            hex = hex[2..];
        }

        if (hex.Length == 0 || hex.Length % 2 != 0)
        {
            return null;
        }

        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
            {
                return null;
            }

            bytes[i] = b;
        }

        return bytes;
    }

    private static (int? Width, int? Height) ParseResolution(Dictionary<string, string> attributes)
    {
        if (!attributes.TryGetValue("RESOLUTION", out string? resolution))
        {
            return (null, null);
        }

        string[] parts = resolution.Split('x', 'X');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int width) &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int height))
        {
            return (width, height);
        }

        return (null, null);
    }

    private static double ParseDuration(string infValue)
    {
        // #EXTINF:<duration>,<title>
        int comma = infValue.IndexOf(',', StringComparison.Ordinal);
        string durationText = comma >= 0 ? infValue[..comma] : infValue;
        return double.TryParse(durationText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
            ? d
            : 0;
    }

    private static long? GetLong(Dictionary<string, string> attributes, string key) =>
        attributes.TryGetValue(key, out string? value) ? GetLongValue(value) : null;

    private static long? GetLongValue(string value) =>
        long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
            ? parsed
            : null;

    private static Uri ResolveUri(string reference, Uri baseUri)
    {
        string trimmed = reference.Trim();
        return Uri.TryCreate(baseUri, trimmed, out Uri? resolved) ? resolved : new Uri(trimmed, UriKind.Absolute);
    }

    private static string[] SplitLines(string content) =>
        content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(l => l.Trim())
            .ToArray();

    /// <summary>
    /// Parses a comma-separated HLS attribute list (<c>KEY=value</c> / <c>KEY="quoted, value"</c>),
    /// honouring quotes so commas inside a quoted value do not split it.
    /// </summary>
    private static Dictionary<string, string> ParseAttributes(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        int i = 0;
        while (i < text.Length)
        {
            int eq = text.IndexOf('=', i);
            if (eq < 0)
            {
                break;
            }

            string key = text[i..eq].Trim();
            int valueStart = eq + 1;
            string value;

            if (valueStart < text.Length && text[valueStart] == '"')
            {
                int closing = text.IndexOf('"', valueStart + 1);
                if (closing < 0)
                {
                    value = text[(valueStart + 1)..];
                    i = text.Length;
                }
                else
                {
                    value = text[(valueStart + 1)..closing];
                    i = closing + 1;
                    if (i < text.Length && text[i] == ',')
                    {
                        i++;
                    }
                }
            }
            else
            {
                int comma = text.IndexOf(',', valueStart);
                if (comma < 0)
                {
                    value = text[valueStart..];
                    i = text.Length;
                }
                else
                {
                    value = text[valueStart..comma];
                    i = comma + 1;
                }
            }

            if (key.Length > 0)
            {
                result[key] = value.Trim();
            }
        }

        return result;
    }
}
