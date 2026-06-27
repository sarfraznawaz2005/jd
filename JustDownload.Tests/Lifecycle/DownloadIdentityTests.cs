using FluentAssertions;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Lifecycle;
using JustDownload.Core.Transport;
using Xunit;

namespace JustDownload.Tests.Lifecycle;

/// <summary>Unit tests for the pure renew identity rule (TASK-032 AC1-2).</summary>
public sealed class DownloadIdentityTests
{
    private static Download Record(string? etag, long? size) => new()
    {
        Url = "https://example.com/file.bin",
        Status = DownloadStatusCodes.Expired,
        ETag = etag,
        TotalBytes = size,
    };

    private static ResourceProbeResult Probe(string? etag, long? size) => new()
    {
        FinalUri = new Uri("https://cdn.example.com/file.bin"),
        StatusCode = 206,
        SupportsRanges = true,
        TotalLength = size,
        SuggestedFileName = "file.bin",
        ETag = etag,
    };

    [Fact]
    public void Matches_WhenEtagsEqual()
    {
        DownloadIdentity.Matches(Record("\"abc\"", 100), Probe("\"abc\"", 999)).Should().BeTrue();
    }

    [Fact]
    public void Mismatch_WhenEtagsDiffer_EvenIfSizeEqual()
    {
        DownloadIdentity.Matches(Record("\"abc\"", 100), Probe("\"xyz\"", 100)).Should().BeFalse();
    }

    [Fact]
    public void Matches_OnSize_WhenNoEtag()
    {
        DownloadIdentity.Matches(Record(null, 100), Probe(null, 100)).Should().BeTrue();
    }

    [Fact]
    public void Mismatch_OnSize_WhenNoEtag()
    {
        DownloadIdentity.Matches(Record(null, 100), Probe(null, 200)).Should().BeFalse();
    }

    [Fact]
    public void Mismatch_WhenNoComparableValidator()
    {
        DownloadIdentity.Matches(Record(null, null), Probe(null, 100)).Should().BeFalse();
        DownloadIdentity.Matches(Record(null, 100), Probe(null, null)).Should().BeFalse();
    }
}
