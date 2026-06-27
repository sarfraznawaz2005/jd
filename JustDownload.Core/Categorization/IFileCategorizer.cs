namespace JustDownload.Core.Categorization;

/// <summary>
/// Resolves a downloaded file into a single <see cref="FileCategory"/> (PRD US-8) from its file name
/// (extension) and/or its HTTP <c>Content-Type</c>. Pure and deterministic — the same inputs always
/// yield the same category and nothing is mutated or observed — so it is trivially unit-testable and
/// safe to call on any thread or hot path.
/// </summary>
public interface IFileCategorizer
{
    /// <summary>
    /// Categorises a file from its name and/or content type. Resolution precedence is
    /// <b>extension first, then MIME, then <see cref="FileCategory.Other"/></b>: a recognised extension
    /// wins (it is what the file is saved as and what the user sees); when the extension is missing or
    /// unrecognised, the content type is consulted; if neither resolves, the result is
    /// <see cref="FileCategory.Other"/>.
    /// </summary>
    /// <param name="fileNameOrExtension">
    /// A file name (<c>"clip.mp4"</c>), a path whose final segment carries the extension, or a bare
    /// extension (<c>".mp4"</c> / <c>"mp4"</c>). May be <see langword="null"/> when only a content type
    /// is known.
    /// </param>
    /// <param name="contentType">
    /// An HTTP <c>Content-Type</c> / MIME type such as <c>"video/mp4"</c>; any <c>; charset=…</c>
    /// parameters are ignored. May be <see langword="null"/> when only a file name is known.
    /// </param>
    /// <returns>The resolved category, never throwing for unknown input — it falls back to
    /// <see cref="FileCategory.Other"/>.</returns>
    FileCategory Categorize(string? fileNameOrExtension, string? contentType = null);
}
