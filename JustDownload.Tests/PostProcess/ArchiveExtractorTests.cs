using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using JustDownload.Core.PostProcess;
using SharpCompress.Common;
using SharpCompress.Writers.Tar;
using Xunit;

namespace JustDownload.Tests.PostProcess;

/// <summary>
/// Post-download archive extraction (TASK-135 .zip, TASK-156 .7z/.rar): recognises the format by extension
/// and unpacks it into a sibling folder named after the archive; unsupported inputs are rejected.
/// <c>sample.7z</c>/<c>sample.rar</c> under Fixtures are SharpCompress's own MIT-licensed test archives
/// (github.com/adamhathcock/sharpcompress, tests/TestArchives/Archives/7Zip.LZMA2.7z and Rar.rar) — pulled in
/// because SharpCompress, the library under test, has no .7z/.rar encoder to author fresh ones with.
/// </summary>
public sealed class ArchiveExtractorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jd-archive-" + Guid.NewGuid().ToString("N"));
    private readonly ArchiveExtractor _extractor = new();

    public ArchiveExtractorTests() => Directory.CreateDirectory(_dir);

    [Theory]
    [InlineData("file.zip", true)]
    [InlineData("FILE.ZIP", true)]
    [InlineData("file.7z", true)]
    [InlineData("FILE.7Z", true)]
    [InlineData("file.rar", true)]
    [InlineData("FILE.RAR", true)]
    [InlineData("file.txt", false)]
    [InlineData("file", false)]
    public void CanExtract_RecognizesSupportedExtensions(string name, bool expected) =>
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
    public async Task ExtractAsync_UnsupportedExtension_Throws()
    {
        string txt = Path.Combine(_dir, "notes.txt");
        await File.WriteAllTextAsync(txt, "x");

        Func<Task> act = () => _extractor.ExtractAsync(txt);

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Theory]
    [InlineData("sample.7z")]
    [InlineData("sample.rar")]
    public async Task ExtractAsync_UnpacksSevenZipAndRarIntoSiblingFolder_ByteForByteCorrect(string fixtureName)
    {
        string archivePath = Path.Combine(_dir, fixtureName);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName), archivePath);

        string destination = await _extractor.ExtractAsync(archivePath);

        destination.Should().Be(Path.Combine(_dir, "sample"));
        Sha256Of(Path.Combine(destination, "jpg", "test.jpg"))
            .Should().Be("B251C7501FB0F55DD4A92FEABE0A6F5733BC40A02679498155FAE9B30138FC53");
        Sha256Of(Path.Combine(destination, "exe", "test.exe"))
            .Should().Be("8557928804F57ECC340B3BB38B095A3607474EC8DEB0076F316FCFE02B562106");
        Sha256Of(Path.Combine(destination, "тест.txt"))
            .Should().Be("4D581D93D369F6E1C9B295FF38D82DABD577F927DFAF0C35818C015C85E322D9");
    }

    [Fact]
    public async Task ExtractAsync_MaliciousEntryEscapingDestination_ThrowsAndWritesNothingOutside()
    {
        // SharpCompress has no .7z/.rar encoder, so a malicious .7z/.rar can't be authored here (or by
        // anyone) — which is exactly why this attack surface is real: any bytes named "*.7z"/"*.rar" get
        // opened by SharpCompress's own content-based format auto-detection, whatever the bytes actually
        // are. We build a real, arbitrary-path-carrying Tar archive (the one format SharpCompress *can*
        // write) with a ".7z" extension and a "../../" entry, and confirm ArchiveExtractor's own
        // path-traversal guard (ResolveEntryPath) rejects it before writing anything outside the
        // destination — the same "zip slip" protection the .zip path gets for free from ZipFile.
        string archivePath = Path.Combine(_dir, "evil.7z");
        WriteMaliciousTarArchive(archivePath);
        string escapedFile = Path.Combine(Path.GetTempPath(), "evil.txt");

        Func<Task> act = () => _extractor.ExtractAsync(archivePath);

        await act.Should().ThrowAsync<IOException>().WithMessage("*outside the destination directory*");
        File.Exists(escapedFile).Should().BeFalse();
    }

    private static string Sha256Of(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void WriteMaliciousTarArchive(string path)
    {
        using FileStream stream = File.Create(path);
        using var writer = new TarWriter(stream, new TarWriterOptions(CompressionType.None, finalizeArchiveOnClose: true));
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("pwned"));
        writer.Write("../../evil.txt", content, DateTime.UtcNow);
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
