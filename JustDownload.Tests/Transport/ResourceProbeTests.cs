using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Transport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JustDownload.Tests.Transport;

/// <summary>
/// Integration tests for <see cref="ResourceProbe"/> over a real loopback HTTP server (TASK-024): range
/// support is detected from a real 206 (AC0), a non-range server falls back to one connection (AC1), and
/// the total size is resolved or correctly reported as unknown (AC2).
/// </summary>
public sealed class ResourceProbeTests
{
    private static byte[] Bytes(int count)
    {
        var data = new byte[count];
        for (int i = 0; i < count; i++)
        {
            data[i] = (byte)(i % 256);
        }

        return data;
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddJustDownloadTransport();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Probe_RangeServer_DetectsSupport_AndSize()
    {
        // AC[0] + AC[2]: a 206 to the one-byte probe proves ranges; Content-Range gives the size.
        await using var server = new LoopbackHttpServer { Body = Bytes(1000), SupportRanges = true };
        using ServiceProvider provider = BuildProvider();
        var probe = provider.GetRequiredService<IResourceProbe>();

        ResourceProbeResult result = await probe.ProbeAsync(server.Url("video.mp4"));

        result.SupportsRanges.Should().BeTrue();
        result.TotalLength.Should().Be(1000);
        result.HasKnownLength.Should().BeTrue();
        result.CanUseMultipleConnections.Should().BeTrue();
        result.Resumable.Should().BeTrue();
        result.PlanConnectionCount(8).Should().Be(8);
        result.SuggestedFileName.Should().Be("video.mp4");
    }

    [Fact]
    public async Task Probe_NonRangeServer_FallsBackToSingleConnection()
    {
        // AC[0] + AC[1]: server ignores Range → no support, and the plan is a single connection.
        await using var server = new LoopbackHttpServer { Body = Bytes(500), SupportRanges = false };
        using ServiceProvider provider = BuildProvider();
        var probe = provider.GetRequiredService<IResourceProbe>();

        ResourceProbeResult result = await probe.ProbeAsync(server.Url("file.bin"));

        result.SupportsRanges.Should().BeFalse();
        result.TotalLength.Should().Be(500);
        result.CanUseMultipleConnections.Should().BeFalse();
        result.Resumable.Should().BeFalse();
        result.PlanConnectionCount(8).Should().Be(1);
    }

    [Fact]
    public async Task Probe_NonRangeServer_ActuallyDownloadsWholeFile_OverOneConnection()
    {
        // AC[1] end-to-end: when segmentation is impossible, a single GET fetches the whole resource.
        byte[] body = Bytes(777);
        await using var server = new LoopbackHttpServer { Body = body, SupportRanges = false };
        using ServiceProvider provider = BuildProvider();
        var probe = provider.GetRequiredService<IResourceProbe>();
        var transport = provider.GetRequiredService<ITransport>();

        ResourceProbeResult result = await probe.ProbeAsync(server.Url("file.bin"));
        result.PlanConnectionCount(8).Should().Be(1);

        await using ITransportResponse response =
            await transport.SendAsync(new TransportRequest { Uri = result.FinalUri });
        using var buffer = new MemoryStream();
        await (await response.OpenContentStreamAsync()).CopyToAsync(buffer);

        buffer.ToArray().Should().Equal(body);
    }

    [Fact]
    public async Task Probe_UnknownSize_WhenServerOmitsContentLength()
    {
        // AC[2]: a 200 without Content-Length yields an unknown size (and single-connection).
        await using var server = new LoopbackHttpServer
        {
            Body = Bytes(128),
            SupportRanges = false,
            SendContentLength = false,
        };
        using ServiceProvider provider = BuildProvider();
        var probe = provider.GetRequiredService<IResourceProbe>();

        ResourceProbeResult result = await probe.ProbeAsync(server.Url("stream.bin"));

        result.TotalLength.Should().BeNull();
        result.HasKnownLength.Should().BeFalse();
        result.CanUseMultipleConnections.Should().BeFalse();
        result.PlanConnectionCount(8).Should().Be(1);
    }

    [Fact]
    public async Task Probe_ResolvesFinalUri_AndValidatorsAreExposed()
    {
        await using var server = new LoopbackHttpServer { Body = Bytes(64), SupportRanges = true };
        using ServiceProvider provider = BuildProvider();
        var probe = provider.GetRequiredService<IResourceProbe>();

        ResourceProbeResult result = await probe.ProbeAsync(server.Url("a/b/c.zip"));

        result.FinalUri.Should().Be(server.Url("a/b/c.zip"));
        result.StatusCode.Should().Be(206);
        result.SuggestedFileName.Should().Be("c.zip");
    }
}
