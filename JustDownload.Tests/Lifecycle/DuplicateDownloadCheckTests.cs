using FluentAssertions;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Lifecycle;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.Lifecycle;

/// <summary>
/// Pre-download duplicate detection (TASK-139): a file already on disk at the destination, a file with a
/// matching size, a prior library record to the same path, and the no-collision case.
/// </summary>
public sealed class DuplicateDownloadCheckTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jd-dup-" + Guid.NewGuid().ToString("N"));
    private readonly IDownloadRepository _repository = Substitute.For<IDownloadRepository>();

    public DuplicateDownloadCheckTests()
    {
        Directory.CreateDirectory(_dir);
        _repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<Download>());
    }

    private DuplicateDownloadCheck Check() => new(_repository);

    [Fact]
    public async Task FileOnDisk_WithMatchingSize_ReportsStrongDuplicate()
    {
        string path = Path.Combine(_dir, "file.bin");
        await File.WriteAllBytesAsync(path, new byte[2048]);

        DuplicateCheckResult result = await Check().CheckAsync(_dir, "file.bin", expectedSize: 2048);

        result.Kind.Should().Be(DuplicateKind.FileExistsOnDisk);
        result.ExistingSizeOnDisk.Should().Be(2048);
        result.SizeMatches.Should().BeTrue();
    }

    [Fact]
    public async Task FileOnDisk_WithDifferentSize_ReportsExistsButNotSizeMatch()
    {
        string path = Path.Combine(_dir, "file.bin");
        await File.WriteAllBytesAsync(path, new byte[100]);

        DuplicateCheckResult result = await Check().CheckAsync(_dir, "file.bin", expectedSize: 2048);

        result.Kind.Should().Be(DuplicateKind.FileExistsOnDisk);
        result.SizeMatches.Should().BeFalse();
    }

    [Fact]
    public async Task NotOnDisk_ButInLibrary_ReportsAlreadyInLibrary()
    {
        _repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            new Download { Url = "u", Status = "complete", Directory = _dir, Filename = "gone.bin" },
        });

        DuplicateCheckResult result = await Check().CheckAsync(_dir, "gone.bin", expectedSize: null);

        result.Kind.Should().Be(DuplicateKind.AlreadyInLibrary);
    }

    [Fact]
    public async Task NoCollision_ReportsNone()
    {
        DuplicateCheckResult result = await Check().CheckAsync(_dir, "fresh.bin", expectedSize: 999);

        result.IsDuplicate.Should().BeFalse();
    }

    [Fact]
    public async Task BlankInputs_ReportNone()
    {
        (await Check().CheckAsync("", "file.bin", null)).IsDuplicate.Should().BeFalse();
        (await Check().CheckAsync(_dir, "", null)).IsDuplicate.Should().BeFalse();
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
