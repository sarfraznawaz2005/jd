using FluentAssertions;
using JustDownload.Core.Lifecycle;
using Xunit;

namespace JustDownload.Tests.Lifecycle;

/// <summary>Unit tests for pure expiry detection (TASK-032 AC0): status codes and signed-URL expiry parsing.</summary>
public sealed class ExpiryDetectionTests
{
    [Theory]
    [InlineData(403, true)]
    [InlineData(410, true)]
    [InlineData(404, false)]
    [InlineData(200, false)]
    [InlineData(401, false)]
    public void IsExpiryStatusCode_ClassifiesExpiryStatuses(int status, bool expected)
    {
        ExpiryDetection.IsExpiryStatusCode(status).Should().Be(expected);
    }

    [Fact]
    public void GetSignedUrlExpiry_ReadsEpochExpiresParam()
    {
        var url = new Uri("https://cdn.example.com/file.bin?Expires=1700000000&Signature=abc");
        ExpiryDetection.GetSignedUrlExpiry(url).Should().Be(DateTimeOffset.FromUnixTimeSeconds(1700000000));
    }

    [Fact]
    public void GetSignedUrlExpiry_ReadsAwsSigV4DatePlusLifetime()
    {
        var url = new Uri(
            "https://s3.example.com/file.bin?X-Amz-Date=20231114T220000Z&X-Amz-Expires=3600&X-Amz-Signature=z");
        ExpiryDetection.GetSignedUrlExpiry(url).Should()
            .Be(new DateTimeOffset(2023, 11, 14, 23, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void GetSignedUrlExpiry_ReadsAzureSasExpiry()
    {
        var url = new Uri("https://acct.blob.core.windows.net/c/file.bin?se=2023-11-14T23%3A00%3A00Z&sig=x");
        ExpiryDetection.GetSignedUrlExpiry(url).Should()
            .Be(new DateTimeOffset(2023, 11, 14, 23, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void GetSignedUrlExpiry_ReturnsNull_WhenNoExpiryParam()
    {
        ExpiryDetection.GetSignedUrlExpiry(new Uri("https://example.com/file.bin?x=1")).Should().BeNull();
    }

    [Fact]
    public void IsUrlExpired_ComparesAgainstNow()
    {
        var url = new Uri("https://cdn.example.com/file.bin?Expires=1700000000"); // 2023-11-14T22:13:20Z
        ExpiryDetection.IsUrlExpired(url, new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)).Should().BeTrue();
        ExpiryDetection.IsUrlExpired(url, new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero)).Should().BeFalse();
    }
}
