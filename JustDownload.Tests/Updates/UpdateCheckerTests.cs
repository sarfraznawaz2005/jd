using System.Net.Http;
using System.Runtime.InteropServices;
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
/// The opt-in GitHub Releases update check (TASK-080/TASK-223): version detection, ECDSA P-256 signature
/// verification of <c>checksums.txt</c>, and SHA-256 verification of the downloaded asset, all driven
/// against a real loopback server with real cryptography (not mocked) — the same bar the ffmpeg/yt-dlp
/// provisioner tests hold. <see cref="UpdateChecker.CheckAsync"/> only detects/verifies (never downloads
/// the installer or applies); <see cref="UpdateChecker.DownloadAndApplyAsync"/> — exercised separately —
/// is the only path that downloads and launches, run once the caller (the user, via "Update Now")
/// confirms. Also covers the two fail-closed paths (AC2 disabled, unconfigured production key) that must
/// never make a network call.
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
    public async Task CheckAsync_ReportsAvailable_WhenSignatureAndHashAreValid()
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
            // TASK-223: CheckAsync only detects/verifies — it never downloads the installer or applies it.
            result.Status.Should().Be(UpdateCheckStatus.Available);
            result.InstallerAssetName.Should().Be(UpdateChecker.WindowsInstallerAssetName);
            result.InstallerDownloadUrl.Should().NotBeNull();
            result.ExpectedSha256.Should().Be(sha256);
        }
        else
        {
            // No macOS/Linux installer format exists yet (TASK-077/078) — detected, not applied.
            result.Status.Should().Be(UpdateCheckStatus.AvailableForManualDownload);
        }

        applier.ApplyCount.Should().Be(0, "CheckAsync must never download or launch anything (TASK-223)");
        result.LatestVersion.Should().Be("2.0.0");
        checker.LastResult.Should().Be(result);
    }

    [Fact]
    public async Task DownloadAndApplyAsync_DownloadsVerifiesAndApplies_WhenCheckResultIsAvailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Apply is Windows-only in this checker instance (real OS/arch, no override) — TASK-172 covers
            // macOS/Linux explicitly below via the forced-platform constructor.
            return;
        }

        await using var server = new PathRoutingLoopbackServer();
        (byte[] installerBytes, string sha256) = FakeInstaller();
        byte[] checksumsBytes = ChecksumsFile(sha256);
        byte[] signature = TestSigningKeys.Sign(checksumsBytes);
        RouteFullRelease(server, "v2.0.0", installerBytes, checksumsBytes, signature);

        var applier = new FakeUpdateApplier();
        UpdateChecker checker = CreateChecker(server, autoUpdateEnabled: true, TestSigningKeys.PublicKeyBase64, applier, currentVersion: "1.0.0");
        UpdateCheckResult checkResult = await checker.CheckAsync();
        checkResult.Status.Should().Be(UpdateCheckStatus.Available);

        var progress = new RecordingProgress();
        UpdateCheckResult result = await checker.DownloadAndApplyAsync(checkResult, progress);

        result.Status.Should().Be(UpdateCheckStatus.Applied);
        applier.ApplyCount.Should().Be(1);
        applier.AppliedPath.Should().NotBeNull();
        File.Exists(applier.AppliedPath!).Should().BeTrue("the verified installer was downloaded before being handed to the applier");
        File.ReadAllBytes(applier.AppliedPath!).Should().Equal(installerBytes);
        checker.LastResult.Should().Be(result);
        progress.Reports.Should().NotBeEmpty("the loopback response declares Content-Length, so real progress must be reported");
        progress.Reports[^1].Should().BeApproximately(1.0, 0.0001, "the final report must reflect a fully-downloaded file");
    }

    [Fact]
    public async Task DownloadAndApplyAsync_Throws_WhenCheckResultIsNotAvailable()
    {
        var applier = new FakeUpdateApplier();
        await using var server = new PathRoutingLoopbackServer();
        UpdateChecker checker = CreateChecker(server, autoUpdateEnabled: true, TestSigningKeys.PublicKeyBase64, applier);

        Func<Task> act = () => checker.DownloadAndApplyAsync(new UpdateCheckResult(UpdateCheckStatus.UpToDate, "1.0.0"));

        await act.Should().ThrowAsync<ArgumentException>(
            "a caller must only pass a result whose Status is Available — this is a programming error, not a user path");
    }

    [Theory]
    [InlineData(true, false, false, Architecture.X64, "JustDownload-win-x64-Setup.exe")]
    [InlineData(false, true, false, Architecture.Arm64, "JustDownload-2.1.0-osx-arm64.dmg")]
    [InlineData(false, true, false, Architecture.X64, "JustDownload-2.1.0-osx-x64.dmg")]
    [InlineData(false, false, true, Architecture.X64, "JustDownload-2.1.0-x86_64.AppImage")]
    [InlineData(false, true, false, Architecture.Arm, null)] // no 32-bit-ARM macOS build is published
    [InlineData(false, false, true, Architecture.Arm64, null)] // only the x64 AppImage is auto-applied (TASK-172)
    [InlineData(false, false, false, Architecture.X64, null)] // an OS with no packaging at all (e.g. FreeBSD)
    public void ResolveInstallerAssetName_PicksThePerOsArchAssetOrNull(
        bool isWindows, bool isMacOs, bool isLinux, Architecture architecture, string? expected)
    {
        string? actual = UpdateChecker.ResolveInstallerAssetName(isWindows, isMacOs, isLinux, architecture, "2.1.0");

        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData(Architecture.Arm64, "JustDownload-2.0.0-osx-arm64.dmg")]
    [InlineData(Architecture.X64, "JustDownload-2.0.0-osx-x64.dmg")]
    public async Task DownloadAndApplyAsync_DownloadsVerifiesAndApplies_OnMacOs(Architecture architecture, string assetName)
    {
        await using var server = new PathRoutingLoopbackServer();
        (byte[] installerBytes, string sha256) = FakeInstaller();
        byte[] checksumsBytes = ChecksumsFile(sha256, assetName);
        byte[] signature = TestSigningKeys.Sign(checksumsBytes);
        RouteFullRelease(server, "v2.0.0", installerBytes, checksumsBytes, signature, assetName);

        var applier = new FakeUpdateApplier();
        UpdateChecker checker = CreateChecker(
            server, autoUpdateEnabled: true, TestSigningKeys.PublicKeyBase64, applier, currentVersion: "1.0.0",
            isWindows: false, isMacOs: true, isLinux: false, architecture: architecture);

        UpdateCheckResult checkResult = await checker.CheckAsync();
        checkResult.Status.Should().Be(UpdateCheckStatus.Available);

        UpdateCheckResult result = await checker.DownloadAndApplyAsync(checkResult);

        result.Status.Should().Be(UpdateCheckStatus.Applied);
        result.LatestVersion.Should().Be("2.0.0");
        applier.ApplyCount.Should().Be(1);
        applier.AppliedPath.Should().NotBeNull();
        Path.GetFileName(applier.AppliedPath!).Should().Be(assetName, "the arm64/x64 dmg must match the resolved arch, not the other one");
        File.ReadAllBytes(applier.AppliedPath!).Should().Equal(installerBytes);
    }

    [Fact]
    public async Task DownloadAndApplyAsync_DownloadsVerifiesAndApplies_OnLinux()
    {
        const string assetName = "JustDownload-2.0.0-x86_64.AppImage";
        await using var server = new PathRoutingLoopbackServer();
        (byte[] installerBytes, string sha256) = FakeInstaller();
        byte[] checksumsBytes = ChecksumsFile(sha256, assetName);
        byte[] signature = TestSigningKeys.Sign(checksumsBytes);
        RouteFullRelease(server, "v2.0.0", installerBytes, checksumsBytes, signature, assetName);

        var applier = new FakeUpdateApplier();
        UpdateChecker checker = CreateChecker(
            server, autoUpdateEnabled: true, TestSigningKeys.PublicKeyBase64, applier, currentVersion: "1.0.0",
            isWindows: false, isMacOs: false, isLinux: true, architecture: Architecture.X64);

        UpdateCheckResult checkResult = await checker.CheckAsync();
        checkResult.Status.Should().Be(UpdateCheckStatus.Available);

        UpdateCheckResult result = await checker.DownloadAndApplyAsync(checkResult);

        result.Status.Should().Be(UpdateCheckStatus.Applied);
        result.LatestVersion.Should().Be("2.0.0");
        applier.ApplyCount.Should().Be(1);
        Path.GetFileName(applier.AppliedPath!).Should().Be(assetName);
        File.ReadAllBytes(applier.AppliedPath!).Should().Equal(installerBytes);
    }

    [Fact]
    public async Task CheckAsync_ReportsAvailableForManualDownload_OnLinux_WhenReleaseHasNoLinuxAsset()
    {
        // A release that only carries the Windows asset (today's actual release workflow, TASK-080 —
        // widening it to publish macOS/Linux assets too is a separate, un-landed follow-up).
        await using var server = new PathRoutingLoopbackServer();
        server.RouteJson($"/repos/{Owner}/{Repo}/releases/latest", ReleaseJson(
            "v2.0.0",
            (UpdateChecker.WindowsInstallerAssetName, server.Url(UpdateChecker.WindowsInstallerAssetName).ToString())));

        var applier = new FakeUpdateApplier();
        UpdateChecker checker = CreateChecker(
            server, autoUpdateEnabled: true, TestSigningKeys.PublicKeyBase64, applier, currentVersion: "1.0.0",
            isWindows: false, isMacOs: false, isLinux: true, architecture: Architecture.X64);

        UpdateCheckResult result = await checker.CheckAsync();

        result.Status.Should().Be(UpdateCheckStatus.AvailableForManualDownload);
        applier.ApplyCount.Should().Be(0);
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
    public async Task DownloadAndApplyAsync_Rejects_WhenDownloadedAssetDoesNotMatchItsSignedHash()
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

        var applier = new FakeUpdateApplier();
        UpdateChecker checker = CreateChecker(server, autoUpdateEnabled: true, TestSigningKeys.PublicKeyBase64, applier, currentVersion: "1.0.0");
        UpdateCheckResult checkResult = await checker.CheckAsync();
        checkResult.Status.Should().Be(UpdateCheckStatus.Available);

        // An attacker swaps the asset after checksums.txt was computed/signed and after the check ran.
        server.Route(UpdateChecker.WindowsInstallerAssetName, Encoding.UTF8.GetBytes("swapped-malicious-bytes"));

        UpdateCheckResult result = await checker.DownloadAndApplyAsync(checkResult);

        result.Status.Should().Be(UpdateCheckStatus.RejectedAssetHashMismatch);
        applier.ApplyCount.Should().Be(0);
        Directory.Exists(_vendorDir).Should().BeTrue();
        Directory.GetFiles(_vendorDir).Should().NotContain(f => Path.GetFileName(f) == UpdateChecker.WindowsInstallerAssetName,
            "a hash-mismatched download must not be kept as if it were trusted");
    }

    [Fact]
    public async Task CheckAsync_ReportsNotConfigured_AndMakesNoNetworkCall_WhenPublicKeyIsEmpty()
    {
        await using var server = new PathRoutingLoopbackServer();
        server.RouteJson($"/repos/{Owner}/{Repo}/releases/latest", ReleaseJson("v9.9.9")); // would 200 if ever called
        var applier = new FakeUpdateApplier();

        // An empty string, not UpdateSigningKey.ProductionPublicKeyBase64: that constant is the real
        // committed production key as of TASK-171 (the "unconfigured placeholder" state it started in is
        // no longer its live value), but the fail-closed behavior this test guards -- an empty/unparseable
        // key must never be treated as "trust anyway" -- has to hold regardless of whether a real key is
        // currently configured, so it's asserted directly against a literal empty key here.
        UpdateChecker checker = CreateChecker(server, autoUpdateEnabled: true, publicKeyBase64: "", applier);

        UpdateCheckResult result = await checker.CheckAsync();

        result.Status.Should().Be(UpdateCheckStatus.NotConfigured);
        server.RequestCount.Should().Be(0, "an unconfigured signing key must fail closed with zero downloads attempted");
        applier.ApplyCount.Should().Be(0);
    }

    [Fact]
    public void ProductionPublicKey_IsConfigured_AndImportsAsAValidEcdsaP256Key()
    {
        // TASK-171: once the maintainer has generated and committed the real production key, it must
        // actually be usable -- a non-empty string that fails to import would ALSO fail closed
        // (TryImportPublicKey catches CryptographicException), silently masking a bad paste.
        UpdateSigningKey.ProductionPublicKeyBase64.Should().NotBeNullOrWhiteSpace(
            "TASK-171 AC0: the maintainer has generated and committed the real production key");

        using ECDsa key = ECDsa.Create();
        Action import = () => key.ImportSubjectPublicKeyInfo(
            Convert.FromBase64String(UpdateSigningKey.ProductionPublicKeyBase64), out _);
        import.Should().NotThrow("the committed key must be a well-formed DER SubjectPublicKeyInfo");
        key.KeySize.Should().Be(256, "the policy (docs/release-signing.md) is ECDSA P-256");
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
        string currentVersion = "1.0.0",
        bool? isWindows = null,
        bool? isMacOs = null,
        bool? isLinux = null,
        Architecture architecture = Architecture.X64)
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

        // Real OS/arch by default (matches every existing test's behaviour); explicit overrides let
        // TASK-172's macOS/Linux tests force those branches from a single (e.g. Windows) CI machine.
        return new UpdateChecker(
            settings, versionProvider, options, new TestHandlerProvider(), appInfo,
            new ChecksumVerifier(), applier,
            isWindows ?? OperatingSystem.IsWindows(),
            isMacOs ?? OperatingSystem.IsMacOS(),
            isLinux ?? OperatingSystem.IsLinux(),
            architecture,
            NullLogger<UpdateChecker>.Instance);
    }

    private static void RouteFullRelease(
        PathRoutingLoopbackServer server, string tag, byte[] installerBytes, byte[] checksumsBytes, byte[] signatureBytes,
        string? installerAssetName = null)
    {
        string assetName = installerAssetName ?? UpdateChecker.WindowsInstallerAssetName;
        server.RouteJson($"/repos/{Owner}/{Repo}/releases/latest", ReleaseJson(
            tag,
            (assetName, server.Url(assetName).ToString()),
            (UpdateChecker.ChecksumsAssetName, server.Url(UpdateChecker.ChecksumsAssetName).ToString()),
            (UpdateChecker.ChecksumsSignatureAssetName, server.Url(UpdateChecker.ChecksumsSignatureAssetName).ToString())));
        server.Route(assetName, installerBytes);
        server.Route(UpdateChecker.ChecksumsAssetName, checksumsBytes);
        server.Route(UpdateChecker.ChecksumsSignatureAssetName, signatureBytes);
    }

    private static byte[] ChecksumsFile(string sha256Hex, string? installerAssetName = null) =>
        Encoding.UTF8.GetBytes($"{sha256Hex}  {installerAssetName ?? UpdateChecker.WindowsInstallerAssetName}\n");

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

    /// <summary>A synchronous <see cref="IProgress{T}"/> test double (TASK-223) — unlike <see cref="Progress{T}"/>,
    /// this never marshals through a captured <see cref="SynchronizationContext"/>, so assertions made right
    /// after the awaited call see every report deterministically.</summary>
    private sealed class RecordingProgress : IProgress<double>
    {
        public List<double> Reports { get; } = [];

        public void Report(double value) => Reports.Add(value);
    }

    private sealed class TestHandlerProvider : ISharedHttpHandlerProvider
    {
        public SocketsHttpHandler Handler { get; } = new();

        public void Dispose() => Handler.Dispose();
    }
}
