using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using JustDownload.Core.Abstractions;
using JustDownload.Core.Integrity;
using JustDownload.Core.Settings;
using JustDownload.Core.Transport;
using JustDownload.Core.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.Updates;

/// <summary>
/// The opt-in GitHub Releases update check (TASK-080): version detection, ECDSA P-256 signature
/// verification of <c>checksums.txt</c>, and SHA-256 verification of the downloaded asset, all driven
/// against a real loopback server with real cryptography (not mocked) — the same bar the ffmpeg/yt-dlp
/// provisioner tests hold. Also covers the two fail-closed paths (AC2 disabled, unconfigured production key)
/// that must never make a network call.
/// </summary>
public sealed class UpdateCheckerTests : IDisposable
{
    private const string Owner = "owner";
    private const string Repo = "repo";

    private readonly string _vendorDir = Path.Combine(
        Path.GetTempPath(), "jd-update-test-" + Guid.NewGuid().ToString("N"));

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
    public async Task CheckAsync_ReportsUpToDate_WhenCurrentVersionIsNotBehindLatest()
    {
        await using var server = new PathRoutingLoopbackServer();
        server.RouteJson($"/repos/{Owner}/{Repo}/releases/latest", ReleaseJson("v1.0.0"));
        var applier = new FakeUpdateApplier();
        UpdateChecker checker = CreateChecker(server, autoUpdateEnabled: true, TestSigningKeys.PublicKeyBase64, applier, currentVersion: "1.0.0");

        UpdateCheckResult result = await checker.CheckAsync();

        result.Status.Should().Be(UpdateCheckStatus.UpToDate);
        result.LatestVersion.Should().Be("1.0.0");
        applier.ApplyCount.Should().Be(0);
    }

    [Fact]
    public async Task CheckAsync_DoesNotReportUpdate_WhenCurrentVersionIsAhead()
    {
        await using var server = new PathRoutingLoopbackServer();
        server.RouteJson($"/repos/{Owner}/{Repo}/releases/latest", ReleaseJson("v1.0.0"));
        var applier = new FakeUpdateApplier();
        UpdateChecker checker = CreateChecker(server, autoUpdateEnabled: true, TestSigningKeys.PublicKeyBase64, applier, currentVersion: "1.5.0");

        UpdateCheckResult result = await checker.CheckAsync();

        result.Status.Should().Be(UpdateCheckStatus.UpToDate);
    }

    [Fact]
    public async Task CheckAsync_VerifiesAndApplies_WhenSignatureAndHashAreValid()
    {
        await using var server = new PathRoutingLoopbackServer();
        (byte[] installerBytes, string sha256) = FakeInstaller();
        byte[] checksumsBytes = ChecksumsFile(sha256);
        byte[] signature = TestSigningKeys.Sign(checksumsBytes);

        RouteFullRelease(server, "v2.0.0", installerBytes, checksumsBytes, signature);

        var applier = new FakeUpdateApplier();
        UpdateChecker checker = CreateChecker(server, autoUpdateEnabled: true, TestSigningKeys.PublicKeyBase64, applier, currentVersion: "1.0.0");

        UpdateCheckResult result = await checker.CheckAsync();

        if (OperatingSystem.IsWindows())
        {
            // TASK-080 locked scope: apply is Windows-only. Verified via the injectable "would apply" seam
            // (FakeUpdateApplier) so no real process is spawned.
            result.Status.Should().Be(UpdateCheckStatus.Applied);
            applier.ApplyCount.Should().Be(1);
            applier.AppliedPath.Should().NotBeNull();
            File.Exists(applier.AppliedPath!).Should().BeTrue("the verified installer was downloaded before being handed to the applier");
            File.ReadAllBytes(applier.AppliedPath!).Should().Equal(installerBytes);
        }
        else
        {
            // No macOS/Linux installer format exists yet (TASK-077/078) — detected, not applied.
            result.Status.Should().Be(UpdateCheckStatus.AvailableForManualDownload);
            applier.ApplyCount.Should().Be(0);
        }

        result.LatestVersion.Should().Be("2.0.0");
    }

