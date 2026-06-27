using FluentAssertions;
using JustDownload.Core.Lifecycle;
using Xunit;

namespace JustDownload.Tests.Lifecycle;

/// <summary>Unit tests for the pure progress/ETA derivation (TASK-031 AC1 — progress/speed/ETA/resumable).</summary>
public sealed class DownloadProgressTests
{
    [Fact]
    public void Create_KnownTotal_ComputesFractionAndEta()
    {
        // 25 MB of 100 MB at 5 MB/s → 25% done, 15 s remaining.
        DownloadProgress p = DownloadProgress.Create(
            DownloadStatus.Active, 25_000_000, 100_000_000, 5_000_000, resumable: true);

        p.Fraction.Should().BeApproximately(0.25, 1e-9);
        p.Eta.Should().NotBeNull();
        p.Eta!.Value.TotalSeconds.Should().BeApproximately(15, 1e-6);
        p.BytesPerSecond.Should().Be(5_000_000);
        p.Resumable.Should().BeTrue();
    }

    [Fact]
    public void Create_UnknownTotal_LeavesFractionAndEtaNull()
    {
        DownloadProgress p = DownloadProgress.Create(
            DownloadStatus.Active, 1_000, totalBytes: null, bytesPerSecond: 1_000, resumable: false);

        p.Fraction.Should().BeNull();
        p.Eta.Should().BeNull();
        p.Resumable.Should().BeFalse();
    }

    [Fact]
    public void Create_ZeroSpeed_HasNoEta_ButStillHasFraction()
    {
        DownloadProgress p = DownloadProgress.Create(
            DownloadStatus.Active, 50, 100, bytesPerSecond: 0, resumable: true);

        p.Fraction.Should().BeApproximately(0.5, 1e-9);
        p.Eta.Should().BeNull();
    }

    [Fact]
    public void Create_Completed_HasZeroEtaAndFullFraction()
    {
        DownloadProgress p = DownloadProgress.Create(
            DownloadStatus.Completed, 100, 100, bytesPerSecond: 0, resumable: true);

        p.Fraction.Should().BeApproximately(1.0, 1e-9);
        p.Eta.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Create_ClampsOvershootAndNegatives()
    {
        // Reported bytes beyond the total must not push the fraction past 1 or the ETA negative.
        DownloadProgress over = DownloadProgress.Create(DownloadStatus.Active, 120, 100, 10, resumable: true);
        over.Fraction.Should().BeApproximately(1.0, 1e-9);
        over.Eta.Should().Be(TimeSpan.Zero);

        DownloadProgress neg = DownloadProgress.Create(DownloadStatus.Active, -5, 100, -3, resumable: true);
        neg.DownloadedBytes.Should().Be(0);
        neg.BytesPerSecond.Should().Be(0);
    }
}
