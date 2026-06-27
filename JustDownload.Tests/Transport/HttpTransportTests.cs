using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Transport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JustDownload.Tests.Transport;

/// <summary>
/// Integration tests for <see cref="HttpTransport"/> over a real loopback HTTP server (TASK-023):
/// ranged GET issues partial requests (AC0), the suggested file name comes from Content-Disposition then
/// the URL (AC1), and the single shared <see cref="SocketsHttpHandler"/> is reused (AC2).
/// </summary>
public sealed class HttpTransportTests
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

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return memory.ToArray();
    }

    [Fact]
    public async Task RangedGet_Returns206_WithExactBytesAndContentRange()
    {
        // AC[0]: a Range header produces a 206 with exactly the requested window.
        byte[] body = Bytes(1000);
        await using var server = new LoopbackHttpServer { Body = body };
        using ServiceProvider provider = BuildProvider();
        var transport = provider.GetRequiredService<ITransport>();

        await using ITransportResponse response = await transport.SendAsync(new TransportRequest
        {
            Uri = server.Url("file.bin"),
            Range = new ByteRange(100, 199),
        });

        response.StatusCode.Should().Be(206);
        response.IsPartialContent.Should().BeTrue();
        response.ContentLength.Should().Be(100);
        response.ContentRange.Should().Be(new ContentRange(100, 199, 1000));

        await using Stream content = await response.OpenContentStreamAsync();
        byte[] received = await ReadAllAsync(content);
        received.Should().Equal(body[100..200]);
    }

    [Fact]
    public async Task OpenEndedRange_Returns206_ToEndOfResource()
    {
        byte[] body = Bytes(500);
        await using var server = new LoopbackHttpServer { Body = body };
        using ServiceProvider provider = BuildProvider();
        var transport = provider.GetRequiredService<ITransport>();

        await using ITransportResponse response = await transport.SendAsync(new TransportRequest
        {
            Uri = server.Url("file.bin"),
            Range = new ByteRange(400, null),
        });

        response.IsPartialContent.Should().BeTrue();
        response.ContentRange!.Value.TotalLength.Should().Be(500);
        byte[] received = await ReadAllAsync(await response.OpenContentStreamAsync());
        received.Should().Equal(body[400..]);
    }

    [Fact]
    public async Task FullGet_Returns200_WithWholeBody_AndAcceptsRanges()
    {
        byte[] body = Bytes(256);
        await using var server = new LoopbackHttpServer { Body = body };
        using ServiceProvider provider = BuildProvider();
        var transport = provider.GetRequiredService<ITransport>();

        await using ITransportResponse response =
            await transport.SendAsync(new TransportRequest { Uri = server.Url("file.bin") });

        response.StatusCode.Should().Be(200);
        response.IsPartialContent.Should().BeFalse();
        response.IsSuccessStatusCode.Should().BeTrue();
        response.AcceptsRanges.Should().BeTrue();
        response.ContentLength.Should().Be(256);
        (await ReadAllAsync(await response.OpenContentStreamAsync())).Should().Equal(body);
    }

    [Fact]
    public async Task SuggestedFileName_ComesFromContentDisposition()
    {
        // AC[1]: Content-Disposition wins.
        await using var server = new LoopbackHttpServer
        {
            Body = Bytes(10),
            ContentDisposition = "attachment; filename=\"hello.bin\"",
        };
        using ServiceProvider provider = BuildProvider();
        var transport = provider.GetRequiredService<ITransport>();

        await using ITransportResponse response =
            await transport.SendAsync(new TransportRequest { Uri = server.Url("ignored-name.dat") });

        response.SuggestedFileName.Should().Be("hello.bin");
    }

    [Fact]
    public async Task SuggestedFileName_FallsBackToUrl_WhenNoContentDisposition()
    {
        // AC[1]: with no Content-Disposition, the URL's last segment is used.
        await using var server = new LoopbackHttpServer { Body = Bytes(10) };
        using ServiceProvider provider = BuildProvider();
        var transport = provider.GetRequiredService<ITransport>();

        await using ITransportResponse response =
            await transport.SendAsync(new TransportRequest { Uri = server.Url("downloads/report.pdf") });

        response.SuggestedFileName.Should().Be("report.pdf");
    }

    [Fact]
    public async Task SharedHandler_IsSingleton_AndReusedAcrossRequests()
    {
        // AC[2]: one shared SocketsHttpHandler, reused for every request.
        await using var server = new LoopbackHttpServer { Body = Bytes(64) };
        using ServiceProvider provider = BuildProvider();

        var first = provider.GetRequiredService<ISharedHttpHandlerProvider>();
        var second = provider.GetRequiredService<ISharedHttpHandlerProvider>();
        first.Should().BeSameAs(second);
        ReferenceEquals(first.Handler, second.Handler).Should().BeTrue();

        var transport = provider.GetRequiredService<ITransport>();
        await using (ITransportResponse r1 = await transport.SendAsync(new TransportRequest { Uri = server.Url("a") }))
        {
            r1.IsSuccessStatusCode.Should().BeTrue();
        }

        await using ITransportResponse r2 = await transport.SendAsync(new TransportRequest { Uri = server.Url("b") });
        r2.IsSuccessStatusCode.Should().BeTrue();
    }

    [Fact]
    public async Task NonRangeServer_StillReportsNoAcceptRanges()
    {
        await using var server = new LoopbackHttpServer { Body = Bytes(128), SupportRanges = false };
        using ServiceProvider provider = BuildProvider();
        var transport = provider.GetRequiredService<ITransport>();

        await using ITransportResponse response =
            await transport.SendAsync(new TransportRequest { Uri = server.Url("file.bin") });

        response.StatusCode.Should().Be(200);
        response.AcceptsRanges.Should().BeFalse();
    }
}
