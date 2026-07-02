using System.Net.Http;
using JustDownload.Core.Abstractions;
using JustDownload.Core.Integrity;
using JustDownload.Core.Transport;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Media;

/// <summary>
/// Default <see cref="IYtDlpProvisioner"/> (TASK-162, D3). Reuses the locator to honour an existing install,
/// and only when none is found downloads the pinned yt-dlp release for the current platform, verifies its
/// SHA-256, and moves it into the vendor directory. Unlike <see cref="FfmpegProvisioner"/> the download is
/// already the final single-file executable — no archive extraction step. Downloads flow through the shared
/// HTTP handler; nothing is fetched unless the user has enabled video capture/detection and asks for it.
/// </summary>
internal sealed partial class YtDlpProvisioner : IYtDlpProvisioner, IDisposable
{
    private const string TempFileName = ".yt-dlp-download.partial";

    private readonly IYtDlpLocator _locator;
    private readonly YtDlpOptions _options;
    private readonly YtDlpManifest _manifest;
    private readonly IChecksumVerifier _checksum;
    private readonly ISharedHttpHandlerProvider _handlerProvider;
    private readonly IAppInfoProvider _appInfo;
    private readonly ILogger<YtDlpProvisioner> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public YtDlpProvisioner(
        IYtDlpLocator locator,
        YtDlpOptions options,
        YtDlpManifest manifest,
        IChecksumVerifier checksum,
        ISharedHttpHandlerProvider handlerProvider,
        IAppInfoProvider appInfo,
        ILogger<YtDlpProvisioner> logger)
    {
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(checksum);
        ArgumentNullException.ThrowIfNull(handlerProvider);
        ArgumentNullException.ThrowIfNull(appInfo);
        ArgumentNullException.ThrowIfNull(logger);
        _locator = locator;
        _options = options;
        _manifest = manifest;
        _checksum = checksum;
        _handlerProvider = handlerProvider;
        _appInfo = appInfo;
        _logger = logger;
    }

    private static string ExecutableName => OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp";

    public void Dispose() => _gate.Dispose();

    public async Task<YtDlpInfo?> EnsureAsync(CancellationToken cancellationToken = default)
    {
        // Honour any yt-dlp already on the configured path, the vendor directory, or PATH.
        YtDlpInfo? existing = await _locator.LocateAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        if (!_manifest.TryGetForCurrentPlatform(out YtDlpDownloadSource source))
        {
            LogNoSource(_logger, FfmpegManifest.CurrentRuntimeIdentifier);
            return null;
        }

        // Serialize provisioning so two concurrent callers (e.g. the Settings button and a media download)
        // don't both fetch the binary.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have provisioned while we waited.
            existing = await _locator.LocateAsync(cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                return existing;
            }

            return await ProvisionAsync(source, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<YtDlpInfo> ProvisionAsync(YtDlpDownloadSource source, CancellationToken cancellationToken)
    {
        string vendorDir = _options.VendorDirectory ?? Path.Combine(AppDataPaths.Directory(_appInfo), "yt-dlp");
        Directory.CreateDirectory(vendorDir);
        _options.VendorDirectory = vendorDir; // so the locator searches here afterwards

        string tempFile = Path.Combine(vendorDir, TempFileName);
        string finalPath = Path.Combine(vendorDir, ExecutableName);
        LogDownloading(_logger, source.Url, source.RuntimeIdentifier);
        try
        {
            await DownloadAsync(source.Url, tempFile, cancellationToken).ConfigureAwait(false);

            ChecksumResult verification =
                await _checksum.VerifyAsync(tempFile, source.Sha256, cancellationToken).ConfigureAwait(false);
            if (!verification.IsMatch)
            {
                throw new YtDlpException(
                    $"yt-dlp download failed its integrity check ({verification.Outcome}; expected " +
                    $"{source.Sha256}, computed {verification.ComputedHash ?? "<none>"}).");
            }

            File.Move(tempFile, finalPath, overwrite: true);
            if (!OperatingSystem.IsWindows())
            {
                // The GitHub asset carries no execute bit; grant rwxr-xr-x so yt-dlp can actually be run.
                File.SetUnixFileMode(
                    finalPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }
        finally
        {
            TryDelete(tempFile);
        }

        // Self-validate: locating yt-dlp runs `yt-dlp --version` and only succeeds if it exits cleanly.
        YtDlpInfo? provisioned = await _locator.LocateAsync(cancellationToken).ConfigureAwait(false);
        if (provisioned is null)
        {
            throw new YtDlpException("yt-dlp was downloaded but could not be located or run afterwards.");
        }

        LogProvisioned(_logger, provisioned.ExecutablePath, provisioned.Version);
        return provisioned;
    }

    private async Task DownloadAsync(Uri url, string destinationPath, CancellationToken cancellationToken)
    {
        using var client = new HttpClient(_handlerProvider.Handler, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan, // bounded by the cancellation token, not a fixed deadline
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"{_appInfo.Name}-ytdlp-provisioner/1.0");

        using HttpResponseMessage response = await client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(
            destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
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

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Downloading yt-dlp from {Url} for {RuntimeIdentifier}.")]
    private static partial void LogDownloading(ILogger logger, Uri url, string runtimeIdentifier);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "Provisioned yt-dlp {Path} (version {Version}).")]
    private static partial void LogProvisioned(ILogger logger, string path, string version);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "No yt-dlp build is available to download for {RuntimeIdentifier}.")]
    private static partial void LogNoSource(ILogger logger, string runtimeIdentifier);
}
