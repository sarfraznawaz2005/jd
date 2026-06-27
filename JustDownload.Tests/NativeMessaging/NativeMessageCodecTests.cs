using System.Text;
using FluentAssertions;
using JustDownload.Core.NativeMessaging;
using Xunit;

namespace JustDownload.Tests.NativeMessaging;

/// <summary>Tests for the length-prefixed native-messaging wire format (TASK-064 AC0).</summary>
public sealed class NativeMessageCodecTests
{
    [Fact]
    public async Task WriteThenRead_RoundTripsTheMessage()
    {
        const string message = "{\"type\":\"download\",\"url\":\"https://example.com/x\"}";
        using var stream = new MemoryStream();

        await NativeMessageCodec.WriteAsync(stream, message);
        stream.Position = 0;
        string? read = await NativeMessageCodec.ReadAsync(stream);

        read.Should().Be(message);
    }

    [Fact]
    public async Task Read_ReturnsNull_OnCleanEndOfStream()
    {
        using var stream = new MemoryStream();
        (await NativeMessageCodec.ReadAsync(stream)).Should().BeNull();
    }

    [Fact]
    public async Task Read_Throws_OnOversizedMessage()
    {
        // 4-byte little-endian length of 2,000,000 with a tiny limit.
        using var stream = new MemoryStream([0x80, 0x84, 0x1e, 0x00]);
        stream.Position = 0;

        Func<Task> act = async () => await NativeMessageCodec.ReadAsync(stream, maxBytes: 1024);
        await act.Should().ThrowAsync<NativeMessagingException>();
    }

    [Fact]
    public async Task Read_Throws_OnTruncatedPayload()
    {
        // Header claims 10 bytes but only 3 follow.
        var bytes = new List<byte> { 10, 0, 0, 0 };
        bytes.AddRange(Encoding.UTF8.GetBytes("abc"));
        using var stream = new MemoryStream([.. bytes]);
        stream.Position = 0;

        Func<Task> act = async () => await NativeMessageCodec.ReadAsync(stream);
        await act.Should().ThrowAsync<NativeMessagingException>();
    }

    [Fact]
    public async Task EmptyPayload_RoundTripsAsEmptyString()
    {
        using var stream = new MemoryStream();
        await NativeMessageCodec.WriteAsync(stream, string.Empty);
        stream.Position = 0;
        (await NativeMessageCodec.ReadAsync(stream)).Should().Be(string.Empty);
    }
}
