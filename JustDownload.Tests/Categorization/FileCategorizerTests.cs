using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Categorization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JustDownload.Tests.Categorization;

/// <summary>
/// Tests for file-type categorization (TASK-044 / PRD US-8). Cover extension resolution for every
/// category, MIME resolution (exact and top-level), the documented extension-then-MIME precedence, the
/// <see cref="FileCategory.Other"/> fallback, and that editing the rules changes the result (proving
/// "rules user-editable", AC2).
/// </summary>
public sealed class FileCategorizerTests
{
    private static FileCategorizer NewCategorizer() =>
        new(CategorizationRules.CreateDefault());

    [Theory]
    [InlineData("movie.mp4", FileCategory.Video)]
    [InlineData("clip.MKV", FileCategory.Video)]
    [InlineData("song.mp3", FileCategory.Audio)]
    [InlineData("track.flac", FileCategory.Audio)]
    [InlineData("report.pdf", FileCategory.Document)]
    [InlineData("sheet.xlsx", FileCategory.Document)]
    [InlineData("notes.txt", FileCategory.Document)]
    [InlineData("archive.zip", FileCategory.Compressed)]
    [InlineData("backup.tar.gz", FileCategory.Compressed)]
    [InlineData("disk.iso", FileCategory.Compressed)]
    [InlineData("setup.exe", FileCategory.Program)]
    [InlineData("installer.msi", FileCategory.Program)]
    [InlineData("app.apk", FileCategory.Program)]
    [InlineData("photo.jpg", FileCategory.Image)]
    [InlineData("logo.PNG", FileCategory.Image)]
    [InlineData("icon.svg", FileCategory.Image)]
    public void Categorize_ResolvesEveryCategory_FromExtension(string fileName, FileCategory expected)
    {
        // AC[0] (extension half) + AC[1]: all seven PRD categories are reachable from an extension.
        NewCategorizer().Categorize(fileName).Should().Be(expected);
    }

    [Fact]
    public void Categorize_CoversAllPrdCategories_AtLeastOnce()
    {
        // AC[1]: prove the default rules can produce each of the seven PRD categories.
        var categorizer = NewCategorizer();

        var produced = new[]
        {
            categorizer.Categorize("a.mp4"),
            categorizer.Categorize("a.mp3"),
            categorizer.Categorize("a.pdf"),
            categorizer.Categorize("a.zip"),
            categorizer.Categorize("a.exe"),
            categorizer.Categorize("a.png"),
            categorizer.Categorize("a.unknownxyz"),
        };

        produced.Should().BeEquivalentTo(new[]
        {
            FileCategory.Video,
            FileCategory.Audio,
            FileCategory.Document,
            FileCategory.Compressed,
            FileCategory.Program,
            FileCategory.Image,
            FileCategory.Other,
        });

        Enum.GetValues<FileCategory>().Should().BeSubsetOf(produced);
    }

    [Theory]
    [InlineData("mp4", FileCategory.Video)]
    [InlineData(".mp4", FileCategory.Video)]
    [InlineData(".MP4", FileCategory.Video)]
    public void Categorize_AcceptsBareAndDottedExtensionTokens(string token, FileCategory expected)
    {
        NewCategorizer().Categorize(token).Should().Be(expected);
    }

    [Theory]
    [InlineData("video/mp4", FileCategory.Video)]
    [InlineData("video/x-matroska", FileCategory.Video)]
    [InlineData("audio/mpeg", FileCategory.Audio)]
    [InlineData("image/png", FileCategory.Image)]
    [InlineData("image/svg+xml", FileCategory.Image)]
    [InlineData("text/plain", FileCategory.Document)]
    [InlineData("application/pdf", FileCategory.Document)]
    [InlineData("application/zip", FileCategory.Compressed)]
    [InlineData("application/x-msdownload", FileCategory.Program)]
    [InlineData("application/vnd.android.package-archive", FileCategory.Program)]
    public void Categorize_ResolvesFromMimeType_WhenNoExtension(string contentType, FileCategory expected)
    {
        // AC[0] (MIME half): with no filename, the content type alone resolves the category, including
        // unknown subtypes via the top-level (video/audio/image/text) fallback.
        NewCategorizer().Categorize(fileNameOrExtension: null, contentType).Should().Be(expected);
    }

