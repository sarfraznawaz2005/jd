using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Transport;

/// <summary>
/// Detects an installed Chrome/Edge/Firefox and its version, so <see cref="BrowserUserAgentRefresher"/> can
/// build a User-Agent that matches a browser actually on this machine rather than a static hardcoded version
/// that inevitably goes stale.
/// </summary>
internal interface IBrowserUserAgentDetector
{
    /// <summary>Tries Chrome, then Edge, then Firefox; returns the first one found with its version, or null
    /// if none of them are installed (or their version couldn't be read) on this machine.</summary>
    Task<(BrowserKind Kind, string Version)?> TryDetectAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IBrowserUserAgentDetector"/>. Reads each candidate's version from its own on-disk
/// metadata (the Win32 executable's version resource on Windows, the app bundle's <c>Info.plist</c> on
/// macOS) — deliberately <b>never</b> launches the browser executable itself: an earlier draft shelled out to
/// <c>chrome.exe --version</c>, which on a real machine did not print a version and exit as the flag is
/// documented to — it forwarded to the already-installed browser and opened a full, visible browser window,
/// an unacceptable side effect for a silent background refresh. Linux has no equivalent no-launch metadata
/// source that's reliable across distros/package managers (dpkg/rpm/snap/flatpak all differ), so it isn't
/// probed there — Linux always falls back to <see cref="TransportOptions"/>'s static default UA.
/// </summary>
internal sealed partial class BrowserUserAgentDetector : IBrowserUserAgentDetector
{
    private static readonly BrowserKind[] PreferenceOrder = [BrowserKind.Chrome, BrowserKind.Edge, BrowserKind.Firefox];

    private readonly ILogger<BrowserUserAgentDetector> _logger;

    public BrowserUserAgentDetector(ILogger<BrowserUserAgentDetector> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public Task<(BrowserKind Kind, string Version)?> TryDetectAsync(CancellationToken cancellationToken = default)
    {
        foreach (BrowserKind kind in PreferenceOrder)
        {
            foreach (string candidate in CandidatePaths(kind))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string? version = TryReadVersion(candidate);
                if (version is not null)
                {
                    LogDetected(_logger, kind, candidate, version);
                    return Task.FromResult<(BrowserKind, string)?>((kind, version));
                }
            }
        }

        LogNoneDetected(_logger);
        return Task.FromResult<(BrowserKind, string)?>(null);
    }

    private static IReadOnlyList<string> CandidatePaths(BrowserKind kind)
    {
        if (OperatingSystem.IsWindows())
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return kind switch
            {
                BrowserKind.Chrome =>
                [
                    Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
                    Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
                    Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe"),
                ],
                BrowserKind.Edge =>
                [
                    Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
                    Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
                ],
                BrowserKind.Firefox =>
                [
                    Path.Combine(programFiles, "Mozilla Firefox", "firefox.exe"),
                    Path.Combine(programFilesX86, "Mozilla Firefox", "firefox.exe"),
                ],
                _ => [],
            };
        }

        if (OperatingSystem.IsMacOS())
        {
            return kind switch
            {
                BrowserKind.Chrome => ["/Applications/Google Chrome.app/Contents/Info.plist"],
                BrowserKind.Edge => ["/Applications/Microsoft Edge.app/Contents/Info.plist"],
                BrowserKind.Firefox => ["/Applications/Firefox.app/Contents/Info.plist"],
                _ => [],
            };
        }

        // Linux: no on-disk metadata that's readable without either launching the browser (unsafe, see the
        // class summary) or parsing a package manager's own format (dpkg/rpm/snap/flatpak all differ) — so
        // no candidates are offered, and TransportOptions keeps its static default UA there.
        return [];
    }

    private static string? TryReadVersion(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            string? raw = OperatingSystem.IsWindows()
                ? FileVersionInfo.GetVersionInfo(path).FileVersion
                : ReadMacBundleShortVersion(path);

            return raw is null ? null : ParseVersion(raw);
        }
        catch (Exception)
        {
            // Not installed there, unreadable, or the version metadata didn't parse — try the next candidate.
            return null;
        }
    }

    /// <summary>Reads <c>CFBundleShortVersionString</c> straight out of an app bundle's <c>Info.plist</c> XML
    /// text, without launching the app or the OS's plist tooling. Returns null (silently skipped by the
    /// caller) for a binary-format plist, which this simple text search does not attempt to decode.</summary>
    private static string? ReadMacBundleShortVersion(string plistPath)
    {
        string content = File.ReadAllText(plistPath);
        Match match = MacBundleVersionPattern().Match(content);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Extracts the first dotted version token from a version string (a Windows file-version
    /// resource or a macOS <c>CFBundleShortVersionString</c>), e.g. "124.0.6367.91".</summary>
    internal static string? ParseVersion(string raw)
    {
        Match match = VersionPattern().Match(raw);
        return match.Success ? match.Value : null;
    }

    [GeneratedRegex(@"\d+(?:\.\d+){1,3}")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"<key>CFBundleShortVersionString</key>\s*<string>([\d.]+)</string>")]
    private static partial Regex MacBundleVersionPattern();

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Detected {Kind} {Version} at {Path} for the default User-Agent.")]
    private static partial void LogDetected(ILogger logger, BrowserKind kind, string path, string version);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Debug,
        Message = "No installed Chrome/Edge/Firefox found; keeping the built-in default User-Agent.")]
    private static partial void LogNoneDetected(ILogger logger);
}
