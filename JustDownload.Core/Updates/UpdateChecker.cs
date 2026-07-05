using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JustDownload.Core.Abstractions;
using JustDownload.Core.Integrity;
using JustDownload.Core.Settings;
using JustDownload.Core.Transport;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Updates;

/// <summary>
/// Default <see cref="IUpdateChecker"/> (TASK-080/TASK-223). Opt-in (AC0/AC2): makes no network call unless
/// <see cref="AppSettings.AutoUpdateEnabled"/> is on, and fails closed — also with no network call — if the
/// embedded production public key is still the documented placeholder (<see cref="UpdateSigningKey"/>);
/// "looks unset" never means "trust anyway". <see cref="CheckAsync"/> only detects a newer release and
/// verifies its <c>checksums.txt</c> against the embedded ECDSA P-256 public key (AC1), returning
/// <see cref="UpdateCheckStatus.Available"/> with the verified installer's URL/hash; nothing is downloaded
/// or launched until the caller explicitly confirms via <see cref="DownloadAndApplyAsync"/> (TASK-223 —
/// downloading/launching without confirmation was TASK-080's original, now-revised, scope).
/// <para>
/// Version/asset detection resolves a per-OS/arch installer asset name (<see cref="ResolveInstallerAssetName"/>,
/// TASK-172) once macOS (<c>build/build-macos-packages.ps1</c>, TASK-077) and Linux
/// (<c>build/build-linux-packages.ps1</c>, TASK-078) packaging exists; a platform/arch with no known asset —
/// or a release that doesn't carry it yet — gets <see cref="UpdateCheckStatus.AvailableForManualDownload"/>
/// with a link to the release instead.
/// </para>
/// </summary>
internal sealed partial class UpdateChecker : IUpdateChecker
{
    /// <summary>Matches the bootstrapper name <c>build/build-installer.ps1</c> produces (TASK-159).</summary>
    internal const string WindowsInstallerAssetName = "JustDownload-win-x64-Setup.exe";
    internal const string ChecksumsAssetName = "checksums.txt";
    internal const string ChecksumsSignatureAssetName = "checksums.txt.sig";

    private readonly ISettingsService _settings;
    private readonly IAppVersionProvider _versionProvider;
    private readonly UpdateOptions _options;
    private readonly ISharedHttpHandlerProvider _handlerProvider;
    private readonly IAppInfoProvider _appInfo;
    private readonly IChecksumVerifier _checksum;
    private readonly IUpdateApplier _applier;
    private readonly bool _isWindows;
    private readonly bool _isMacOs;
    private readonly bool _isLinux;
    private readonly Architecture _architecture;
    private readonly ILogger<UpdateChecker> _logger;
    private volatile UpdateCheckResult? _lastResult;