    [Fact]
    public async Task CheckAsync_Rejects_WhenSignatureAssetIsMissing()
    {
        if (!OperatingSystem.IsWindows())
        {
            // The signature is only fetched once a Windows installer asset is found (see the class-level
            // OS gate); off Windows there is nothing to reject against, so this scenario is Windows-only.
            return;
        }

        await using var server = new PathRoutingLoopbackServer();
        (byte[] installerBytes, string sha256) = FakeInstaller();
        byte[] checksumsBytes = ChecksumsFile(sha256);

        server.RouteJson($"/repos/{Owner}/{Repo}/releases/latest", ReleaseJson(
            "v2.0.0",
            (UpdateChecker.WindowsInstallerAssetName, server.Url(UpdateChecker.WindowsInstallerAssetName).ToString()),
            (UpdateChecker.ChecksumsAssetName, server.Url(UpdateChecker.ChecksumsAssetName).ToString())));
        server.Route(UpdateChecker.WindowsInstallerAssetName, installerBytes);
        server.Route(UpdateChecker.ChecksumsAssetName, checksumsBytes);
        // No checksums.txt.sig route registered — the release simply doesn't have one.

        var applier = new FakeUpdateApplier();
        UpdateChecker checker = CreateChecker(server, autoUpdateEnabled: true, TestSigningKeys.PublicKeyBase64, applier, currentVersion: "1.0.0");

        UpdateCheckResult result = await checker.CheckAsync();

        result.Status.Should().Be(UpdateCheckStatus.RejectedUnsigned);
        applier.ApplyCount.Should().Be(0);
    }

    [Fact]
    public async Task CheckAsync_Rejects_WhenSignatureBytesAreCorrupt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var server = new PathRoutingLoopbackServer();
        (byte[] installerBytes, string sha256) = FakeInstaller();
        byte[] checksumsBytes = ChecksumsFile(sha256);
        byte[] corruptSignature = [1, 2, 3, 4, 5, 6, 7, 8];

        RouteFullRelease(server, "v2.0.0", installerBytes, checksumsBytes, corruptSignature);

        var applier = new FakeUpdateApplier();
        UpdateChecker checker = CreateChecker(server, autoUpdateEnabled: true, TestSigningKeys.PublicKeyBase64, applier, currentVersion: "1.0.0");

        UpdateCheckResult result = await checker.CheckAsync();

