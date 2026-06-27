using System.Text.Json;

namespace JustDownload.Core.NativeMessaging;

/// <summary>
/// The default <see cref="INativeMessageHandler"/> (TASK-064): replies to a <c>{"type":"ping"}</c>
/// health check with <c>{"type":"pong"}</c> and rejects anything else with a structured error. It uses
/// <see cref="JsonDocument"/> (no reflection-based (de)serialization, trimming-safe). Real message types
/// — send-link, detected-media — are layered on in later extension tasks (TASK-067 etc.).
/// </summary>
internal sealed class PingNativeMessageHandler : INativeMessageHandler
{
    public Task<string?> HandleAsync(string requestJson, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestJson);

        string? type = TryReadType(requestJson);
        string response = type switch
        {
            "ping" => "{\"type\":\"pong\"}",
            null => "{\"type\":\"error\",\"error\":\"malformed message\"}",
            _ => "{\"type\":\"error\",\"error\":\"unsupported message type\"}",
        };

        return Task.FromResult<string?>(response);
    }

    private static string? TryReadType(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("type", out JsonElement typeElement) &&
                   typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString()
                : string.Empty; // valid JSON but no usable "type" → unsupported, not malformed
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
