using JustDownload.Core.Data.Models;
using JustDownload.Core.Transport;

namespace JustDownload.Core.Lifecycle;

/// <summary>
/// Decides whether a renewed URL points at the same bytes as the original download (TASK-032, US-13 AC2-3).
/// A resume is only safe when the new resource is provably identical, so the rule is conservative: trust a
/// matching <c>ETag</c> first, fall back to a matching exact size, and treat "cannot confirm" as a mismatch
/// (forcing a clean restart rather than risking a corrupt, spliced file). Pure and unit-tested.
/// </summary>
public static class DownloadIdentity
{
    /// <summary>
    /// Whether the resource described by <paramref name="probe"/> is the same as the one recorded in
    /// <paramref name="record"/>, so an in-progress download may resume against the new URL.
    /// </summary>
    public static bool Matches(Download record, ResourceProbeResult probe)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(probe);

        // ETag is the strongest validator: equal entity tags mean identical content.
        if (!string.IsNullOrEmpty(record.ETag) && !string.IsNullOrEmpty(probe.ETag))
        {
            return string.Equals(record.ETag, probe.ETag, StringComparison.Ordinal);
        }

        // Otherwise require an exact size match (both sides must report a concrete length).
        if (record.TotalBytes is { } recorded && probe.TotalLength is { } current)
        {
            return recorded == current;
        }

        // No comparable validator — cannot confirm identity, so do not resume.
        return false;
    }
}
