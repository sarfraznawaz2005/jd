using System.IO.Compression;
using System.Net.Http;
using JustDownload.Core.Abstractions;
using JustDownload.Core.Integrity;
using JustDownload.Core.Transport;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Media;

/// <summary>
/// Default <see cref="IFfmpegProvisioner"/> (TASK-079). Reuses the locator to honour an existing
/// install, and only when none is found downloads the pinned LGPL build for the current platform,
/// verifies its SHA-256, and extracts the executables into the vendor directory. Downloads flow through
/// the shared HTTP handler; nothing is fetched unless the user actually needs ffmpeg (lazy, opt-in).
/// </summary>
internal sealed partial class FfmpegProvisioner : IFfmpegProvisioner, IDisposable
{
    private const string TempFileName = ".ffmpeg-download.partial";

    private readonly IFfmpegLocator _locator;
    private readonly FfmpegOptions _options;
    private readonly FfmpegManifest _manifest;
    private readonly IChecksumVerifier _checksum;
    private readonly ISharedHttpHandlerProvider _handlerProvider;
    private readonly IAppInfoProvider _appInfo;
    private readonly ILogger<FfmpegProvisioner> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FfmpegProvisioner(
        IFfmpegLocator locator,
        FfmpegOptions options,
        FfmpegManifest manifest,
        IChecksumVerifier checksum,
        ISharedHttpHandlerProvider handlerProvider,
        IAppInfoProvider appInfo,
        ILogger<FfmpegProvisioner> logger)
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

    private static string ExecutableName => OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

    public void Dispose() => _gate.Dispose();

    public async Task<FfmpegInfo?> EnsureAsync(CancellationToken cancellationToken = default)
    {
        // Honour any ffmpeg already on the configured path, the vendor directory, or PATH.
        FfmpegInfo? existing = await _locator.LocateAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        if (!_manifest.TryGetForCurrentPlatform(out FfmpegDownloadSource source))
        {
            LogNoSource(_logger, FfmpegManifest.CurrentRuntimeIdentifier);
            return null;
        }

        // Serialize provisioning so two concurrent media downloads don't both fetch the archive.
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

    private async Task<FfmpegInfo> ProvisionAsync(FfmpegDownloadSource source, CancellationToken cancellationToken)
    {
        // Policy guard: only LGPL builds are ever obtained (AC1, D7 / CLAUDE.md §4).
        if (source.License != FfmpegBuildLicense.Lgpl)
        {
            throw new FfmpegException(
                $"Refusing to download a non-LGPL ffmpeg build ({source.License}) for {source.RuntimeIdentifier}.");
        }

        string vendorDir = _options.VendorDirectory ?? Path.Combine(AppDataPaths.Directory(_appInfo), "ffmpeg");
        Directory.CreateDirectory(vendorDir);
        _options.VendorDirectory = vendorDir; // so the locator searches here after extraction

        string tempArchive = Path.Combine(vendorDir, TempFileName);
        LogDownloading(_logger, source.Url, source.RuntimeIdentifier);
        try
        {
            await DownloadAsync(source.Url, tempArchive, cancellationToken).ConfigureAwait(false);

            ChecksumResult verification =
                await _checksum.VerifyAsync(tempArchive, source.Sha256, cancellationToken).ConfigureAwait(false);
            if (!verification.IsMatch)
            {
                throw new FfmpegException(
                    $"ffmpeg download failed its integrity check ({verification.Outcome}; expected " +
                    $"{source.Sha256}, computed {verification.ComputedHash ?? "<none>"}).");
            }

            ExtractBinaries(tempArchive, vendorDir);
        }
        finally
        {
            TryDelete(tempArchive);
        }

        FfmpegInfo? provisioned = await _locator.LocateAsync(cancellationToken).ConfigureAwait(false);
        if (provisioned is null)
        {
            throw new FfmpegException("ffmpeg was downloaded and extracted but could not be located afterwards.");
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
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"{_appInfo.Name}-ffmpeg-provisioner/1.0");

        using HttpResponseMessage response = await client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(
            destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts the executables and side-by-side libraries from the archive's <c>bin/</c> folder, flattened
    /// into <paramref name="vendorDir"/>. Using each entry's file name only is inherently safe from
    /// path-traversal ("zip slip").
    /// </summary>
    private static void ExtractBinaries(string archivePath, string vendorDir)
    {
        using ZipArchive zip = ZipFile.OpenRead(archivePath);
        bool foundExecutable = false;

        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (entry.Name.Length == 0)
            {
                continue; // directory entry
            }

            string normalized = entry.FullName.Replace('\\', '/');
            if (!normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
            {
                continue; // only the bin/ payload (skip docs, presets, …)
            }

            string destination = Path.Combine(vendorDir, entry.Name);
            entry.ExtractToFile(destination, overwrite: true);

            if (string.Equals(entry.Name, ExecutableName, StringComparison.OrdinalIgnoreCase))
            {
                foundExecutable = true;
            }
        }

        if (!foundExecutable)
        {
            throw new FfmpegException($"the downloaded archive did not contain '{ExecutableName}'.");
        }
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
        Message = "Downloading LGPL ffmpeg from {Url} for {RuntimeIdentifier}.")]
    private static partial void LogDownloading(ILogger logger, Uri url, string runtimeIdentifier);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "Provisioned ffmpeg {Path} (version {Version}).")]
    private static partial void LogProvisioned(ILogger logger, string path, string version);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "No ffmpeg found and no LGPL build is available to download for {RuntimeIdentifier}; "
            + "install ffmpeg via your package manager or set its path in settings.")]
    private static partial void LogNoSource(ILogger logger, string runtimeIdentifier);
}
