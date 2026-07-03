using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace JustDownload.Core.Media.Dash;

/// <summary>
/// A pure, deterministic parser for DASH <c>.mpd</c> manifests (TASK-039/102): video and audio adaptation
/// sets whose representations are addressed either by a <c>BaseURL</c> (a single progressive file) or by
/// <c>SegmentTemplate</c>/<c>SegmentList</c> (an init segment + an ordered sequence of media segments). It
/// resolves the MPD→Period→AdaptationSet→Representation <c>BaseURL</c> chain against the manifest URI and
/// classifies sets as video/audio by <c>contentType</c> or <c>mimeType</c>. Namespace-agnostic (matches local
/// element names). No I/O.
/// <para>
/// A SegmentTemplate/SegmentList representation cannot be reduced to one URL up front (TASK-102): instead
/// <see cref="Parse"/> gives it an identifier URI — the manifest URL plus its representation id
/// (<see cref="BuildRepresentationUri"/>) — and its actual segment list is resolved later, at download time,
/// by re-fetching the manifest and calling <see cref="ResolveSegments"/> (the same "resolve again from a
/// stored URL" shape the HLS variant-playlist path already uses).
/// </para>
/// </summary>
public static class MpdParser
{
    private const string RepresentationFragmentPrefix = "dash-rep=";

    /// <summary>Parses <paramref name="xml"/> into the downloadable video/audio representations.</summary>
    public static DashManifest Parse(string xml, Uri manifestUri)
    {
        ArgumentNullException.ThrowIfNull(xml);
        ArgumentNullException.ThrowIfNull(manifestUri);

        XDocument document = XDocument.Parse(xml);
        XElement? mpd = document.Root;
        if (mpd is null || !LocalNameIs(mpd, "MPD"))
        {
            return new DashManifest([], []);
        }

        Uri mpdBase = ResolveBase(manifestUri, FirstBaseUrl(mpd));

        var video = new List<DashRepresentation>();
        var audio = new List<DashRepresentation>();
        bool isSegmented = false;

        foreach (XElement period in Children(mpd, "Period"))
        {
            Uri periodBase = ResolveBase(mpdBase, FirstBaseUrl(period));

            foreach (XElement set in Children(period, "AdaptationSet"))
            {
                Uri setBase = ResolveBase(periodBase, FirstBaseUrl(set));
                bool isVideo = ClassifyIsVideo(set, out bool isAudio);
                if (!isVideo && !isAudio)
                {
                    continue;
                }

                foreach (XElement representation in Children(set, "Representation"))
                {
                    string? baseUrl = FirstBaseUrl(representation);
                    if (baseUrl is not null)
                    {
                        Uri fileUri = ResolveBase(setBase, baseUrl);
                        (isVideo ? video : audio).Add(BuildRepresentation(set, representation, fileUri));
                        continue;
                    }

                    string? id = Attr(representation, "id");
                    if (id is null)
                    {
                        continue; // Can't build a stable, re-resolvable identifier without a representation id.
                    }

                    List<Uri>? segments = TryResolveSegments(period, set, representation, setBase);
                    if (segments is null || segments.Count == 0)
                    {
                        continue; // SegmentTemplate/SegmentList present but unresolvable (e.g. no duration or
                                  // timeline info; a dynamic/live manifest) — degrade gracefully.
                    }

                    Uri identifierUri = BuildRepresentationUri(manifestUri, id);
                    (isVideo ? video : audio).Add(BuildRepresentation(set, representation, identifierUri));
                    isSegmented = true;
                }
            }
        }

        return new DashManifest(video, audio, isSegmented);
    }

    /// <summary>
    /// Resolves the ordered segment URIs (an optional init segment first, then media segments) for the
    /// representation identified by <paramref name="representationId"/> in the manifest <paramref name="xml"/>
    /// (TASK-102). Called at download time from the identifier URI <see cref="Parse"/> produced for a
    /// SegmentTemplate/SegmentList representation (see <see cref="TryParseRepresentationUri"/>). Returns
    /// <see langword="null"/> when the manifest is not a well-formed MPD or the representation/its segments
    /// can no longer be resolved.
    /// </summary>
    public static IReadOnlyList<Uri>? ResolveSegments(string xml, Uri manifestUri, string representationId)
    {
        ArgumentNullException.ThrowIfNull(xml);
        ArgumentNullException.ThrowIfNull(manifestUri);
        ArgumentException.ThrowIfNullOrEmpty(representationId);

        XDocument document = XDocument.Parse(xml);
        XElement? mpd = document.Root;
        if (mpd is null || !LocalNameIs(mpd, "MPD"))
        {
            return null;
        }

        Uri mpdBase = ResolveBase(manifestUri, FirstBaseUrl(mpd));

        foreach (XElement period in Children(mpd, "Period"))
        {
            Uri periodBase = ResolveBase(mpdBase, FirstBaseUrl(period));

            foreach (XElement set in Children(period, "AdaptationSet"))
            {
                Uri setBase = ResolveBase(periodBase, FirstBaseUrl(set));

                foreach (XElement representation in Children(set, "Representation"))
                {
                    if (Attr(representation, "id") != representationId)
                    {
                        continue;
                    }

                    string? baseUrl = FirstBaseUrl(representation);
                    return baseUrl is not null
                        ? [ResolveBase(setBase, baseUrl)]
                        : TryResolveSegments(period, set, representation, setBase);
                }
            }
        }

        return null;
    }

