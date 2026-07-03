namespace JustDownload.Core.NativeMessaging;

/// <summary>
/// Validates the calling extension's launch argument against the allowlist (TASK-064, US-11 AC4).
/// Browsers identify the caller when they spawn the host: Chromium passes the origin
/// <c>chrome-extension://&lt;id&gt;/</c> as an argument, Firefox passes the extension id. The host only
/// proceeds when one of the configured ids appears in that argument.
/// </summary>
public static class ExtensionOrigin
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="launchArgument"/> identifies an allowed
    /// extension. An empty allowlist or a missing/blank argument is rejected (fail closed).
    /// </summary>
    public static bool IsAllowed(string? launchArgument, IReadOnlyList<string> allowedExtensionIds)
    {
        ArgumentNullException.ThrowIfNull(allowedExtensionIds);
        if (string.IsNullOrWhiteSpace(launchArgument) || allowedExtensionIds.Count == 0)
        {
            return false;
        }

        foreach (string id in allowedExtensionIds)
        {
            if (!string.IsNullOrWhiteSpace(id) &&
                launchArgument.Contains(id, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Picks the extension launch argument from the process arguments. Browsers pass it as the first
    /// argument (the host's own executable path is not part of <c>args</c> in .NET).
    /// </summary>
    public static string? FromArguments(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        foreach (string argument in arguments)
        {
            if (argument.Contains("://", StringComparison.Ordinal) ||
                argument.Contains('@', StringComparison.Ordinal))
            {
                return argument;
            }
        }

        return arguments.Count > 0 ? arguments[0] : null;
    }

    /// <summary>
    /// Categorizes an already-validated <paramref name="launchArgument"/> (one that passed
    /// <see cref="IsAllowed"/>) into a browser family for contact tracking (TASK-175), or
    /// <see langword="null"/> if it doesn't actually match either known id — defensive, since this is only
    /// ever called after <see cref="IsAllowed"/> already confirmed a match.
    /// </summary>
    public static ExtensionContactOrigin? Categorize(string launchArgument)
    {
        ArgumentException.ThrowIfNullOrEmpty(launchArgument);

        if (NativeHostIdentity.ChromiumExtensionId is { Length: > 0 } chromiumId &&
            launchArgument.Contains(chromiumId, StringComparison.OrdinalIgnoreCase))
        {
            return ExtensionContactOrigin.Chromium;
        }

        return launchArgument.Contains(NativeHostIdentity.FirefoxExtensionId, StringComparison.OrdinalIgnoreCase)
            ? ExtensionContactOrigin.Firefox
            : null;
    }
}
