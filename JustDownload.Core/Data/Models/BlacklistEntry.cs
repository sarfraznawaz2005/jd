namespace JustDownload.Core.Data.Models;

/// <summary>
/// A per-site blacklist row (PRD §4.4 <c>site_blacklist</c> table, US-12): a domain on which the
/// extension's floating video button must never appear. Both fields together form the natural key,
/// so a domain can be blacklisted independently per <see cref="Scope"/>.
/// </summary>
public sealed record BlacklistEntry
{
    /// <summary>The blacklisted domain (e.g. <c>example.com</c>).</summary>
    public required string Domain { get; init; }

    /// <summary>The scope the blacklist applies to (e.g. <c>button</c> / <c>extension</c> / <c>app</c>).</summary>
    public required string Scope { get; init; }
}
