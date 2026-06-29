using System.Text.RegularExpressions;
using FluentAssertions;
using JustDownload.Core.Media;
using Xunit;

namespace JustDownload.Tests.Media;

/// <summary>
/// The pinned ffmpeg download manifest (TASK-079). Asserts the LGPL-only policy (AC1: never a GPL build),
/// that every entry is integrity-pinned (AC0), and that platform lookup is exact.
/// </summary>
public sealed class FfmpegManifestTests
{
    [Fact]
    public void Default_ListsOnlyLgplBuilds()
    {
        FfmpegManifest.Default.Sources.Should().NotBeEmpty();
        FfmpegManifest.Default.Sources.Should().OnlyContain(
            s => s.License == FfmpegBuildLicense.Lgpl, "no GPL build may ever be shipped (D7 / §4)");
    }

    [Fact]
    public void Default_PinsEveryBuildBySha256OverHttps()
    {
        foreach (FfmpegDownloadSource source in FfmpegManifest.Default.Sources)
        {
            source.Url.Scheme.Should().Be("https", "downloads must be over TLS");
            Regex.IsMatch(source.Sha256, "^[0-9a-f]{64}$")
                .Should().BeTrue("each build is pinned by a 64-char SHA-256 hex digest");
            source.RuntimeIdentifier.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void Default_CoversWindowsX64()
    {
        FfmpegManifest.Default.TryGet("win-x64", out FfmpegDownloadSource x64).Should().BeTrue();
        x64.License.Should().Be(FfmpegBuildLicense.Lgpl);
    }

    [Fact]
    public void TryGet_IsCaseInsensitive_AndMissesUnknown()
    {
        FfmpegManifest.Default.TryGet("WIN-X64", out _).Should().BeTrue();
        FfmpegManifest.Default.TryGet("solaris-sparc", out FfmpegDownloadSource none).Should().BeFalse();
        none.Should().BeNull();
    }

    [Fact]
    public void CurrentRuntimeIdentifier_IsOsDashArch()
    {
        Regex.IsMatch(FfmpegManifest.CurrentRuntimeIdentifier, "^[a-z]+-[a-z0-9]+$")
            .Should().BeTrue("the RID is os-arch, e.g. win-x64");
    }
}