    /// <summary>Builds the identifier URI <see cref="Parse"/> gives a SegmentTemplate/SegmentList representation.</summary>
    private static Uri BuildRepresentationUri(Uri manifestUri, string representationId)
    {
        string basePart = manifestUri.GetLeftPart(UriPartial.Query);
        return new Uri(basePart + "#" + RepresentationFragmentPrefix + Uri.EscapeDataString(representationId));
    }

    /// <summary>
    /// Splits an identifier URI built by <see cref="Parse"/> back into the manifest URI to re-fetch and the
    /// representation id to look up (TASK-102), for <see cref="ResolveSegments"/> at download time.
    /// </summary>
    public static bool TryParseRepresentationUri(
        Uri representationUri, [NotNullWhen(true)] out Uri? manifestUri, [NotNullWhen(true)] out string? representationId)
    {
        ArgumentNullException.ThrowIfNull(representationUri);

        string fragment = representationUri.Fragment; // includes the leading '#', percent-encoded.
        string prefix = "#" + RepresentationFragmentPrefix;
        if (!fragment.StartsWith(prefix, StringComparison.Ordinal))
        {
            manifestUri = null;
            representationId = null;
            return false;
        }

        representationId = Uri.UnescapeDataString(fragment[prefix.Length..]);
        manifestUri = new Uri(representationUri.GetLeftPart(UriPartial.Query));
        return true;
    }

    private static DashRepresentation BuildRepresentation(XElement set, XElement representation, Uri uri)
    {
        long bandwidth = GetLong(representation, "bandwidth") ?? 0;
        int? width = GetInt(representation, "width") ?? GetInt(set, "width");
        int? height = GetInt(representation, "height") ?? GetInt(set, "height");
        string? codecs = Attr(representation, "codecs") ?? Attr(set, "codecs");
        string? language = Attr(set, "lang") ?? Attr(representation, "lang");
        return new DashRepresentation(uri, bandwidth, width, height, codecs, language);
    }

    private static bool ClassifyIsVideo(XElement set, out bool isAudio)
    {
        string contentType = (Attr(set, "contentType") ?? string.Empty).ToLowerInvariant();
        string mimeType = (Attr(set, "mimeType") ?? string.Empty).ToLowerInvariant();

        bool isVideo = contentType == "video" || mimeType.StartsWith("video/", StringComparison.Ordinal);
        isAudio = contentType == "audio" || mimeType.StartsWith("audio/", StringComparison.Ordinal);

        if (!isVideo && !isAudio)
        {
            // Fall back to the child representations' mimeType.
            foreach (XElement rep in Children(set, "Representation"))
            {
                string repMime = (Attr(rep, "mimeType") ?? string.Empty).ToLowerInvariant();
                if (repMime.StartsWith("video/", StringComparison.Ordinal))
                {
                    return true;
                }

                if (repMime.StartsWith("audio/", StringComparison.Ordinal))
                {
                    isAudio = true;
                    return false;
                }
            }
        }

        return isVideo;
    }

    // --- SegmentTemplate / SegmentList resolution (TASK-102) ---------------------------------------

    /// <summary>
    /// Resolves the effective SegmentTemplate/SegmentList for <paramref name="representation"/> — its own
    /// takes precedence over its <paramref name="set"/>'s (DASH inheritance) — into an ordered segment list.
    /// </summary>
    private static List<Uri>? TryResolveSegments(
        XElement period, XElement set, XElement representation, Uri effectiveBase)
    {
        XElement? template = Children(representation, "SegmentTemplate").FirstOrDefault()
            ?? Children(set, "SegmentTemplate").FirstOrDefault();
        if (template is not null)
        {
            return ResolveSegmentTemplate(period, representation, template, effectiveBase);
        }

        XElement? list = Children(representation, "SegmentList").FirstOrDefault()
            ?? Children(set, "SegmentList").FirstOrDefault();
        return list is not null ? ResolveSegmentList(list, effectiveBase) : null;
    }

