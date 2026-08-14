using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using FluentAssertions;
using JustDownload.Core.Abstractions;
using JustDownload.Core.Integrity;
using JustDownload.Core.Media;
using JustDownload.Core.Transport;
using JustDownload.Tests.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.Media;

/// <summary>
/// Download-on-first-use provisioning of the pinned Deno build (the JS runtime yt-dlp needs for YouTube's
/// signature/JS challenges). Drives the provisioner against a loopback server serving a fake Deno archive so
/// the download → SHA-256 verify → extract flow is tested offline and deterministically, plus integrity
/// rejection, idempotence, and the graceful "no source for this platform" path — mirrors
/// <see cref="FfmpegProvisionerTests"/>, but the archive has no <c>bin/</c> folder (just the executable at
/// its root), matching Deno's real zip layout.
/// </summary>
public sealed class DenoProvisionerTests : IDisposable
{
    private static readonly string ExecutableName = OperatingSystem.IsWindows() ? "deno.exe" : "deno";

    private readonly string _vendorDir = Path.Combine(
        Path.GetTempPath(), "jd-deno-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_vendorDir))
            {
                Directory.Delete(_vendorDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task EnsureAsync_DownloadsVerifiesAndExtracts_WhenAbsent()
    {
        (byte[] archive, string sha256) = BuildFakeDenoArchive();
        await using var server = new LoopbackHttpServer { Body = archive, ContentType = "application/zip" };
        var manifest = ManifestFor(server.Url("deno.zip"), sha256);

        string exePath = Path.Combine(_vendorDir, ExecutableName);
        IDenoLocator locator = Substitute.For<IDenoLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(
            _ => File.Exists(exePath) ? new DenoInfo(exePath, "2.9.5") : null);

        DenoProvisioner provisioner = CreateProvisioner(manifest, locator);

        DenoInfo? result = await provisioner.EnsureAsync();

        result.Should().NotBeNull();
        result!.ExecutablePath.Should().Be(exePath);
        File.Exists(exePath).Should().BeTrue("the executable is extracted from the archive's root");
        File.Exists(Path.Combine(_vendorDir, ".deno-download.partial"))
            .Should().BeFalse("the temp archive is removed after extraction");
    }

    [Fact]
    public async Task EnsureAsync_Throws_AndLeavesNothing_OnChecksumMismatch()
    {
        (byte[] archive, _) = BuildFakeDenoArchive();
        await using var server = new LoopbackHttpServer { Body = archive };
        var manifest = ManifestFor(server.Url("deno.zip"), new string('a', 64));

        IDenoLocator locator = Substitute.For<IDenoLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns((DenoInfo?)null);

        DenoProvisioner provisioner = CreateProvisioner(manifest, locator);

        Func<Task> act = async () => await provisioner.EnsureAsync();

        await act.Should().ThrowAsync<DenoException>().WithMessage("*integrity check*");
        File.Exists(Path.Combine(_vendorDir, ExecutableName))
            .Should().BeFalse("a corrupt download must not be extracted");
        File.Exists(Path.Combine(_vendorDir, ".deno-download.partial"))
            .Should().BeFalse("the rejected temp archive is cleaned up");
    }

    [Fact]
    public async Task EnsureAsync_ReturnsExisting_WithoutDownloading()
    {
        var existing = new DenoInfo("/usr/bin/deno", "2.9.5");
        IDenoLocator locator = Substitute.For<IDenoLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(existing);

        // An unreachable URL would fail if a download were attempted; it must not be.
        var manifest = ManifestFor(new Uri("http://127.0.0.1:1/never.zip"), new string('b', 64));
        DenoProvisioner provisioner = CreateProvisioner(manifest, locator);

        DenoInfo? result = await provisioner.EnsureAsync();

        result.Should().BeSameAs(existing);
        File.Exists(Path.Combine(_vendorDir, ExecutableName)).Should().BeFalse();
    }

    [Fact]
    public async Task EnsureAsync_ReturnsNull_WhenNoSourceForPlatform()
    {
        IDenoLocator locator = Substitute.For<IDenoLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns((DenoInfo?)null);

        var manifest = new DenoManifest([]); // nothing pinned for any platform
        DenoProvisioner provisioner = CreateProvisioner(manifest, locator);

        DenoInfo? result = await provisioner.EnsureAsync();

        result.Should().BeNull("the caller should degrade gracefully — yt-dlp still works without Deno");
    }

    [Fact]
    public async Task EnsureAsync_UpgradesOwnVendorBinary_WhenOlderThanPinnedManifest()
    {
        (byte[] archive, string sha256) = BuildFakeDenoArchive();
        await using var server = new LoopbackHttpServer { Body = archive, ContentType = "application/zip" };
        // The manifest's Version carries the "v" release-tag prefix, same as the real DenoManifest.
        var manifest = ManifestFor(server.Url("deno.zip"), sha256, version: "v2.9.5");

        string exePath = Path.Combine(_vendorDir, ExecutableName);
        // Simulate a stale binary already sitting in OUR vendor directory (an old provisioner run).
        Directory.CreateDirectory(_vendorDir);
        File.WriteAllText(exePath, "stale-deno-binary");
        IDenoLocator locator = Substitute.For<IDenoLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            File.ReadAllText(exePath) == "fake-deno-binary"
                ? new DenoInfo(exePath, "2.9.5")
                : new DenoInfo(exePath, "1.0.0"));

        DenoProvisioner provisioner = CreateProvisioner(manifest, locator);

        DenoInfo? result = await provisioner.EnsureAsync();

        result.Should().NotBeNull();
        result!.Version.Should().Be("2.9.5", "the stale vendor binary must be replaced with the pinned version");
        File.ReadAllText(exePath).Should().Be("fake-deno-binary", "the pinned archive was actually downloaded and extracted");
    }

    [Fact]
    public async Task EnsureAsync_DoesNotRedownload_WhenOwnVendorBinaryAlreadyMatchesPinnedVersion()
    {
        string exePath = Path.Combine(_vendorDir, ExecutableName);
        Directory.CreateDirectory(_vendorDir);
        File.WriteAllText(exePath, "current");
        var existing = new DenoInfo(exePath, "2.9.5");
        IDenoLocator locator = Substitute.For<IDenoLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(existing);

        // An unreachable URL would fail if a download were attempted; it must not be.
        var manifest = ManifestFor(new Uri("http://127.0.0.1:1/never.zip"), new string('b', 64), version: "v2.9.5");
        DenoProvisioner provisioner = CreateProvisioner(manifest, locator);

        DenoInfo? result = await provisioner.EnsureAsync();

        result.Should().BeSameAs(existing);
        locator.DidNotReceive().Invalidate();
    }

    [Fact]
    public async Task EnsureAsync_DoesNotUpgrade_WhenExistingIsAtCustomConfiguredPath()
    {
        // Not inside the vendor directory — represents a user-configured DenoPath or a PATH-resolved binary.
        var existing = new DenoInfo(Path.Combine(Path.GetTempPath(), "custom-deno", ExecutableName), "1.0.0");
        IDenoLocator locator = Substitute.For<IDenoLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(existing);

        // An unreachable URL would fail if a download were attempted; it must not be.
        var manifest = ManifestFor(new Uri("http://127.0.0.1:1/never.zip"), new string('b', 64), version: "v2.9.5");
        DenoProvisioner provisioner = CreateProvisioner(manifest, locator);

        DenoInfo? result = await provisioner.EnsureAsync();

        result.Should().BeSameAs(existing, "a user-configured/PATH binary must never be auto-upgraded");
        locator.DidNotReceive().Invalidate();
    }

    private DenoProvisioner CreateProvisioner(DenoManifest manifest, IDenoLocator locator)
    {
        var options = new DenoOptions { VendorDirectory = _vendorDir };
        IAppInfoProvider appInfo = Substitute.For<IAppInfoProvider>();
        appInfo.Name.Returns("JustDownloadTest");

        return new DenoProvisioner(
            locator,
            options,
            manifest,
            new ChecksumVerifier(),
            new TestHandlerProvider(),
            appInfo,
            NullLogger<DenoProvisioner>.Instance);
    }

    private static DenoManifest ManifestFor(Uri url, string sha256, string version = "test") =>
        new([new DenoDownloadSource(FfmpegManifest.CurrentRuntimeIdentifier, version, url, sha256)]);

    /// <summary>Builds a minimal zip holding only the executable at its root (Deno's real layout — no
    /// <c>bin/</c> folder), and its hash.</summary>
    private static (byte[] Archive, string Sha256) BuildFakeDenoArchive()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, ExecutableName, "fake-deno-binary");
        }

        byte[] bytes = buffer.ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return (bytes, hash);
    }

    private static void WriteEntry(ZipArchive zip, string path, string content)
    {
        ZipArchiveEntry entry = zip.CreateEntry(path);
        using StreamWriter writer = new(entry.Open());
        writer.Write(content);
    }

    private sealed class TestHandlerProvider : ISharedHttpHandlerProvider
    {
        public SocketsHttpHandler Handler { get; } = new();

        public void Dispose() => Handler.Dispose();
    }
}
