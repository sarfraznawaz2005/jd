namespace JustDownload.Core.Transport;

/// <summary>The three browsers <see cref="BrowserUserAgentDetector"/> checks, in preference order.</summary>
internal enum BrowserKind
{
    Chrome,
    Edge,
    Firefox,
}

/// <summary>
/// Builds a realistic desktop User-Agent string for a detected browser + version. Pure, and the OS platform
/// token is an explicit parameter on the internal overload so the string format itself is fully unit-testable
/// without depending on the actual running OS.
/// </summary>
internal static class BrowserUserAgentFormatter
{
    public static string Build(BrowserKind kind, string version) => Build(kind, version, PlatformToken());

    internal static string Build(BrowserKind kind, string version, string platformToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(platformToken);

        return kind switch
        {
            BrowserKind.Chrome =>
                $"Mozilla/5.0 ({platformToken}) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{version} Safari/537.36",
            BrowserKind.Edge =>
                $"Mozilla/5.0 ({platformToken}) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{version} Safari/537.36 Edg/{version}",
            BrowserKind.Firefox =>
                $"Mozilla/5.0 ({platformToken}; rv:{version}) Gecko/20100101 Firefox/{version}",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown browser kind."),
        };
    }

    private static string PlatformToken()
    {
        if (OperatingSystem.IsWindows())
        {
            return "Windows NT 10.0; Win64; x64";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "Macintosh; Intel Mac OS X 10_15_7";
        }

        return "X11; Linux x86_64";
    }
}