        result.Status.Should().Be(UpdateCheckStatus.RejectedInvalidSignature);
        applier.ApplyCount.Should().Be(0);
    }

    [Fact]
    public async Task CheckAsync_Rejects_WhenChecksumsContentIsTamperedAfterSigning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var server = new PathRoutingLoopbackServer();
        (byte[] installerBytes, string sha256) = FakeInstaller();
        byte[] signedChecksums = ChecksumsFile(sha256);
        byte[] signature = TestSigningKeys.Sign(signedChecksums); // signs the honest content...

        // ...but an attacker swaps in a different hash after signing. The signature no longer matches.
        byte[] tamperedChecksums = ChecksumsFile(new string('f', 64));

        RouteFullRelease(server, "v2.0.0", installerBytes, tamperedChecksums, signature);

        var applier = new FakeUpdateApplier();
        UpdateChecker checker = CreateChecker(server, autoUpdateEnabled: true, TestSigningKeys.PublicKeyBase64, applier, currentVersion: "1.0.0");

        UpdateCheckResult result = await checker.CheckAsync();

        result.Status.Should().Be(UpdateCheckStatus.RejectedInvalidSignature);
        applier.ApplyCount.Should().Be(0);
    }

    [Fact]
    public async Task CheckAsync_Rejects_WhenDownloadedAssetDoesNotMatchItsSignedHash()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var server = new PathRoutingLoopbackServer();
        (byte[] installerBytes, string sha256) = FakeInstaller();
        byte[] checksumsBytes = ChecksumsFile(sha256); // correctly hashes the *original* installer bytes
        byte[] signature = TestSigningKeys.Sign(checksumsBytes);

        RouteFullRelease(server, "v2.0.0", installerBytes, checksumsBytes, signature);
        // An attacker swaps the asset after checksums.txt was computed and signed.
        server.Route(UpdateChecker.WindowsInstallerAssetName, Encoding.UTF8.GetBytes("swapped-malicious-bytes"));

        var applier = new FakeUpdateApplier();
        UpdateChecker checker = CreateChecker(server, autoUpdateEnabled: true, TestSigningKeys.PublicKeyBase64, applier, currentVersion: "1.0.0");

        UpdateCheckResult result = await checker.CheckAsync();

        result.Status.Should().Be(UpdateCheckStatus.RejectedAssetHashMismatch);
        applier.ApplyCount.Should().Be(0);
        Directory.Exists(_vendorDir).Should().BeTrue();
        Directory.GetFiles(_vendorDir).Should().NotContain(f => Path.GetFileName(f) == UpdateChecker.WindowsInstallerAssetName,
            "a hash-mismatched download must not be kept as if it were trusted");
    }

    [Fact]
    public async Task CheckAsync_ReportsNotConfigured_AndMakesNoNetworkCall_WhenPublicKeyIsThePlaceholder()
    {
        await using var server = new PathRoutingLoopbackServer();
        server.RouteJson($"/repos/{Owner}/{Repo}/releases/latest", ReleaseJson("v9.9.9")); // would 200 if ever called
        var applier = new FakeUpdateApplier();

        // The production placeholder is the empty string (UpdateSigningKey.ProductionPublicKeyBase64).
        UpdateChecker checker = CreateChecker(server, autoUpdateEnabled: true, UpdateSigningKey.ProductionPublicKeyBase64, applier);

        UpdateCheckResult result = await checker.CheckAsync();

        result.Status.Should().Be(UpdateCheckStatus.NotConfigured);
        server.RequestCount.Should().Be(0, "an unconfigured signing key must fail closed with zero downloads attempted");
        applier.ApplyCount.Should().Be(0);
    }

    [Fact]
    public async Task CheckAsync_MakesNoNetworkCall_WhenAutoUpdateIsDisabled()
    {
        await using var server = new PathRoutingLoopbackServer();
        server.RouteJson($"/repos/{Owner}/{Repo}/releases/latest", ReleaseJson("v9.9.9")); // would 200 if ever called
        var applier = new FakeUpdateApplier();

        UpdateChecker checker = CreateChecker(server, autoUpdateEnabled: false, TestSigningKeys.PublicKeyBase64, applier);

        UpdateCheckResult result = await checker.CheckAsync();

        result.Status.Should().Be(UpdateCheckStatus.Disabled);
        server.RequestCount.Should().Be(0, "AC2: no network call at all while auto-update is off");
        applier.ApplyCount.Should().Be(0);
    }

    // --- helpers -------------------------------------------------------------------------------

    private UpdateChecker CreateChecker(
        PathRoutingLoopbackServer server,
        bool autoUpdateEnabled,
        string publicKeyBase64,
        IUpdateApplier applier,
        string currentVersion = "1.0.0")
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings { AutoUpdateEnabled = autoUpdateEnabled });

        var appInfo = Substitute.For<IAppInfoProvider>();
        appInfo.Name.Returns("JustDownloadTest");

        var versionProvider = Substitute.For<IAppVersionProvider>();
        versionProvider.CurrentVersion.Returns(currentVersion);

        var options = new UpdateOptions
        {
            ApiBaseUri = server.BaseUri,
            RepositoryOwner = Owner,
            RepositoryName = Repo,
            PublicKeyBase64 = publicKeyBase64,
            VendorDirectory = _vendorDir,
        };

        return new UpdateChecker(
            settings, versionProvider, options, new TestHandlerProvider(), appInfo,
            new ChecksumVerifier(), applier, NullLogger<UpdateChecker>.Instance);
    }

    private static void RouteFullRelease(
        PathRoutingLoopbackServer server, string tag, byte[] installerBytes, byte[] checksumsBytes, byte[] signatureBytes)
    {
        server.RouteJson($"/repos/{Owner}/{Repo}/releases/latest", ReleaseJson(
            tag,
            (UpdateChecker.WindowsInstallerAssetName, server.Url(UpdateChecker.WindowsInstallerAssetName).ToString()),
            (UpdateChecker.ChecksumsAssetName, server.Url(UpdateChecker.ChecksumsAssetName).ToString()),
            (UpdateChecker.ChecksumsSignatureAssetName, server.Url(UpdateChecker.ChecksumsSignatureAssetName).ToString())));
        server.Route(UpdateChecker.WindowsInstallerAssetName, installerBytes);
        server.Route(UpdateChecker.ChecksumsAssetName, checksumsBytes);
        server.Route(UpdateChecker.ChecksumsSignatureAssetName, signatureBytes);
    }

    private static byte[] ChecksumsFile(string sha256Hex) =>
        Encoding.UTF8.GetBytes($"{sha256Hex}  {UpdateChecker.WindowsInstallerAssetName}\n");

    private static (byte[] Bytes, string Sha256Hex) FakeInstaller()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("fake-installer-bytes-" + Guid.NewGuid().ToString("N"));
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return (bytes, hash);
    }

    private static string ReleaseJson(string tag, params (string Name, string Url)[] assets)
    {
        string assetsJson = string.Join(",", assets.Select(a =>
            $$"""{"name":"{{a.Name}}","browser_download_url":"{{a.Url}}"}"""));
        return $$"""{"tag_name":"{{tag}}","html_url":"https://example.com/releases/{{tag}}","assets":[{{assetsJson}}]}""";
    }

    private sealed class FakeUpdateApplier : IUpdateApplier
    {
        public string? AppliedPath { get; private set; }

        public int ApplyCount { get; private set; }

        public Task ApplyAsync(string installerPath, CancellationToken cancellationToken = default)
        {
            AppliedPath = installerPath;
            ApplyCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class TestHandlerProvider : ISharedHttpHandlerProvider
    {
        public SocketsHttpHandler Handler { get; } = new();

        public void Dispose() => Handler.Dispose();
    }
}
