using FluentAssertions;
using JustDownload.Core.Media.Streams;
using JustDownload.Core.Settings;
using Xunit;

namespace JustDownload.Tests.Media;

/// <summary>
/// Container selection for muxing (TASK-041 AC1): MKV is the default and the safe fallback; MP4 is chosen
/// only when both codecs are MP4-compatible, so a pure stream copy always works.
/// </summary>
public sealed class MuxContainerSelectorTests
{
    [Theory]
    [InlineData("h264", "aac")]
    [InlineData("avc1.640028", "mp4a.40.2")]
    [InlineData("hevc", "ac3")]
    [InlineData("av01.0.05M.08", "aac")]
    public void Select_Mp4Preferred_CompatibleCodecs_ReturnsMp4(string video, string audio)
    {
        MuxContainerSelector.Select(MediaContainer.Mp4, video, audio).Should().Be(MediaContainer.Mp4);
    }

    [Theory]
    [InlineData("vp9", "opus")]
    [InlineData("vp8", "vorbis")]
    [InlineData("h264", "flac")]
    [InlineData("vp9", "aac")]
    public void Select_Mp4Preferred_IncompatibleCodecs_FallsBackToMkv(string video, string audio)
    {
        MuxContainerSelector.Select(MediaContainer.Mp4, video, audio).Should().Be(MediaContainer.Mkv);
    }

    [Fact]
    public void Select_Mp4Preferred_UnknownCodecs_FallsBackToMkv()
    {
        MuxContainerSelector.Select(MediaContainer.Mp4, null, null).Should().Be(MediaContainer.Mkv);
    }

    [Theory]
    [InlineData(MediaContainer.Mkv)]
    [InlineData(MediaContainer.Webm)]
    public void Select_NonMp4Preference_AlwaysReturnsMkv(MediaContainer preferred)
    {
        MuxContainerSelector.Select(preferred, "h264", "aac").Should().Be(MediaContainer.Mkv);
    }
}
