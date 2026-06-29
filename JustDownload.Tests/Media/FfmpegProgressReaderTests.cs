using FluentAssertions;
using JustDownload.Core.Media;
using Xunit;

namespace JustDownload.Tests.Media;

/// <summary>Unit tests for parsing ffmpeg's <c>-progress</c> stream (TASK-040 AC1).</summary>
public sealed class FfmpegProgressReaderTests
{
    private static FfmpegProgress? PushBlock(IEnumerable<string> lines)
    {
        var reader = new FfmpegProgressReader();
        FfmpegProgress? last = null;
        foreach (string line in lines)
        {
            if (reader.Push(line, out FfmpegProgress snapshot))
            {
                last = snapshot;
            }
        }

        return last;
    }

    [Fact]
    public void Parses_EndBlock_FromRealOutput()
    {
        // A real ffmpeg "-progress pipe:1" terminating block.
        FfmpegProgress? progress = PushBlock(
        [
            "bitrate=N/A",
            "total_size=N/A",
            "out_time_us=200000",
            "out_time_ms=200000",
            "out_time=00:00:00.200000",
            "speed=17.7x",
            "progress=end",
        ]);

        progress.Should().NotBeNull();
        progress!.Value.OutTime.Should().Be(TimeSpan.FromMilliseconds(200));
        progress.Value.Speed.Should().BeApproximately(17.7, 0.001);
        progress.Value.TotalSize.Should().BeNull("total_size was N/A");
        progress.Value.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void Parses_ContinueBlock_WithSizeAndSpeed()
    {
        FfmpegProgress? progress = PushBlock(
        [
            "out_time_us=5000000",
            "total_size=123456",
            "speed=2.0x",
            "progress=continue",
        ]);

        progress.Should().NotBeNull();
        progress!.Value.OutTime.Should().Be(TimeSpan.FromSeconds(5));
        progress.Value.TotalSize.Should().Be(123456);
        progress.Value.Speed.Should().BeApproximately(2.0, 0.001);
        progress.Value.IsComplete.Should().BeFalse();
    }

    [Fact]
    public void ResetsBetweenBlocks()
    {
        var reader = new FfmpegProgressReader();
        reader.Push("out_time_us=1000000", out _);
        reader.Push("progress=continue", out _);

        // Second block reports no out_time → defaults to zero, not the previous value.
        reader.Push("progress=continue", out FfmpegProgress second);
        second.OutTime.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void IgnoresNonKeyValueLines()
    {
        var reader = new FfmpegProgressReader();
        reader.Push("not a progress line", out _).Should().BeFalse();
        reader.Push(string.Empty, out _).Should().BeFalse();
    }
}
