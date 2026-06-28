using System.Security.Cryptography;
using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Downloading;
using JustDownload.Core.Media.Streams;
using JustDownload.Tests.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JustDownload.Tests.Media;

/// <summary>
/// Separate video+audio downloading (TASK-039): the two streams run concurrently with independent progress
/// (AC0/AC1) and a failure of one stream isolates rather than aborts the other, which stays resumable
/// (AC2). Fake-based tests pin the downloader's own behaviour; an integration test against the real engine
/// and a loopback server proves end-to-end correctness and partial-failure isolation.
/// </summary>
public sealed class SeparateStreamDownloaderTests : IDisposable
{
    private readonly List<string> _temp = [];

    private string TempPath(string suffix)
    {
        string path = Path.Combine(Path.GetTempPath(), $"jd-stream-{Guid.NewGuid():N}-{suffix}");
        _temp.Add(path);
        return path;
    }

    private static StreamDownloadRequest Req(
        Uri url, StreamRole role, string dest, IProgress<long>? progress = null, ReceivedRanges? received = null) =>
        new()
        {
            Spec = new MediaStreamSpec { Url = url, Role = role, DestinationPath = dest },
            Progress = progress,
            Received = received,
        };

    // --- Fake-based unit tests -------------------------------------------------------------------

    private sealed class FakeSegmentedDownloader : ISegmentedDownloader
    {
        private int _current;
        private int _peak;

        public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(40);

        public HashSet<string> FailUrls { get; } = new(StringComparer.Ordinal);

        public List<DownloadRequest> Requests { get; } = [];

        public int PeakConcurrency => Volatile.Read(ref _peak);

        public async Task<DownloadResult> DownloadAsync(
            DownloadRequest request,
            IProgress<long>? progress = null,
            ReceivedRanges? received = null,
            IProgress<ConnectionProgress>? connectionProgress = null,
            ConnectionController? connections = null,
            CancellationToken cancellationToken = default)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }

            int now = Interlocked.Increment(ref _current);
            UpdatePeak(now);
            try
            {
                await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }

            if (FailUrls.Contains(request.Url.ToString()))
            {
                throw new IOException("stream failed");
            }

