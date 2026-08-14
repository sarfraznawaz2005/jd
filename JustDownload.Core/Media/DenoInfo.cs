namespace JustDownload.Core.Media;

/// <summary>A located Deno executable and its reported version.</summary>
/// <param name="ExecutablePath">The path/name used to invoke Deno (may resolve via <c>PATH</c>).</param>
/// <param name="Version">The version string parsed from <c>deno --version</c>, e.g. <c>2.9.5</c>.</param>
public sealed record DenoInfo(string ExecutablePath, string Version);
