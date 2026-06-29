using System.IO.Compression;
using FluentAssertions;
using JustDownload.Core.PostProcess;
using Xunit;

namespace JustDownload.Tests.PostProcess;

/// <summary>
/// Post-download archive extraction (TASK-135): recognises .zip by extension and unpacks it into a sibling
/// folder named after the archive; non-zip inputs are rejected.
/// </summary>
public sealed class ArchiveExtractorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jd-archive-" + Guid.NewGuid().ToString("N"));
    private readonly ArchiveExtractor _extractor = new();

    public ArchiveExtractorTests() => Directory.CreateDirectory(_dir);

    [Theory]
    [InlineData("file.zip", true)]
    [InlineData("FILE.ZIP", true)]
    [InlineData("file.7z", false)]
    [InlineData("file.txt", false)]
    [InlineData("file", false)]
    public void CanExtract_RecognizesZipByExtension(string name, bool expected) =>
        _extractor.CanExtract(name).Should().Be(expected);

    [Fact]
    public async Task ExtractAsync_UnpacksZipIntoSiblingFolderNamedAfterTheArchive()
    {
        string zipPath = Path.Combine(_dir, "bundle.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            AddEntry(zip, "readme.txt", "hello");
            AddEntry(zip, "sub/data.bin", "xyz");
        }

        string destination = await _extractor.ExtractAsync(zipPath);

        destination.Should().Be(Path.Combine(_dir, "bundle"));
        File.ReadAllText(Path.Combine(destination, "readme.txt")).Should().Be("hello");
        File.ReadAllText(Path.Combine(destination, "sub", "data.bin")).Should().Be("xyz");
    }

    [Fact]
    public async Task ExtractAsync_NonZip_Throws()
    {
        string txt = Path.Combine(_dir, "notes.txt");
        await File.WriteAllTextAsync(txt, "x");

        Func<Task> act = () => _extractor.ExtractAsync(txt);

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    private static void AddEntry(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(content);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
