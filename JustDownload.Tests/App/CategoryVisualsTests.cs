using FluentAssertions;
using JustDownload.App.Converters;
using JustDownload.Core.Categorization;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>Unit tests for the pure category→visual-resource-key mapping (TASK-051).</summary>
public sealed class CategoryVisualsTests
{
    [Theory]
    [InlineData(FileCategory.Video, "IconCatVideo", "CatVideoFg", "CatVideoBg")]
    [InlineData(FileCategory.Audio, "IconCatAudio", "CatAudioFg", "CatAudioBg")]
    [InlineData(FileCategory.Document, "IconCatDocument", "CatDocumentFg", "CatDocumentBg")]
    [InlineData(FileCategory.Compressed, "IconCatCompressed", "CatCompressedFg", "CatCompressedBg")]
    [InlineData(FileCategory.Program, "IconCatProgram", "CatProgramFg", "CatProgramBg")]
    [InlineData(FileCategory.Image, "IconCatImage", "CatImageFg", "CatImageBg")]
    [InlineData(FileCategory.Other, "IconCatOther", "CatOtherFg", "CatOtherBg")]
    public void MapsEveryCategoryToItsKeys(FileCategory category, string geometry, string foreground, string background)
    {
        CategoryVisuals.GeometryKey(category).Should().Be(geometry);
        CategoryVisuals.ForegroundKey(category).Should().Be(foreground);
        CategoryVisuals.BackgroundKey(category).Should().Be(background);
    }
}
