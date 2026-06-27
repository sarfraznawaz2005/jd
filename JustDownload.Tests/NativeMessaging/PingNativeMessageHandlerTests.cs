using FluentAssertions;
using JustDownload.Core.NativeMessaging;
using Xunit;

namespace JustDownload.Tests.NativeMessaging;

/// <summary>Tests for the default ping handler (TASK-064).</summary>
public sealed class PingNativeMessageHandlerTests
{
    private readonly PingNativeMessageHandler _handler = new();

    [Fact]
    public async Task Ping_RepliesPong()
    {
        (await _handler.HandleAsync("{\"type\":\"ping\"}")).Should().Be("{\"type\":\"pong\"}");
    }

    [Fact]
    public async Task UnknownType_RepliesUnsupportedError()
    {
        string? reply = await _handler.HandleAsync("{\"type\":\"frobnicate\"}");
        reply.Should().Contain("error").And.Contain("unsupported");
    }

    [Fact]
    public async Task MalformedJson_RepliesMalformedError()
    {
        string? reply = await _handler.HandleAsync("{not valid json");
        reply.Should().Contain("malformed");
    }

    [Fact]
    public async Task NoTypeProperty_RepliesUnsupported()
    {
        string? reply = await _handler.HandleAsync("{\"foo\":1}");
        reply.Should().Contain("unsupported");
    }
}
