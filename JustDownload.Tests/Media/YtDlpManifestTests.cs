using System.Text.RegularExpressions;
using FluentAssertions;
using JustDownload.Core.Media;
using Xunit;

namespace JustDownload.Tests.Media;

/// <summary>
/// The pinned yt-dlp download manifest (TASK-162, D3). Asserts every entry is integrity-pinned over HTTPS,
/// that the well-known platforms are covered, and that platform lookup is exact.
/// </summary>
public sealed class YtDlpManifestTests
{
    [Fact]
    public void Default_IsNotEmpty()
    {
        YtDlpManifest.Default.Sources.Should().NotBeEmpty();
    }

    [Fact]
    public void Default_PinsEveryBuildBySha256OverHttps()
    {
        foreach (YtDlpDownloadSource source in YtDlpManifest.Default.Sources)
        {
            source.Url.Scheme.Should().Be("https", "downloads must be over TLS");
            Regex.IsMatch(source.Sha256, "^[0-9a-f]{64}$")
                .Should().BeTrue("each build is pinned by a 64-char SHA-256 hex digest");
            source.RuntimeIdentifier.Should().NotBeNullOrWhiteSpace();
            source.Version.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void Default_CoversWindowsLinuxAndMacOs_X64()
    {
        YtDlpManifest.Default.TryGet("win-x64", out _).Should().BeTrue();
        YtDlpManifest.Default.TryGet("linux-x64", out _).Should().BeTrue();
        YtDlpManifest.Default.TryGet("osx-x64", out _).Should().BeTrue();
    }

    [Fact]
    public void Default_MacOsUniversalBinary_CoversBothArchitectures()
    {
        // yt-dlp ships one universal2 macOS executable — the same URL/hash covers x64 and arm64.
        YtDlpManifest.Default.TryGet("osx-x64", out YtDlpDownloadSource x64).Should().BeTrue();
        YtDlpManifest.Default.TryGet("osx-arm64", out YtDlpDownloadSource arm64).Should().BeTrue();
        x64.Url.Should().Be(arm64.Url);
        x64.Sha256.Should().Be(arm64.Sha256);
    }

    [Fact]
    public void TryGet_IsCaseInsensitive_AndMissesUnknown()
    {
        YtDlpManifest.Default.TryGet("WIN-X64", out _).Should().BeTrue();
        YtDlpManifest.Default.TryGet("solaris-sparc", out YtDlpDownloadSource none).Should().BeFalse();
        none.Should().BeNull();
    }

    [Fact]
    public void TryGetForCurrentPlatform_UsesTheSameRuntimeIdentifierAsFfmpeg()
    {
        // yt-dlp reuses FfmpegManifest.CurrentRuntimeIdentifier (DRY) rather than duplicating OS/arch
        // detection — this pins that the two manifests' current-platform lookups agree.
        YtDlpManifest.Default.TryGetForCurrentPlatform(out YtDlpDownloadSource viaHelper);
        YtDlpManifest.Default.TryGet(FfmpegManifest.CurrentRuntimeIdentifier, out YtDlpDownloadSource viaDirectRid);

        viaHelper.Should().Be(viaDirectRid);
    }
}