            received?.Add(0, 10);
            progress?.Report(10);
            return new DownloadResult
            {
                TotalBytes = 10,
                FinalUri = request.Url,
                FileName = Path.GetFileName(request.DestinationPath),
                SingleConnection = false,
                InitialSegments = 1,
                Steals = 0,
            };
        }

        private void UpdatePeak(int candidate)
        {
            int peak;
            do
            {
                peak = Volatile.Read(ref _peak);
                if (candidate <= peak)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref _peak, candidate, peak) != peak);
        }
    }

    private static SeparateStreamDownloader Build(ISegmentedDownloader inner) =>
        new(inner, NullLogger<SeparateStreamDownloader>.Instance);

    [Fact]
    public async Task DownloadAsync_RunsBothStreamsConcurrently()
    {
        var fake = new FakeSegmentedDownloader();
        SeparateStreamResult result = await Build(fake).DownloadAsync(
            Req(new Uri("https://x/v.mp4"), StreamRole.Video, TempPath("v")),
            Req(new Uri("https://x/a.m4a"), StreamRole.Audio, TempPath("a")));

        result.AllSucceeded.Should().BeTrue();
        fake.PeakConcurrency.Should().Be(2, "the two streams download at the same time");
    }

    [Fact]
    public async Task DownloadAsync_OneStreamFails_OtherStillSucceeds_AndFailureIsIsolated()
    {
        var fake = new FakeSegmentedDownloader();
        fake.FailUrls.Add("https://x/a.m4a");
        var audioReceived = new ReceivedRanges();

        SeparateStreamResult result = await Build(fake).DownloadAsync(
            Req(new Uri("https://x/v.mp4"), StreamRole.Video, TempPath("v")),
            Req(new Uri("https://x/a.m4a"), StreamRole.Audio, TempPath("a"), received: audioReceived));

        result.Video.Succeeded.Should().BeTrue("the video stream is unaffected by the audio failure");
        result.Audio.Succeeded.Should().BeFalse();
        result.Audio.Error.Should().BeOfType<IOException>();
        result.AllSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task DownloadAsync_PassesPerStreamReceivedAndConnectionsThrough()
    {
        var fake = new FakeSegmentedDownloader();
        var videoReq = new StreamDownloadRequest
        {
            Spec = new MediaStreamSpec
            {
                Url = new Uri("https://x/v.mp4"),
                Role = StreamRole.Video,
                DestinationPath = TempPath("v"),
                Connections = 8,
            },
        };

        await Build(fake).DownloadAsync(videoReq, Req(new Uri("https://x/a.m4a"), StreamRole.Audio, TempPath("a")));

        fake.Requests.Should().Contain(r => r.Url == new Uri("https://x/v.mp4") && r.Connections == 8);
    }

    [Fact]
    public async Task DownloadAsync_ExternalCancellation_Propagates()
    {
        var fake = new FakeSegmentedDownloader { Delay = TimeSpan.FromSeconds(5) };
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        Func<Task> act = () => Build(fake).DownloadAsync(
            Req(new Uri("https://x/v.mp4"), StreamRole.Video, TempPath("v")),
            Req(new Uri("https://x/a.m4a"), StreamRole.Audio, TempPath("a")),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // --- Integration against the real engine -----------------------------------------------------

    private static ServiceProvider BuildEngine()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddJustDownloadTransport();
        services.AddJustDownloadStorage();
        services.AddJustDownloadDownloading();
        services.AddJustDownloadMedia();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task DownloadAsync_RealEngine_DownloadsBothStreams_ByteCorrect_WithIndependentProgress()
    {
        await using var videoServer = new LoopbackHttpServer { Body = RandomNumberGenerator.GetBytes(40000) };
        await using var audioServer = new LoopbackHttpServer { Body = RandomNumberGenerator.GetBytes(12000) };
        using ServiceProvider provider = BuildEngine();
        var sut = provider.GetRequiredService<ISeparateStreamDownloader>();

        var videoProgress = new List<long>();
        var audioProgress = new List<long>();
        string videoPath = TempPath("video.mp4");
        string audioPath = TempPath("audio.m4a");

        SeparateStreamResult result = await sut.DownloadAsync(
            Req(videoServer.Url("v.mp4"), StreamRole.Video, videoPath,
                new Progress<long>(v => { lock (videoProgress) { videoProgress.Add(v); } })),
            Req(audioServer.Url("a.m4a"), StreamRole.Audio, audioPath,
                new Progress<long>(v => { lock (audioProgress) { audioProgress.Add(v); } })));

        result.AllSucceeded.Should().BeTrue();
        (await File.ReadAllBytesAsync(videoPath)).Should().Equal(videoServer.Body);
        (await File.ReadAllBytesAsync(audioPath)).Should().Equal(audioServer.Body);

        await Task.Delay(50);
        lock (videoProgress)
        {
            videoProgress.Max().Should().Be(videoServer.Body.Length);
        }

        lock (audioProgress)
        {
            audioProgress.Max().Should().Be(audioServer.Body.Length, "each stream reports its own progress");
        }
    }

    [Fact]
    public async Task DownloadAsync_RealEngine_OneStreamFails_OtherCompletesIntact()
    {
        await using var videoServer = new LoopbackHttpServer { Body = RandomNumberGenerator.GetBytes(40000) };
        await using var audioServer = new LoopbackHttpServer
        {
            Body = RandomNumberGenerator.GetBytes(12000),
            StatusOverride = 403, // the audio link is withdrawn/expired
        };
        using ServiceProvider provider = BuildEngine();
        var sut = provider.GetRequiredService<ISeparateStreamDownloader>();

        string videoPath = TempPath("video.mp4");
        SeparateStreamResult result = await sut.DownloadAsync(
            Req(videoServer.Url("v.mp4"), StreamRole.Video, videoPath),
            Req(audioServer.Url("a.m4a"), StreamRole.Audio, TempPath("audio.m4a")));

        result.Video.Succeeded.Should().BeTrue("the video stream completes despite the audio failure");
        result.Audio.Succeeded.Should().BeFalse();
        result.AllSucceeded.Should().BeFalse();
        (await File.ReadAllBytesAsync(videoPath)).Should().Equal(videoServer.Body,
            "the surviving stream's file is intact");
    }

    public void Dispose()
    {
        foreach (string path in _temp)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