    [Fact]
    public void Categorize_IgnoresMimeParameters()
    {
        NewCategorizer()
            .Categorize(fileNameOrExtension: null, "text/html; charset=utf-8")
            .Should().Be(FileCategory.Document);
    }

    [Fact]
    public void Categorize_ExtensionWinsOverMime_WhenBothKnownAndConflict()
    {
        // AC[0]: documented precedence — a recognised extension beats the content type.
        var categorizer = NewCategorizer();

        // .mp4 says Video; the (deliberately wrong) content type says Audio. Extension must win.
        categorizer.Categorize("song.mp4", "audio/mpeg").Should().Be(FileCategory.Video);
    }

    [Fact]
    public void Categorize_FallsBackToMime_WhenExtensionUnknown()
    {
        // AC[0]: precedence fallback — an unrecognised extension yields to the content type.
        var categorizer = NewCategorizer();

        categorizer.Categorize("download.unknownxyz", "video/mp4").Should().Be(FileCategory.Video);
    }

    [Theory]
    [InlineData("README")]
    [InlineData("file.unknownext")]
    [InlineData("")]
    [InlineData(null)]
    public void Categorize_ReturnsOther_ForUnknownOrMissingInput(string? fileName)
    {
        // AC[1]: the Other fallback is reachable and never throws for unknown/empty/null input.
        NewCategorizer().Categorize(fileName).Should().Be(FileCategory.Other);
    }

    [Fact]
    public void Categorize_ReturnsOther_WhenBothInputsNull()
    {
        NewCategorizer().Categorize(null, null).Should().Be(FileCategory.Other);
    }

    [Fact]
    public void Categorize_ReturnsOther_ForGenericOctetStream()
    {
        // application/octet-stream is intentionally unmapped — it carries no type information.
        NewCategorizer()
            .Categorize(fileNameOrExtension: null, "application/octet-stream")
            .Should().Be(FileCategory.Other);
    }

    [Fact]
    public void Categorize_HonoursUserAddedRule_ForNewExtension()
    {
        // AC[2]: a brand-new mapping the user adds takes effect.
        var rules = CategorizationRules.CreateDefault();
        rules.MapExtension("zxc", FileCategory.Program);
        var categorizer = new FileCategorizer(rules);

        categorizer.Categorize("tool.zxc").Should().Be(FileCategory.Program);
    }

    [Fact]
    public void Categorize_HonoursUserOverride_OfDefaultExtension()
    {
        // AC[2]: a user override of a seeded default changes the result.
        var before = NewCategorizer();
        before.Categorize("a.mp4").Should().Be(FileCategory.Video);

        var rules = CategorizationRules.CreateDefault();
        rules.MapExtension("mp4", FileCategory.Audio); // reclassify .mp4 as Audio
        var after = new FileCategorizer(rules);

        after.Categorize("a.mp4").Should().Be(FileCategory.Audio);
    }

    [Fact]
    public void Categorize_HonoursUserAddedMimeRule()
    {
        // AC[2]: editable on the MIME side too.
        var rules = CategorizationRules.CreateDefault();
        rules.MapMimeType("application/x-custom-pack", FileCategory.Compressed);
        var categorizer = new FileCategorizer(rules);

        categorizer
            .Categorize(fileNameOrExtension: null, "application/x-custom-pack")
            .Should().Be(FileCategory.Compressed);
    }

    [Fact]
    public void CompositionRoot_RegistersCategorizer_AndSharesEditableRules()
    {
        // The DI seam resolves, and the registered rules instance is the same one the categorizer uses,
        // so a host editing the resolved rules affects categorization (AC[2] via the composition root).
        using ServiceProvider provider = new ServiceCollection()
            .AddJustDownloadCore()
            .BuildServiceProvider();

        var categorizer = provider.GetRequiredService<IFileCategorizer>();
        categorizer.Categorize("a.mp4").Should().Be(FileCategory.Video);

        var rules = provider.GetRequiredService<CategorizationRules>();
        rules.MapExtension("qwerty", FileCategory.Document);

        categorizer.Categorize("doc.qwerty").Should().Be(FileCategory.Document);
    }

    [Fact]
    public void MapExtension_RejectsEmptyExtension()
    {
        var rules = new CategorizationRules();

        Action act = () => rules.MapExtension("  ", FileCategory.Video);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MapMimeType_RejectsUndefinedCategory()
    {
        var rules = new CategorizationRules();

        Action act = () => rules.MapMimeType("application/pdf", (FileCategory)999);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
