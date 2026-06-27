using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Media;
using JustDownload.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JustDownload.Tests.Media;

/// <summary>
/// Integration tests for stream-copy remux (TASK-042) using a committed h264 <c>.ts</c> fixture and the
/// real ffmpeg present on the host: ts→mp4 produces a valid file (AC0), the container setting is honored
/// (AC2), and a failed remux leaves the source intact with no partial output (AC2). Skipped gracefully if
/// ffmpeg is unavailable.
/// </summary>
public sealed class MediaConverterTests : IDisposable
{
    private static readonly string FixtureTs =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.ts");

    private readonly string _tempDir;

    public MediaConverterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jd-remux-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddJustDownloadMedia();
        return services.BuildServiceProvider();
    }

    private static async Task<(ServiceProvider Provider, IMediaConverter Converter)?> SetUpAsync()
    {
        ServiceProvider provider = BuildProvider();
        if (await provider.GetRequiredService<IFfmpegLocator>().LocateAsync() is null)
        {
            provider.Dispose();
            return null; // ffmpeg not installed — skip the integration assertion.
        }

        return (provider, provider.GetRequiredService<IMediaConverter>());
    }

    private string CopyFixture(string name)
    {
        string dest = Path.Combine(_tempDir, name);
        File.Copy(FixtureTs, dest);
        return dest;
    }

    [Fact]
    public async Task Remux_TsToMp4_ProducesReadableMp4()
    {
        if (await SetUpAsync() is not var (provider, converter) || provider is null)
        {
            return;
        }

        using (provider)
        {
            string input = CopyFixture("clip.ts");
            string output = await converter.RemuxAsync(input, MediaContainer.Mp4);

            output.Should().EndWith(".mp4");
            File.Exists(output).Should().BeTrue();
            new FileInfo(output).Length.Should().BeGreaterThan(0);

            // The mp4 must be readable by ffmpeg (a real demux), proving a valid container.
            FfmpegRunResult verify = await provider.GetRequiredService<IFfmpegRunner>()
                .RunAsync(["-v", "error", "-i", output, "-f", "null", "-"]);
            verify.Succeeded.Should().BeTrue($"the remuxed mp4 should decode cleanly; stderr: {verify.StandardError}");
        }
    }

    [Fact]
    public async Task Remux_HonorsMkvContainer()
    {
        if (await SetUpAsync() is not var (provider, converter) || provider is null)
        {
            return;
        }

        using (provider)
        {
            string input = CopyFixture("clip.ts");
            string output = await converter.RemuxAsync(input, MediaContainer.Mkv);

            output.Should().EndWith(".mkv");
            File.Exists(output).Should().BeTrue();
        }
    }

    [Fact]
    public async Task Remux_Failure_KeepsSource_AndLeavesNoOutput()
    {
        if (await SetUpAsync() is not var (provider, converter) || provider is null)
        {
            return;
        }

        using (provider)
        {
            // Not real media — ffmpeg will fail to remux it.
            string input = Path.Combine(_tempDir, "garbage.ts");
            await File.WriteAllBytesAsync(input, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
            string expectedOutput = Path.ChangeExtension(input, ".mp4");

            Func<Task> act = async () => await converter.RemuxAsync(input, MediaContainer.Mp4);

            await act.Should().ThrowAsync<FfmpegException>();
            File.Exists(input).Should().BeTrue("the source must be kept on failure");
            File.Exists(expectedOutput).Should().BeFalse("no partial output should remain");
        }
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
