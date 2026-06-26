using System.Text.RegularExpressions;

namespace JustDownload.Core.Logging;

/// <summary>
/// Default <see cref="ISecretRedactor"/>. A pure, allocation-light masker built on
/// source-generated regexes (no runtime compile, friendly to the light-&-fast budget). It covers
/// the four secret shapes called out in CLAUDE.md §5:
/// <list type="bullet">
///   <item><description><c>Authorization</c> headers (any scheme).</description></item>
///   <item><description>Bare <c>Bearer</c>/<c>Basic</c>/<c>Digest</c> credentials.</description></item>
///   <item><description>Token / password / secret key-value pairs (headers, JSON, form bodies).</description></item>
///   <item><description>Signed-URL query parameters (SAS <c>sig</c>, AWS SigV4, generic <c>token</c>/<c>key</c>).</description></item>
/// </list>
/// The replacement is a constant so logs are still readable ("an Authorization header was present")
/// without ever leaking the value. Matching is deliberately broad on the key side and tight on the
/// value side so prose is left untouched but real secrets are masked.
/// </summary>
public sealed partial class SecretRedactor : ISecretRedactor
{
    /// <summary>The constant token that replaces every redacted secret value.</summary>
    public const string Mask = "***REDACTED***";

    /// <inheritdoc />
    public string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        // Order matters only for readability; every pattern is idempotent against the mask.
        string result = AuthorizationHeaderRegex().Replace(input, "${key}${sep}" + Mask);
        result = SchemeCredentialRegex().Replace(result, "${scheme} " + Mask);
        result = QueryParamRegex().Replace(result, "${key}" + Mask);
        result = KeyValueRegex().Replace(result, "${key}${sep}" + Mask);
        return result;
    }

    // "Authorization: Bearer abc", '"authorization":"Basic x=="', "authorization=Bearer x".
    // The value runs to the first delimiter (quote, comma, newline, closing brace/bracket, '&').
    [GeneratedRegex(
        @"(?<key>authorization)(?<sep>\s*[""']?\s*[:=]\s*[""']?)(?<value>[^""'\r\n,&}\]]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationHeaderRegex();

    // A bare "Bearer <token>" / "Basic <creds>" / "Digest <creds>" anywhere in the text.
    // Requires a credential-shaped run of >=8 chars so ordinary prose ("bearer of bad news") is safe.
    [GeneratedRegex(
        @"(?<scheme>bearer|basic|digest)\s+[A-Za-z0-9\-._~+/]{8,}={0,2}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SchemeCredentialRegex();

    // Signed-URL query parameters: "?sig=...", "&X-Amz-Signature=...", "&token=...", "&api_key=...".
    [GeneratedRegex(
        @"(?<key>[?&](?:sig|signature|x-amz-signature|x-amz-credential|x-amz-security-token|awsaccesskeyid|policy|token|access_token|api[_-]?key|apikey|password|pwd|secret|key)=)[^&\s#""']+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QueryParamRegex();

    // Token / password / secret key-value pairs in headers, JSON, or form bodies.
    // Longer keys precede shorter ones so "access_token" wins over a bare "token".
    [GeneratedRegex(
        @"(?<key>\b(?:access[_-]?token|refresh[_-]?token|id[_-]?token|client[_-]?secret|x-api-key|api[_-]?key|auth[_-]?token|session[_-]?id|password|passwd|pwd|secret|token|cookie)\b)(?<sep>\s*[""']?\s*[:=]\s*[""']?)(?<value>[^""'\r\n,&}\]\s]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueRegex();
}
