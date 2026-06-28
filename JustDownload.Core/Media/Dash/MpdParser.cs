using System.Globalization;
using System.Xml.Linq;

namespace JustDownload.Core.Media.Dash;

/// <summary>
/// A pure, deterministic parser for DASH <c>.mpd</c> manifests (TASK-039), reduced to the progressive
/// separate-streams case: video and audio adaptation sets whose representations carry a <c>BaseURL</c>
/// pointing at a single media file. It resolves the MPD→Period→AdaptationSet→Representation
/// <c>BaseURL</c> chain against the manifest URI and classifies sets as video/audio by <c>contentType</c>
/// or <c>mimeType</c>. Namespace-agnostic (matches local element names). No I/O.
/// </summary>
public static class MpdParser
{
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
                    if (baseUrl is null)
                    {
                        continue; // SegmentTemplate/SegmentList-only — out of scope for progressive download.
                    }

                    Uri fileUri = ResolveBase(setBase, baseUrl);
                    DashRepresentation rep = BuildRepresentation(set, representation, fileUri);
                    (isVideo ? video : audio).Add(rep);
                }
            }
        }

        return new DashManifest(video, audio);
    }

    private static DashRepresentation BuildRepresentation(XElement set, XElement representation, Uri fileUri)
    {
        long bandwidth = GetLong(representation, "bandwidth") ?? 0;
        int? width = GetInt(representation, "width") ?? GetInt(set, "width");
        int? height = GetInt(representation, "height") ?? GetInt(set, "height");
        string? codecs = Attr(representation, "codecs") ?? Attr(set, "codecs");
        string? language = Attr(set, "lang") ?? Attr(representation, "lang");
        return new DashRepresentation(fileUri, bandwidth, width, height, codecs, language);
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
