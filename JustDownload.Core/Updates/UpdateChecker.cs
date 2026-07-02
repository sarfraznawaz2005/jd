using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Net.Http.Json;
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
/// Default <see cref="IUpdateChecker"/> (TASK-080). Opt-in (AC0/AC2): makes no network call unless
/// <see cref="AppSettings.AutoUpdateEnabled"/> is on, and fails closed — also with no network call — if the
/// embedded production public key is still the documented placeholder (<see cref="UpdateSigningKey"/>);
/// "looks unset" never means "trust anyway". When a newer release exists, its <c>checksums.txt</c> is
/// verified against the embedded ECDSA P-256 public key (AC1) and the target asset's SHA-256 is checked
/// against the now-trusted manifest before anything is downloaded and launched.
/// <para>
/// Version/asset detection is OS-agnostic, but the release workflow (TASK-080 scope) only ever publishes a
/// Windows installer asset — macOS/Linux packaging doesn't exist yet (TASK-077/078) — so verification and
/// the launch step only ever run on Windows; other platforms get <see cref="UpdateCheckStatus.AvailableForManualDownload"/>
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
    private readonly ILogger<UpdateChecker> _logger;

    public UpdateChecker(
        ISettingsService settings,
        IAppVersionProvider versionProvider,
        UpdateOptions options,
        ISharedHttpHandlerProvider handlerProvider,
        IAppInfoProvider appInfo,
        IChecksumVerifier checksum,
        IUpdateApplier applier,
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
        _logger = logger;
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
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

        if (!OperatingSystem.IsWindows())
        {
            // Locked scope: apply — and the verification that precedes it — is Windows-only for now; no
            // macOS/Linux installer asset exists to verify (TASK-077/078).
            return new UpdateCheckResult(UpdateCheckStatus.AvailableForManualDownload, latestVersionText, releaseUrl);
        }

        if (FindAsset(release, WindowsInstallerAssetName) is not { BrowserDownloadUrl: { Length: > 0 } installerUrl })
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

        string? expectedHash = FindChecksum(checksumsBytes, WindowsInstallerAssetName);
        if (expectedHash is null)
        {
            LogRejected(_logger, UpdateCheckStatus.RejectedAssetHashMismatch, "the signed manifest doesn't list this asset");
            return new UpdateCheckResult(UpdateCheckStatus.RejectedAssetHashMismatch, latestVersionText, releaseUrl);
        }

        string vendorDir = _options.VendorDirectory ?? Path.Combine(AppDataPaths.Directory(_appInfo), "updates");
        Directory.CreateDirectory(vendorDir);
        string tempPath = Path.Combine(vendorDir, ".update-download.partial");
        string finalPath = Path.Combine(vendorDir, WindowsInstallerAssetName);
        try
        {
            await DownloadAsync(client, installerUrl, tempPath, cancellationToken).ConfigureAwait(false);

            ChecksumResult verification =
                await _checksum.VerifyAsync(tempPath, expectedHash, cancellationToken).ConfigureAwait(false);
            if (!verification.IsMatch)
            {
                LogRejected(
                    _logger, UpdateCheckStatus.RejectedAssetHashMismatch, "the downloaded bytes didn't match the signed hash");
                return new UpdateCheckResult(UpdateCheckStatus.RejectedAssetHashMismatch, latestVersionText, releaseUrl);
            }

            File.Move(tempPath, finalPath, overwrite: true);
        }
        catch (HttpRequestException ex)
        {
            LogCheckFailed(_logger, ex);
            return new UpdateCheckResult(UpdateCheckStatus.Error, latestVersionText, releaseUrl, ex.Message);
        }
        finally
        {
            TryDelete(tempPath);
        }

        await _applier.ApplyAsync(finalPath, cancellationToken).ConfigureAwait(false);
        LogApplied(_logger, latestVersionText);
        return new UpdateCheckResult(UpdateCheckStatus.Applied, latestVersionText, releaseUrl);
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

    private static async Task DownloadAsync(
        HttpClient client, string url, string destinationPath, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(
            destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
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
