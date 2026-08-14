using System.IO.Compression;
using System.Net.Http;
using JustDownload.Core.Abstractions;
using JustDownload.Core.Integrity;
using JustDownload.Core.Transport;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Media;

/// <summary>
/// Default <see cref="IDenoProvisioner"/>. Reuses the locator to honour an existing install, and only when
/// none is found downloads the pinned Deno build for the current platform, verifies its SHA-256, and
/// extracts the single executable entry into the vendor directory (like <see cref="FfmpegProvisioner"/>'s
/// archive pattern, but with no <c>bin/</c> folder or side-by-side libraries — Deno's zip holds only the
/// executable). Downloads flow through the shared HTTP handler; nothing is fetched unless the user actually
/// downloads yt-dlp (Deno provisioning piggybacks on that same explicit action, D3).
/// </summary>
internal sealed partial class DenoProvisioner : IDenoProvisioner, IDisposable
{
    private const string TempFileName = ".deno-download.partial";

    private readonly IDenoLocator _locator;
    private readonly DenoOptions _options;
    private readonly DenoManifest _manifest;
    private readonly IChecksumVerifier _checksum;
    private readonly ISharedHttpHandlerProvider _handlerProvider;
    private readonly IAppInfoProvider _appInfo;
    private readonly ILogger<DenoProvisioner> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DenoProvisioner(
        IDenoLocator locator,
        DenoOptions options,
        DenoManifest manifest,
        IChecksumVerifier checksum,
        ISharedHttpHandlerProvider handlerProvider,
        IAppInfoProvider appInfo,
        ILogger<DenoProvisioner> logger)
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

    private static string ExecutableName => OperatingSystem.IsWindows() ? "deno.exe" : "deno";

    public void Dispose() => _gate.Dispose();

    public async Task<DenoInfo?> EnsureAsync(CancellationToken cancellationToken = default)
    {
        bool hasSource = _manifest.TryGetForCurrentPlatform(out DenoDownloadSource source);

        // Honour any Deno already on the configured path, the vendor directory, or PATH — unless it's a
        // binary WE previously downloaded into the vendor directory and the pinned manifest version has
        // since moved on, in which case it's upgraded rather than trusted as-is.
        DenoInfo? existing = await _locator.LocateAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!hasSource || !IsUpgradeNeeded(existing, source))
            {
                return existing;
            }

            LogUpgrading(_logger, existing.Version, source.Version);
            _locator.Invalidate();
        }
        else if (!hasSource)
        {
            LogNoSource(_logger, FfmpegManifest.CurrentRuntimeIdentifier);
            return null;
        }

        // Serialize provisioning so two concurrent callers don't both fetch the archive.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have provisioned/upgraded while we waited.
            existing = await _locator.LocateAsync(cancellationToken).ConfigureAwait(false);
            if (existing is not null && !IsUpgradeNeeded(existing, source))
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

    /// <summary>
    /// True only for a binary WE provisioned (living at the vendor-directory path) whose reported version
    /// differs from the pinned manifest version. A user-configured <see cref="DenoOptions.DenoPath"/> or a
    /// PATH-resolved Deno is never auto-upgraded — that would silently override an explicit user choice.
    /// </summary>
    private bool IsUpgradeNeeded(DenoInfo existing, DenoDownloadSource source) =>
        IsOwnVendorBinary(existing.ExecutablePath) &&
        !string.Equals(existing.Version, NormalizeVersion(source.Version), StringComparison.Ordinal);

    private bool IsOwnVendorBinary(string executablePath)
    {
        string vendorDir = _options.VendorDirectory ?? Path.Combine(AppDataPaths.Directory(_appInfo), "deno");
        string ownPath = Path.Combine(vendorDir, ExecutableName);
        return string.Equals(
            Path.GetFullPath(executablePath),
            Path.GetFullPath(ownPath),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>Strips the release tag's leading <c>v</c> (e.g. <c>v2.9.5</c>) to match the plain
    /// <c>2.9.5</c> format <see cref="DenoLocator.ParseVersion"/> reports from <c>deno --version</c>.</summary>
    private static string NormalizeVersion(string version) =>
        version.Length > 1 && (version[0] == 'v' || version[0] == 'V') && char.IsDigit(version[1])
            ? version[1..]
            : version;

    private async Task<DenoInfo> ProvisionAsync(DenoDownloadSource source, CancellationToken cancellationToken)
    {
        string vendorDir = _options.VendorDirectory ?? Path.Combine(AppDataPaths.Directory(_appInfo), "deno");
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
                throw new DenoException(
                    $"Deno download failed its integrity check ({verification.Outcome}; expected " +
                    $"{source.Sha256}, computed {verification.ComputedHash ?? "<none>"}).");
            }

            ExtractExecutable(tempArchive, vendorDir);
        }
        finally
        {
            TryDelete(tempArchive);
        }

        // The binary on disk just changed (fresh download or an upgrade over a stale one) — drop any cached
        // locator result so self-validation below, and any other consumer holding this same singleton
        // locator, re-reads the new file instead of a pre-upgrade cached DenoInfo.
        _locator.Invalidate();

        DenoInfo? provisioned = await _locator.LocateAsync(cancellationToken).ConfigureAwait(false);
        if (provisioned is null)
        {
            throw new DenoException("Deno was downloaded and extracted but could not be located afterwards.");
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
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"{_appInfo.Name}-deno-provisioner/1.0");

        using HttpResponseMessage response = await client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(
            destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts the single executable entry from the archive by name, flattened into
    /// <paramref name="vendorDir"/> — Deno's zip holds nothing but the executable at its root, unlike
    /// ffmpeg's <c>bin/</c> layout. Using the entry's file name only is inherently safe from path-traversal
    /// ("zip slip"). Grants the execute bit on non-Windows, matching <see cref="YtDlpProvisioner"/> — the
    /// GitHub asset carries no execute bit for the platforms that need one.
    /// </summary>
    private static void ExtractExecutable(string archivePath, string vendorDir)
    {
        using ZipArchive zip = ZipFile.OpenRead(archivePath);
        ZipArchiveEntry? entry = zip.Entries.FirstOrDefault(
            e => string.Equals(e.Name, ExecutableName, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            throw new DenoException($"the downloaded archive did not contain '{ExecutableName}'.");
        }

        string destination = Path.Combine(vendorDir, ExecutableName);
        entry.ExtractToFile(destination, overwrite: true);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                destination,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
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
        Message = "Downloading Deno from {Url} for {RuntimeIdentifier}.")]
    private static partial void LogDownloading(ILogger logger, Uri url, string runtimeIdentifier);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "Provisioned Deno {Path} (version {Version}).")]
    private static partial void LogProvisioned(ILogger logger, string path, string version);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug,
        Message = "No Deno build is available to download for {RuntimeIdentifier}; yt-dlp will run without a JS runtime.")]
    private static partial void LogNoSource(ILogger logger, string runtimeIdentifier);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information,
        Message = "Deno {ExistingVersion} is older than the pinned {PinnedVersion}; upgrading.")]
    private static partial void LogUpgrading(ILogger logger, string existingVersion, string pinnedVersion);
}
