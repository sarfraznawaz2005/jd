using FluentAssertions;
using JustDownload.Core.Logging;
using Xunit;

namespace JustDownload.Tests.Logging;

/// <summary>
/// The log-safe URL reducer (TASK-099, §5): a URL is reduced to scheme + host so signed-URL query tokens and
/// any userinfo never reach a log, regardless of the token parameter name.
/// </summary>
public sealed class SafeLogUrlTests
{
    [Theory]
    [InlineData("https://cdn.example.com/video.mp4?token=secret&sig=xyz", "https://cdn.example.com")]
    [InlineData("https://cdn.example.com:8443/path?X-Amz-Signature=abc", "https://cdn.example.com")]
    [InlineData("https://user:p%40ss@host.example/path?x=1", "https://host.example")] // userinfo stripped
    [InlineData("ftp://files.example/dir/file.bin?auth=deadbeef", "ftp://files.example")]
    public void Of_ReturnsSchemeAndHostOnly(string url, string expected) =>
        SafeLogUrl.Of(url).Should().Be(expected);

    [Theory]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    [InlineData("")]
    [InlineData(null)]
    public void Of_NonAbsolute_ReturnsPlaceholder(string? url) =>
        SafeLogUrl.Of(url).Should().Be("(non-absolute URL)");

    [Fact]
    public void Of_NeverContainsTheToken()
    {
        SafeLogUrl.Of("https://x.example/a?token=TOPSECRET").Should().NotContain("TOPSECRET");
    }
}
