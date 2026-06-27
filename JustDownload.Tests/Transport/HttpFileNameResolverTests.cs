using FluentAssertions;
using JustDownload.Core.Transport;
using Xunit;

namespace JustDownload.Tests.Transport;

/// <summary>
/// Tests for <see cref="HttpFileNameResolver"/> (TASK-023 AC1): file name derived from
/// <c>Content-Disposition</c> first, then the URL, always sanitised to a safe bare name.
/// </summary>
public sealed class HttpFileNameResolverTests
{
    [Theory]
    [InlineData("attachment; filename=\"report.pdf\"", "report.pdf")]
    [InlineData("attachment; filename=report.pdf", "report.pdf")]
    [InlineData("attachment; filename*=UTF-8''na%C3%AFve%20file.txt", "naïve file.txt")]
    [InlineData("attachment; filename=\"../../etc/passwd\"", "passwd")]
    [InlineData("inline", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void FromContentDisposition_ParsesAndSanitizes(string? header, string? expected)
    {
        HttpFileNameResolver.FromContentDisposition(header).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://example.com/a/b/file.zip", "file.zip")]
    [InlineData("https://example.com/download", "download")]
    [InlineData("https://example.com/my%20file.dat", "my file.dat")]
    public void FromUri_UsesLastDecodedSegment(string url, string expected)
    {
        HttpFileNameResolver.FromUri(new Uri(url)).Should().Be(expected);
    }

    [Fact]
    public void FromUri_FallsBackToHost_WhenNoPathSegment()
    {
        HttpFileNameResolver.FromUri(new Uri("https://example.com/")).Should().Be("example.com");
    }

    [Fact]
    public void Resolve_PrefersContentDisposition_OverUrl()
    {
        HttpFileNameResolver
            .Resolve("attachment; filename=\"chosen.bin\"", new Uri("https://example.com/other.zip"))
            .Should().Be("chosen.bin");
    }

    [Fact]
    public void Resolve_FallsBackToUrl_WhenDispositionHasNoFileName()
    {
        HttpFileNameResolver
            .Resolve("inline", new Uri("https://example.com/path/movie.mkv"))
            .Should().Be("movie.mkv");
    }

    [Fact]
    public void Resolve_NeverReturnsEmpty()
    {
        HttpFileNameResolver.Resolve(null, new Uri("https://example.com/")).Should().Be("example.com");
    }
}
