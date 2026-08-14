namespace JustDownload.Core.Media;

/// <summary>
/// A pinned, integrity-checked Deno archive for one runtime identifier. Deno is MIT-licensed and ships one
/// standalone executable zipped per platform — like ffmpeg's builds (D7), download-on-first-use fetches the
/// <see cref="Url"/>, verifies it against <see cref="Sha256"/>, and extracts the single executable entry
/// into the vendor directory; unlike ffmpeg there is no <c>bin/</c> folder or side-by-side libraries.
/// </summary>
/// <param name="RuntimeIdentifier">The .NET RID this build targets, e.g. <c>win-x64</c>.</param>
/// <param name="Version">The Deno release tag, e.g. <c>v2.9.5</c>.</param>
/// <param name="Url">The HTTPS download URL of the (zip) archive.</param>
/// <param name="Sha256">The expected lower-case SHA-256 hex digest of the archive.</param>
public sealed record DenoDownloadSource(string RuntimeIdentifier, string Version, Uri Url, string Sha256);
