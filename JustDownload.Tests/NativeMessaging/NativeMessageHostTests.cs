using FluentAssertions;
using JustDownload.Core.NativeMessaging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JustDownload.Tests.NativeMessaging;

/// <summary>
/// End-to-end tests for the host loop over in-memory streams (TASK-064 AC0): framed requests are decoded,
/// dispatched, and framed replies written back — using only the supplied streams, never a socket (AC2).
/// </summary>
public sealed class NativeMessageHostTests
{
    private static NativeMessageHost CreateHost(INativeMessageHandler? handler = null) =>
        new(handler ?? new PingNativeMessageHandler(), new NativeHostOptions(), NullLogger<NativeMessageHost>.Instance);

    [Fact]
    public async Task ProcessesFramedRequests_AndWritesFramedReplies()
    {
        using var input = new MemoryStream();
        await NativeMessageCodec.WriteAsync(input, "{\"type\":\"ping\"}");
        await NativeMessageCodec.WriteAsync(input, "{\"type\":\"unknown\"}");
        input.Position = 0;

        using var output = new MemoryStream();
        await CreateHost().RunAsync(input, output);

        output.Position = 0;
        (await NativeMessageCodec.ReadAsync(output)).Should().Be("{\"type\":\"pong\"}");
        (await NativeMessageCodec.ReadAsync(output)).Should().Contain("error");
        (await NativeMessageCodec.ReadAsync(output)).Should().BeNull("only two replies were produced");
    }

    [Fact]
    public async Task EmptyInput_CompletesImmediately()
    {
        using var input = new MemoryStream();
        using var output = new MemoryStream();

        await CreateHost().RunAsync(input, output);

        output.Length.Should().Be(0);
    }

    [Fact]
    public async Task Cancellation_StopsTheLoop()
    {
        using var input = new MemoryStream();
        await NativeMessageCodec.WriteAsync(input, "{\"type\":\"ping\"}");
        input.Position = 0;
        using var output = new MemoryStream();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Already-cancelled: the loop should not process anything.
        await CreateHost().RunAsync(input, output, cts.Token);
        output.Length.Should().Be(0);
    }
}