    private static List<Uri>? ResolveSegmentTemplate(
        XElement period, XElement representation, XElement template, Uri effectiveBase)
    {
        string? mediaTemplate = Attr(template, "media");
        if (mediaTemplate is null)
        {
            return null; // Nothing to address media segments with.
        }

        long timescale = GetLong(template, "timescale") is { } ts and > 0 ? ts : 1;
        long startNumber = GetLong(template, "startNumber") ?? 1;

        List<(long Time, long Duration)>? entries;
        XElement? timeline = Children(template, "SegmentTimeline").FirstOrDefault();
        if (timeline is not null)
        {
            entries = ExpandTimeline(timeline);
        }
        else
        {
            long? segmentDuration = GetLong(template, "duration");
            if (segmentDuration is not (> 0))
            {
                return null; // No timeline and no fixed duration — cannot determine segment count.
            }

            double segmentSeconds = (double)segmentDuration.Value / timescale;
            long count;
            long? endNumber = GetLong(template, "endNumber");
            if (endNumber is { } end)
            {
                count = end - startNumber + 1;
            }
            else
            {
                double? totalSeconds = GetPeriodOrMpdDurationSeconds(period);
                if (totalSeconds is null)
                {
                    return null; // A static count needs a known total duration (dynamic/live MPDs — out of scope).
                }

                count = (long)Math.Ceiling(totalSeconds.Value / segmentSeconds);
            }

            if (count <= 0)
            {
                return null;
            }

            entries = new List<(long Time, long Duration)>((int)Math.Min(count, int.MaxValue));
            long time = 0;
            for (long i = 0; i < count; i++)
            {
                entries.Add((time, segmentDuration.Value));
                time += segmentDuration.Value;
            }
        }

        if (entries.Count == 0)
        {
            return null;
        }

        string representationId = Attr(representation, "id") ?? string.Empty;
        long bandwidth = GetLong(representation, "bandwidth") ?? 0;

        var uris = new List<Uri>(entries.Count + 1);
        string? initTemplate = Attr(template, "initialization");
        if (initTemplate is { Length: > 0 })
        {
            string initString = Substitute(initTemplate, representationId, bandwidth, number: null, time: null);
            uris.Add(ResolveBase(effectiveBase, initString));
        }

        for (int i = 0; i < entries.Count; i++)
        {
            long number = startNumber + i;
            string mediaString = Substitute(mediaTemplate, representationId, bandwidth, number, entries[i].Time);
            uris.Add(ResolveBase(effectiveBase, mediaString));
        }

        return uris;
    }

    /// <summary>Expands a <c>SegmentTimeline</c>'s <c>S</c> elements into ordered (start time, duration) entries.</summary>
    private static List<(long Time, long Duration)> ExpandTimeline(XElement timelineElement)
    {
        List<XElement> entries = Children(timelineElement, "S").ToList();
        var expanded = new List<(long Time, long Duration)>();
        long cursor = 0;

        for (int i = 0; i < entries.Count; i++)
        {
            XElement s = entries[i];
            long time = GetLong(s, "t") ?? cursor;
            long duration = GetLong(s, "d") ?? 0;
            long repeat = GetLong(s, "r") ?? 0;

            if (repeat < 0)
            {
                // "Repeat until the next S's start (or the end of the period)" — only the bounded case (a
                // following S with an explicit start) is supported; an open-ended trailing r=-1 belongs to a
                // dynamic/live timeline, which is out of scope (TASK-102), so it degrades to a single instance.
                long? nextTime = i + 1 < entries.Count ? GetLong(entries[i + 1], "t") : null;
                repeat = nextTime is { } next && duration > 0 ? Math.Max(0, (next - time) / duration - 1) : 0;
            }

            for (long r = 0; r <= repeat; r++)
            {
                expanded.Add((time, duration));
                time += duration;
            }

            cursor = time;
        }

        return expanded;
    }

    private static List<Uri>? ResolveSegmentList(XElement list, Uri effectiveBase)
    {
        var uris = new List<Uri>();

        XElement? init = Children(list, "Initialization").FirstOrDefault();
        string? initSourceUrl = init is not null ? Attr(init, "sourceURL") : null;
        if (initSourceUrl is { Length: > 0 })
        {
            uris.Add(ResolveBase(effectiveBase, initSourceUrl));
        }

        foreach (XElement segmentUrl in Children(list, "SegmentURL"))
        {
            string? media = Attr(segmentUrl, "media");
            if (media is { Length: > 0 })
            {
                uris.Add(ResolveBase(effectiveBase, media));
            }
        }

        return uris.Count > 0 ? uris : null;
    }