    public UpdateChecker(
        ISettingsService settings,
        IAppVersionProvider versionProvider,
        UpdateOptions options,
        ISharedHttpHandlerProvider handlerProvider,
        IAppInfoProvider appInfo,
        IChecksumVerifier checksum,
        IUpdateApplier applier,
        ILogger<UpdateChecker> logger)
        : this(
            settings, versionProvider, options, handlerProvider, appInfo, checksum, applier,
            OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), OperatingSystem.IsLinux(),
            RuntimeInformation.OSArchitecture, logger)
    {
    }

    /// <summary>
    /// Creates a checker with explicit platform/architecture info instead of the real
    /// <see cref="OperatingSystem"/>/<see cref="RuntimeInformation"/> — used by tests to exercise the
    /// macOS/Linux asset-resolution and apply paths from a single (Windows) CI machine, the same way
    /// <c>NativeHostManifestLocations</c>' explicit-platform constructor does for native-messaging paths.
    /// </summary>
    internal UpdateChecker(
        ISettingsService settings,
        IAppVersionProvider versionProvider,
        UpdateOptions options,
        ISharedHttpHandlerProvider handlerProvider,
        IAppInfoProvider appInfo,
        IChecksumVerifier checksum,
        IUpdateApplier applier,
        bool isWindows,
        bool isMacOs,
        bool isLinux,
        Architecture architecture,
        ILogger<UpdateChecker> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(versionProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(handlerProvider);
        ArgumentNullException.ThrowIfNull(appInfo);
        ArgumentNullException.ThrowIfNull(checksum);
        ArgumentNullException.ThrowIfNull(applier);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _versionProvider = versionProvider;
        _options = options;
        _handlerProvider = handlerProvider;
        _appInfo = appInfo;
        _checksum = checksum;
        _applier = applier;
        _isWindows = isWindows;
        _isMacOs = isMacOs;
        _isLinux = isLinux;
        _architecture = architecture;
        _logger = logger;
    }

    public UpdateCheckResult? LastResult => _lastResult;

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        UpdateCheckResult result = await CheckCoreAsync(cancellationToken).ConfigureAwait(false);
        _lastResult = result;
        return result;
    }

    public Task<UpdateCheckResult> DownloadAndApplyAsync(
        UpdateCheckResult checkResult, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkResult);
        if (checkResult.Status != UpdateCheckStatus.Available)
        {
            throw new ArgumentException(
                $"Expected a result with Status == {UpdateCheckStatus.Available}, got {checkResult.Status}.",
                nameof(checkResult));
        }

        return DownloadAndApplyCoreAsync(checkResult, progress, cancellationToken);
    }

    private async Task<UpdateCheckResult> DownloadAndApplyCoreAsync(
        UpdateCheckResult checkResult, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        string installerAssetName = checkResult.InstallerAssetName!;
        string installerUrl = checkResult.InstallerDownloadUrl!.ToString();
        string expectedHash = checkResult.ExpectedSha256!;
        string latestVersionText = checkResult.LatestVersion!;
        Uri? releaseUrl = checkResult.ReleaseUrl;

        string vendorDir = _options.VendorDirectory ?? Path.Combine(AppDataPaths.Directory(_appInfo), "updates");
        Directory.CreateDirectory(vendorDir);
        string tempPath = Path.Combine(vendorDir, ".update-download.partial");
        string finalPath = Path.Combine(vendorDir, installerAssetName);

        UpdateCheckResult result;
        try
        {
            using HttpClient client = CreateClient();
            await DownloadAsync(client, installerUrl, tempPath, progress, cancellationToken).ConfigureAwait(false);

            ChecksumResult verification =
                await _checksum.VerifyAsync(tempPath, expectedHash, cancellationToken).ConfigureAwait(false);
            if (!verification.IsMatch)
            {
                LogRejected(
                    _logger, UpdateCheckStatus.RejectedAssetHashMismatch, "the downloaded bytes didn't match the signed hash");
                result = new UpdateCheckResult(UpdateCheckStatus.RejectedAssetHashMismatch, latestVersionText, releaseUrl);
                _lastResult = result;
                return result;
            }

            File.Move(tempPath, finalPath, overwrite: true);
        }
        catch (HttpRequestException ex)
        {
            LogCheckFailed(_logger, ex);
            result = new UpdateCheckResult(UpdateCheckStatus.Error, latestVersionText, releaseUrl, ex.Message);
            _lastResult = result;
            return result;
        }
        finally
        {
            TryDelete(tempPath);
        }

        await _applier.ApplyAsync(finalPath, cancellationToken).ConfigureAwait(false);
        LogApplied(_logger, latestVersionText);
        result = new UpdateCheckResult(UpdateCheckStatus.Applied, latestVersionText, releaseUrl);
        _lastResult = result;
        return result;
    }

    private async Task<UpdateCheckResult> CheckCoreAsync(CancellationToken cancellationToken)
    {
        // AC2: not even a metadata GET when the feature is off.
        if (!_settings.Current.AutoUpdateEnabled)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Disabled);
        }

        if (!TryImportPublicKey(out ECDsa? publicKey))
        {
            LogNotConfigured(_logger);
            return new UpdateCheckResult(UpdateCheckStatus.NotConfigured);
        }

        using ECDsa key = publicKey;
        using HttpClient client = CreateClient();

        GitHubReleaseDto release;
        try
        {
            Uri releaseUri = new(
                _options.ApiBaseUri,
                $"repos/{_options.RepositoryOwner}/{_options.RepositoryName}/releases/latest");
            release = await client.GetFromJsonAsync<GitHubReleaseDto>(releaseUri, cancellationToken)
                .ConfigureAwait(false) ?? throw new HttpRequestException("Empty release response.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            LogCheckFailed(_logger, ex);
            return new UpdateCheckResult(UpdateCheckStatus.Error, ErrorMessage: ex.Message);
        }

        if (release.TagName is not { Length: > 0 } tagName)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Error, ErrorMessage: "The release response had no tag_name.");
        }

        if (!TryParseVersion(tagName, out Version? latestVersion))
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.Error, ErrorMessage: $"Could not parse a version from release tag '{tagName}'.");
        }

        if (!TryParseVersion(_versionProvider.CurrentVersion, out Version? currentVersion))
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.Error,
                ErrorMessage: $"Could not parse the current app version '{_versionProvider.CurrentVersion}'.");
        }

        Uri? releaseUrl = Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out Uri? parsedUrl) ? parsedUrl : null;
        string latestVersionText = tagName.TrimStart('v', 'V');

        if (currentVersion.CompareTo(latestVersion) >= 0)
        {
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate, latestVersionText, releaseUrl);
        }

        string? installerAssetName = ResolveInstallerAssetName(_isWindows, _isMacOs, _isLinux, _architecture, latestVersionText);
        if (installerAssetName is null)
        {
            // No known installer asset for this OS/arch (or an OS with no packaging at all) — detected,
            // not applied.
            return new UpdateCheckResult(UpdateCheckStatus.AvailableForManualDownload, latestVersionText, releaseUrl);
        }

        if (FindAsset(release, installerAssetName) is not { BrowserDownloadUrl: { Length: > 0 } installerUrl })
        {
            return new UpdateCheckResult(UpdateCheckStatus.AvailableForManualDownload, latestVersionText, releaseUrl);
        }

        if (FindAsset(release, ChecksumsAssetName) is not { BrowserDownloadUrl: { Length: > 0 } checksumsUrl } ||
            FindAsset(release, ChecksumsSignatureAssetName) is not { BrowserDownloadUrl: { Length: > 0 } signatureUrl })
        {
            LogRejected(_logger, UpdateCheckStatus.RejectedUnsigned, "no checksums.txt/.sig asset was published");
            return new UpdateCheckResult(UpdateCheckStatus.RejectedUnsigned, latestVersionText, releaseUrl);
        }

        byte[] checksumsBytes;
        byte[] signatureBytes;
        try
        {
            checksumsBytes = await client.GetByteArrayAsync(checksumsUrl, cancellationToken).ConfigureAwait(false);
            signatureBytes = await client.GetByteArrayAsync(signatureUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            LogCheckFailed(_logger, ex);
            return new UpdateCheckResult(UpdateCheckStatus.Error, latestVersionText, releaseUrl, ex.Message);
        }

        // Both checks are mandatory and independent (TASK-080): the signature covers checksums.txt's exact
        // bytes, so tampered content invalidates it even if the signature bytes themselves are well-formed.
        if (!key.VerifyData(checksumsBytes, signatureBytes, HashAlgorithmName.SHA256))
        {
            LogRejected(_logger, UpdateCheckStatus.RejectedInvalidSignature, "the signature did not verify");
            return new UpdateCheckResult(UpdateCheckStatus.RejectedInvalidSignature, latestVersionText, releaseUrl);
        }

        string? expectedHash = FindChecksum(checksumsBytes, installerAssetName);
        if (expectedHash is null)
        {
            LogRejected(_logger, UpdateCheckStatus.RejectedAssetHashMismatch, "the signed manifest doesn't list this asset");
            return new UpdateCheckResult(UpdateCheckStatus.RejectedAssetHashMismatch, latestVersionText, releaseUrl);
        }

        // Everything needed to download/apply later is now verified (signature + manifest membership) —
        // hand it back for DownloadAndApplyAsync to run once the user confirms, without downloading
        // anything yet.
        return new UpdateCheckResult(
            UpdateCheckStatus.Available,
            latestVersionText,
            releaseUrl,
            InstallerAssetName: installerAssetName,
            InstallerDownloadUrl: new Uri(installerUrl),
            ExpectedSha256: expectedHash);
    }

    private bool TryImportPublicKey([NotNullWhen(true)] out ECDsa? key)
    {
        if (string.IsNullOrWhiteSpace(_options.PublicKeyBase64))
        {
            key = null;
            return false;
        }

        try
        {
            var candidate = ECDsa.Create();
            candidate.ImportSubjectPublicKeyInfo(Convert.FromBase64String(_options.PublicKeyBase64), out _);
            key = candidate;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            key = null;
            return false;
        }
    }

    private HttpClient CreateClient()
    {
        var client = new HttpClient(_handlerProvider.Handler, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan, // bounded by the cancellation token, not a fixed deadline
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"{_appInfo.Name}-update-checker/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private const int DownloadBufferSize = 64 * 1024; // sub-LOH, matches ChecksumVerifier's copy buffer

    private static async Task DownloadAsync(
        HttpClient client, string url, string destinationPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(
            destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

        // Only report real fractional progress (against a known Content-Length); no faked/indeterminate
        // reporting when the server doesn't declare one.
        if (progress is null || response.Content.Headers.ContentLength is not { } totalBytes || totalBytes <= 0)
        {
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            return;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(DownloadBufferSize);
        try
        {
            long totalRead = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, DownloadBufferSize), cancellationToken)
                       .ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                totalRead += read;
                progress.Report((double)totalRead / totalBytes);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// The installer asset name to look for on this OS/arch (TASK-172), or <see langword="null"/> when
    /// there is none — e.g. an OS with no packaging, or an architecture with no published build. Windows'
    /// name is version-independent (<see cref="WindowsInstallerAssetName"/> — <c>build/build-installer.ps1</c>
    /// always emits the same bootstrapper name); macOS/Linux bake the version into the filename
    /// (<c>build/build-macos-packages.ps1</c>, <c>build/build-linux-packages.ps1</c>), hence
    /// <paramref name="version"/>. Of Linux's three package formats (AppImage/deb/rpm), only the AppImage is
    /// auto-applied here — deb/rpm need an elevated package-manager install (pkexec/apt/dnf) that a plain
    /// process launch can't drive; see <see cref="LinuxUpdateApplier"/>. Of macOS's two per-arch dmgs, the
    /// one matching <paramref name="architecture"/> is picked (<see cref="RuntimeInformation.OSArchitecture"/>
    /// — the underlying hardware, not the running process' architecture, so a Rosetta-translated process
    /// still gets the native arm64 build). A pure function of explicit inputs (not read from
    /// <see cref="OperatingSystem"/>/<see cref="RuntimeInformation"/> directly) so every branch is testable
    /// from one machine.
    /// </summary>
    internal static string? ResolveInstallerAssetName(
        bool isWindows, bool isMacOs, bool isLinux, Architecture architecture, string version)
    {
        if (isWindows)
        {
            return WindowsInstallerAssetName;
        }

        if (isMacOs)
        {
            return architecture switch
            {
                Architecture.Arm64 => $"JustDownload-{version}-osx-arm64.dmg",
                Architecture.X64 => $"JustDownload-{version}-osx-x64.dmg",
                _ => null,
            };
        }

        if (isLinux)
        {
            return architecture == Architecture.X64 ? $"JustDownload-{version}-x86_64.AppImage" : null;
        }

        return null;
    }

    private static GitHubReleaseAssetDto? FindAsset(GitHubReleaseDto release, string name) =>
        release.Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));

    /// <summary>Parses sha256sum-style lines (<c>&lt;hex&gt;  &lt;filename&gt;</c>) for <paramref name="fileName"/>.</summary>
    private static string? FindChecksum(byte[] checksumsBytes, string fileName)
    {
        string text = Encoding.UTF8.GetString(checksumsBytes);
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0)
            {
                continue;
            }

            int split = line.IndexOfAny([' ', '\t']);
            if (split <= 0)
            {
                continue;
            }

            string hash = line[..split];
            string name = line[split..].TrimStart(' ', '\t', '*');
            if (string.Equals(name, fileName, StringComparison.Ordinal))
            {
                return hash;
            }
        }

        return null;
    }

    /// <summary>
    /// Simple x.y.z comparison via <see cref="Version"/> — no prerelease/build-metadata semver handling
    /// (TASK-080 accepted scope limit; see the implementation notes).
    /// </summary>
    private static bool TryParseVersion(string text, [NotNullWhen(true)] out Version? version)
    {
        string trimmed = text.TrimStart('v', 'V');
        int plus = trimmed.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            trimmed = trimmed[..plus];
        }

        return Version.TryParse(trimmed, out version);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A leftover temp file is harmless; it is overwritten (FileMode.Create) on the next attempt.
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "Update check skipped: the production signing key is not configured (placeholder).")]
    private static partial void LogNotConfigured(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Update check failed.")]
    private static partial void LogCheckFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Update rejected ({Status}): {Reason}.")]
    private static partial void LogRejected(ILogger logger, UpdateCheckStatus status, string reason);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Update {Version} verified; installer launched.")]
    private static partial void LogApplied(ILogger logger, string version);
}
