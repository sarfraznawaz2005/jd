using JustDownload.Core.Categorization;

namespace JustDownload.App.Converters;

/// <summary>
/// Pure mapping from a <see cref="FileCategory"/> to the resource keys for its list-row icon glyph and colour
/// tints (TASK-051). Keeping the mapping here — rather than inline in a converter — makes it unit-testable and
/// the single place the category→visual contract is defined.
/// </summary>
public static class CategoryVisuals
{
    /// <summary>The <c>StreamGeometry</c> resource key for the category's glyph.</summary>
    public static string GeometryKey(FileCategory category) => category switch
    {
        FileCategory.Video => "IconCatVideo",
        FileCategory.Audio => "IconCatAudio",
        FileCategory.Document => "IconCatDocument",
        FileCategory.Compressed => "IconCatCompressed",
        FileCategory.Program => "IconCatProgram",
        FileCategory.Image => "IconCatImage",
        _ => "IconCatOther",
    };

    /// <summary>The foreground (glyph) brush resource key for the category.</summary>
    public static string ForegroundKey(FileCategory category) => category switch
    {
        FileCategory.Video => "CatVideoFg",
        FileCategory.Audio => "CatAudioFg",
        FileCategory.Document => "CatDocumentFg",
        FileCategory.Compressed => "CatCompressedFg",
        FileCategory.Program => "CatProgramFg",
        FileCategory.Image => "CatImageFg",
        _ => "CatOtherFg",
    };

    /// <summary>The background (tile tint) brush resource key for the category.</summary>
    public static string BackgroundKey(FileCategory category) => category switch
    {
        FileCategory.Video => "CatVideoBg",
        FileCategory.Audio => "CatAudioBg",
        FileCategory.Document => "CatDocumentBg",
        FileCategory.Compressed => "CatCompressedBg",
        FileCategory.Program => "CatProgramBg",
        FileCategory.Image => "CatImageBg",
        _ => "CatOtherBg",
    };
}