    /// <summary>The total content duration in seconds, from <c>Period@duration</c> else <c>MPD@mediaPresentationDuration</c>.</summary>
    private static double? GetPeriodOrMpdDurationSeconds(XElement period)
    {
        XElement? mpd = period.Parent;
        if (Attr(period, "duration") is { Length: > 0 } periodDuration &&
            TryParseIsoDuration(periodDuration, out double periodSeconds))
        {
            return periodSeconds;
        }

        if (mpd is not null && Attr(mpd, "mediaPresentationDuration") is { Length: > 0 } mpdDuration &&
            TryParseIsoDuration(mpdDuration, out double mpdSeconds))
        {
            return mpdSeconds;
        }

        return null;
    }

    private static bool TryParseIsoDuration(string value, out double seconds)
    {
        try
        {
            seconds = XmlConvert.ToTimeSpan(value).TotalSeconds;
            return true;
        }
        catch (FormatException)
        {
            seconds = 0;
            return false;
        }
    }

    /// <summary>Substitutes <c>$RepresentationID$</c>/<c>$Bandwidth$</c>/<c>$Number$</c>/<c>$Time$</c>/<c>$$</c> tokens.</summary>
    private static string Substitute(string template, string representationId, long bandwidth, long? number, long? time)
    {
        var result = new StringBuilder(template.Length);
        int i = 0;
        while (i < template.Length)
        {
            char c = template[i];
            if (c != '$')
            {
                result.Append(c);
                i++;
                continue;
            }

            int end = template.IndexOf('$', i + 1);
            if (end < 0)
            {
                result.Append(c); // Stray unmatched '$' — keep it literally.
                i++;
                continue;
            }

            result.Append(ExpandToken(template[(i + 1)..end], representationId, bandwidth, number, time));
            i = end + 1;
        }

        return result.ToString();
    }

    private static string ExpandToken(string token, string representationId, long bandwidth, long? number, long? time)
    {
        if (token.Length == 0)
        {
            return "$"; // "$$" is a literal '$'.
        }

        int percent = token.IndexOf('%', StringComparison.Ordinal);
        string name = percent >= 0 ? token[..percent] : token;
        string? format = percent >= 0 ? token[percent..] : null;

        string? raw = name switch
        {
            "RepresentationID" => representationId,
            "Bandwidth" => bandwidth.ToString(CultureInfo.InvariantCulture),
            "Number" => number?.ToString(CultureInfo.InvariantCulture),
            "Time" => time?.ToString(CultureInfo.InvariantCulture),
            _ => null,
        };

        if (raw is null)
        {
            return "$" + token + "$"; // Unknown/inapplicable identifier — leave untouched rather than guess.
        }

        return format is null ? raw : ApplyWidth(raw, format);
    }

    /// <summary>Applies a <c>%0Nd</c> zero-pad width specifier (e.g. <c>$Number%05d$</c>).</summary>
    private static string ApplyWidth(string digits, string format)
    {
        int dIndex = format.IndexOf('d', StringComparison.Ordinal);
        if (dIndex < 1)
        {
            return digits;
        }

        string widthPart = format[1..dIndex].TrimStart('0');
        return int.TryParse(widthPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int width)
            ? digits.PadLeft(width, '0')
            : digits;
    }

    // --- Shared tree helpers -------------------------------------------------------------------------

    private static string? FirstBaseUrl(XElement element) =>
        Children(element, "BaseURL").FirstOrDefault()?.Value.Trim() is { Length: > 0 } value ? value : null;

    private static Uri ResolveBase(Uri current, string? relative) =>
        relative is null ? current : Uri.TryCreate(current, relative, out Uri? resolved) ? resolved : current;

    private static IEnumerable<XElement> Children(XElement parent, string localName) =>
        parent.Elements().Where(e => LocalNameIs(e, localName));

    private static bool LocalNameIs(XElement element, string localName) =>
        string.Equals(element.Name.LocalName, localName, StringComparison.Ordinal);

    private static string? Attr(XElement element, string name) =>
        element.Attribute(name)?.Value;

    private static long? GetLong(XElement element, string name) =>
        long.TryParse(Attr(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
            ? value
            : null;

    private static int? GetInt(XElement element, string name) =>
        int.TryParse(Attr(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : null;
}
