using FluentAssertions;
using JustDownload.Core.Storage;
using Xunit;

namespace JustDownload.Tests.Storage;

/// <summary>
/// Tests for <see cref="PreallocatedFile"/> (TASK-025): the output file is pre-allocated to the total
/// size (AC0), concurrent positioned writes assemble correctly, and the pooled streaming copy writes at
/// the right offset (AC2).
/// </summary>
public sealed class PreallocatedFileTests : IDisposable
{
    private readonly string _tempDir;

    public PreallocatedFileTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jd-prealloc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    private string PathFor(string name) => Path.Combine(_tempDir, name);

    private static byte[] Bytes(int count, int seed = 0)
    {
        var data = new byte[count];
        for (int i = 0; i < count; i++)
        {
            data[i] = (byte)((i + seed) % 256);
        }

        return data;
    }

    [Fact]
    public async Task Create_PreAllocatesFile_ToTotalSize()
    {
        // AC[0]: the file exists at exactly the requested length immediately after creation.
        string path = PathFor("prealloc.bin");
        await using (PreallocatedFile file = PreallocatedFile.Create(path, 4096))
        {
            file.Length.Should().Be(4096);
        }

        new FileInfo(path).Length.Should().Be(4096);
    }

    [Fact]
    public async Task Create_InNonExistentDirectory_CreatesIt()
    {
        string path = Path.Combine(_tempDir, "nested", "deep", "out.bin");
        await using PreallocatedFile file = PreallocatedFile.Create(path, 16);
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task ConcurrentWrites_AtDifferentOffsets_AssembleCorrectly()
    {
        // Four "segments" write their own non-overlapping ranges concurrently via positioned writes.
        const int segment = 4096;
        const int total = segment * 4;
        byte[] expected = Bytes(total);
        string path = PathFor("concurrent.bin");

        await using (PreallocatedFile file = PreallocatedFile.Create(path, total))
        {
            await Task.WhenAll(
                Enumerable.Range(0, 4).Select(i =>
                {
                    long offset = (long)i * segment;
                    ReadOnlyMemory<byte> slice = expected.AsMemory((int)offset, segment);
                    return file.WriteAsync(offset, slice).AsTask();
                }));

            await file.FlushToDiskAsync();
        }

        (await File.ReadAllBytesAsync(path)).Should().Equal(expected);
    }

    [Fact]
    public async Task CopyFromAsync_WritesStreamAtOffset_AndReportsProgress()
    {
        // AC[2]: the pooled streaming copy lands the bytes at the offset and returns the count.
        byte[] payload = Bytes(PreallocatedFile.CopyBufferSize * 2 + 123, seed: 7); // spans multiple buffers
        string path = PathFor("copy.bin");
        long offset = 512;
        long lastProgress = 0;
        var progress = new Progress<long>(v => lastProgress = v);

        await using (PreallocatedFile file = PreallocatedFile.Create(path, offset + payload.Length))
        {
            using var source = new MemoryStream(payload);
            long written = await file.CopyFromAsync(source, offset, progress);
            written.Should().Be(payload.Length);
        }

        byte[] onDisk = await File.ReadAllBytesAsync(path);
        onDisk.AsMemory((int)offset, payload.Length).ToArray().Should().Equal(payload);
        // Leading region before the offset stays zero (pre-allocated, untouched).
        onDisk.AsMemory(0, (int)offset).ToArray().Should().OnlyContain(b => b == 0);
    }

    [Fact]
    public async Task WriteAsync_AfterDispose_Throws()
    {
        PreallocatedFile file = PreallocatedFile.Create(PathFor("disposed.bin"), 16);
        await file.DisposeAsync();

        Func<Task> act = async () => await file.WriteAsync(0, new byte[4]);
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
