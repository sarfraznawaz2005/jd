using System.Text.RegularExpressions;
using FluentAssertions;
using JustDownload.Core.Media;
using Xunit;

namespace JustDownload.Tests.Media;

/// <summary>
/// The pinned Deno download manifest (the JS runtime yt-dlp needs for YouTube's signature/JS challenges).
/// Asserts every entry is integrity-pinned over HTTPS, that the well-known platforms are covered, and that
/// platform lookup is exact — mirrors <see cref="FfmpegManifestTests"/>/<c>YtDlpManifestTests</c>.
/// </summary>
public sealed class DenoManifestTests
{
    [Fact]
    public void Default_IsNotEmpty()
    {
        DenoManifest.Default.Sources.Should().NotBeEmpty();
    }

    [Fact]
    public void Default_PinsEveryBuildBySha256OverHttps()
    {
        foreach (DenoDownloadSource source in DenoManifest.Default.Sources)
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
        DenoManifest.Default.TryGet("win-x64", out _).Should().BeTrue();
        DenoManifest.Default.TryGet("linux-x64", out _).Should().BeTrue();
        DenoManifest.Default.TryGet("osx-x64", out _).Should().BeTrue();
        DenoManifest.Default.TryGet("osx-arm64", out _).Should().BeTrue();
    }

    [Fact]
    public void Default_MacOsBuilds_AreDistinctPerArchitecture()
    {
        // Unlike yt-dlp's universal2 macOS binary, Deno ships separate x64/arm64 archives.
        DenoManifest.Default.TryGet("osx-x64", out DenoDownloadSource x64).Should().BeTrue();
        DenoManifest.Default.TryGet("osx-arm64", out DenoDownloadSource arm64).Should().BeTrue();
        x64.Url.Should().NotBe(arm64.Url);
        x64.Sha256.Should().NotBe(arm64.Sha256);
    }

    [Fact]
    public void TryGet_IsCaseInsensitive_AndMissesUnknown()
    {
        DenoManifest.Default.TryGet("WIN-X64", out _).Should().BeTrue();
        DenoManifest.Default.TryGet("solaris-sparc", out DenoDownloadSource none).Should().BeFalse();
        none.Should().BeNull();
    }

    [Fact]
    public void TryGetForCurrentPlatform_UsesTheSameRuntimeIdentifierAsFfmpeg()
    {
        DenoManifest.Default.TryGetForCurrentPlatform(out DenoDownloadSource viaHelper);
        DenoManifest.Default.TryGet(FfmpegManifest.CurrentRuntimeIdentifier, out DenoDownloadSource viaDirectRid);

        viaHelper.Should().Be(viaDirectRid);
    }
}
