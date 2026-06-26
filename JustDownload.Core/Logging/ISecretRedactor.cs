namespace JustDownload.Core.Logging;

/// <summary>
/// Masks secrets (Authorization headers, bearer/basic credentials, tokens, passwords, and
/// signed-URL query parameters) out of text before it reaches any log sink. This is the
/// privacy guardrail from CLAUDE.md §5 ("Logs redact auth headers, tokens, and signed-URL
/// query strings") — the engine's secrets must never be written to disk or console in the clear.
/// Implementations are pure and deterministic so the masking is unit-testable in isolation (§1).
/// </summary>
public interface ISecretRedactor
{
    /// <summary>
    /// Returns <paramref name="input"/> with any recognised secrets replaced by a constant mask.
    /// Non-secret text is returned unchanged. Never throws on user content.
    /// </summary>
    /// <param name="input">The candidate log text (message, header dump, URL, JSON fragment).</param>
    /// <returns>The redacted text. <see langword="null"/> in yields <see cref="string.Empty"/>.</returns>
    string Redact(string? input);
}
