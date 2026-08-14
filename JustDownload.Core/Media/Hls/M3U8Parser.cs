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
    private const string MediaTag = "#EXT-X-MEDIA:";
    private const string KeyTag = "#EXT-X-KEY:";
    private const string InfTag = "#EXTINF:";
    private const string MediaSequenceTag = "#EXT-X-MEDIA-SEQUENCE:";
    private const string MapTag = "#EXT-X-MAP:";
    private const string ByteRangeTag = "#EXT-X-BYTERANGE:";
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
        var audioRenditions = new List<HlsAudioRendition>();
        string[] lines = SplitLines(content);

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];

            // Checked first: "#EXT-X-MEDIA:" and "#EXT-X-STREAM-INF:" are distinct tags, but this also isn't
            // "#EXT-X-MEDIA-SEQUENCE:" (a master playlist tag of its own) — the colon position tells them apart.
            if (line.StartsWith(MediaTag, StringComparison.Ordinal))
            {
                if (TryParseAudioRendition(line[MediaTag.Length..], baseUri) is { } rendition)
                {
                    audioRenditions.Add(rendition);
                }

                continue;
            }

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

        return new HlsMasterPlaylist(variants, audioRenditions);
    }

    /// <summary>
    /// Parses one <c>#EXT-X-MEDIA:</c> attribute list into an <see cref="HlsAudioRendition"/>, or
    /// <see langword="null"/> when it isn't a downloadable audio rendition: not <c>TYPE=AUDIO</c>, missing
    /// <c>URI</c> (audio already muxed into the video segments — nothing separate to fetch), or missing the
    /// required <c>GROUP-ID</c> (RFC 8216 §4.3.4.1).
    /// </summary>
    private static HlsAudioRendition? TryParseAudioRendition(string attributeText, Uri baseUri)
    {
        Dictionary<string, string> attributes = ParseAttributes(attributeText);

        if (!attributes.TryGetValue("TYPE", out string? type) ||
            !string.Equals(type, "AUDIO", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!attributes.TryGetValue("URI", out string? uriValue) || uriValue.Length == 0)
        {
            return null;
        }

        if (!attributes.TryGetValue("GROUP-ID", out string? groupId) || groupId.Length == 0)
        {
            return null;
        }

        attributes.TryGetValue("NAME", out string? name);
        attributes.TryGetValue("LANGUAGE", out string? language);
        bool isDefault = attributes.TryGetValue("DEFAULT", out string? defaultValue) &&
            string.Equals(defaultValue, "YES", StringComparison.OrdinalIgnoreCase);

        return new HlsAudioRendition(ResolveUri(uriValue, baseUri), groupId, name, language, isDefault);
    }

    /// <summary>Parses a media playlist's ordered segments, resolved against <paramref name="baseUri"/>.</summary>
    /// <exception cref="HlsExtractionException">
    /// A <c>#EXT-X-BYTERANGE</c> (or an <c>#EXT-X-MAP</c> <c>BYTERANGE</c>) is malformed, or omits its offset
    /// with no earlier sub-range of the same resource to continue from. Such a playlist cannot be downloaded
    /// correctly, and treating it as an unranged one would silently produce a wrong file.
    /// </exception>
    public static HlsMediaPlaylist ParseMedia(string content, Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(baseUri);

        var segments = new List<HlsSegment>();
        int targetDuration = 0;
        long startSequence = 0;
        bool isEndList = false;

        HlsEncryption currentKey = HlsEncryption.None;
        HlsInitializationSegment? initializationSegment = null;
        double pendingDuration = 0;
        bool haveInf = false;
        string? pendingByteRange = null;

        // Where the next offset-less #EXT-X-BYTERANGE of each resource continues from (RFC 8216 §4.3.2.2).
        var nextByteRangeOffsets = new Dictionary<Uri, long>();

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
            else if (line.StartsWith(MapTag, StringComparison.Ordinal))
            {
                // Only the first #EXT-X-MAP is kept: a second one re-initialises the decoder mid-stream, which
                // this byte-append pipeline cannot express, and silently appending it would corrupt the output.
                initializationSegment ??= ParseMap(line[MapTag.Length..], baseUri, nextByteRangeOffsets);
            }
            else if (line.StartsWith(ByteRangeTag, StringComparison.Ordinal))
            {
                // Applies to the next segment URI; its offset can only be resolved once that URI is known.
                pendingByteRange = line[ByteRangeTag.Length..];
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
                HlsByteRange? byteRange = pendingByteRange is { } rangeText
                    ? ResolveByteRange(rangeText, uri, nextByteRangeOffsets)
                    : null;

                segments.Add(new HlsSegment(uri, haveInf ? pendingDuration : 0, sequence, currentKey, byteRange));
                sequence++;
                pendingDuration = 0;
                haveInf = false;
                pendingByteRange = null;
            }
        }

        return new HlsMediaPlaylist(segments, targetDuration, startSequence, isEndList, initializationSegment);
    }

    private static HlsInitializationSegment? ParseMap(
        string attributeText, Uri baseUri, Dictionary<Uri, long> nextByteRangeOffsets)
    {
        Dictionary<string, string> attributes = ParseAttributes(attributeText);
        if (!attributes.TryGetValue("URI", out string? uriValue) || uriValue.Length == 0)
        {
            return null;
        }

        Uri uri = ResolveUri(uriValue, baseUri);
        HlsByteRange? byteRange = attributes.TryGetValue("BYTERANGE", out string? rangeText) && rangeText.Length > 0
            ? ResolveByteRange(rangeText, uri, nextByteRangeOffsets)
            : null;

        return new HlsInitializationSegment(uri, byteRange);
    }

    /// <summary>
    /// Resolves a <c>&lt;length&gt;[@&lt;offset&gt;]</c> byte range against <paramref name="uri"/>, continuing from the
    /// previous sub-range of the same resource when the offset is omitted (RFC 8216 §4.3.2.2), and recording
    /// where the next one would continue from.
    /// </summary>
    private static HlsByteRange ResolveByteRange(string text, Uri uri, Dictionary<Uri, long> nextByteRangeOffsets)
    {
        string value = text.Trim();
        int at = value.IndexOf('@', StringComparison.Ordinal);
        string lengthText = at >= 0 ? value[..at] : value;

        if (GetLongValue(lengthText) is not { } length || length <= 0)
        {
            throw new HlsExtractionException(
                $"Byte range '{value}' for '{uri}' does not declare a usable length (RFC 8216 §4.3.2.2).");
        }

        long offset;
        if (at >= 0)
        {
            if (GetLongValue(value[(at + 1)..]) is not { } declaredOffset || declaredOffset < 0)
            {
                throw new HlsExtractionException(
                    $"Byte range '{value}' for '{uri}' declares an invalid offset (RFC 8216 §4.3.2.2).");
            }

            offset = declaredOffset;
        }
        else if (!nextByteRangeOffsets.TryGetValue(uri, out offset))
        {
            throw new HlsExtractionException(
                $"Byte range '{value}' for '{uri}' omits its offset, but no earlier sub-range of that " +
                "resource precedes it (RFC 8216 §4.3.2.2).");
        }

        nextByteRangeOffsets[uri] = offset + length;
        return new HlsByteRange(length, offset);
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
