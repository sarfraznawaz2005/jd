using System.Buffers.Binary;
using System.Text;

namespace JustDownload.Core.NativeMessaging;

/// <summary>
/// The browser Native Messaging wire format (TASK-064, D8): each message is a 32-bit length prefix in
/// the platform's native byte order (little-endian on all supported platforms) followed by that many
/// UTF-8 JSON bytes, exchanged over stdin/stdout. No socket is ever opened. Incoming messages are capped
/// (Chrome limits extension→host to 1 MiB) so a hostile or buggy peer cannot exhaust memory.
/// </summary>
public static class NativeMessageCodec
{
    /// <summary>The default maximum accepted incoming message size (Chrome's 1 MiB limit).</summary>
    public const int DefaultMaxMessageBytes = 1024 * 1024;

    /// <summary>
    /// Reads one framed message from <paramref name="input"/>, returning its UTF-8 JSON text, or
    /// <see langword="null"/> when the stream is cleanly closed (no more messages).
    /// </summary>
    /// <exception cref="NativeMessagingException">The frame is truncated or exceeds <paramref name="maxBytes"/>.</exception>
    public static async Task<string?> ReadAsync(
        Stream input,
        int maxBytes = DefaultMaxMessageBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var header = new byte[4];
        if (!await ReadExactAsync(input, header, allowEofAtStart: true, cancellationToken).ConfigureAwait(false))
        {
            return null; // clean EOF — peer closed the pipe
        }

        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0)
        {
            return string.Empty;
        }

        if (length > (uint)maxBytes)
        {
            throw new NativeMessagingException(
                $"Native message of {length} bytes exceeds the {maxBytes}-byte limit.");
        }

        var payload = new byte[length];
        await ReadExactAsync(input, payload, allowEofAtStart: false, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(payload);
    }

    /// <summary>Writes <paramref name="json"/> as one framed message to <paramref name="output"/>.</summary>
    public static async Task WriteAsync(Stream output, string json, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(json);

        byte[] payload = Encoding.UTF8.GetBytes(json);
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);

        await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> ReadExactAsync(
        Stream input, byte[] buffer, bool allowEofAtStart, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await input.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (offset == 0 && allowEofAtStart)
                {
                    return false; // clean EOF before any byte of this frame
                }

                throw new NativeMessagingException("Unexpected end of stream within a native message frame.");
            }

            offset += read;
        }

        return true;
    }
}
