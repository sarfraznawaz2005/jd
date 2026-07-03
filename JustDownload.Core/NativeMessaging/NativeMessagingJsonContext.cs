using System.Text.Json.Serialization;

namespace JustDownload.Core.NativeMessaging;

/// <summary>
/// Source-generated JSON metadata for native-messaging (de)serialization. Using a
/// <see cref="JsonSerializerContext"/> instead of reflection-based <c>System.Text.Json</c> keeps the engine
/// trim- and AOT-safe (TASK-075): the serializer never has to discover types at runtime, so trimming can't
/// strip a member it didn't see referenced.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(List<PendingLink>))]
[JsonSerializable(typeof(ExtensionSettingsDto))]
[JsonSerializable(typeof(ExtensionContactRecord))]
internal sealed partial class NativeMessagingJsonContext : JsonSerializerContext;

/// <summary>
/// The <c>get_settings</c> reply the host sends back to the extension popup (TASK-071). A fixed-shape DTO so
/// it serializes through the source-generated context above rather than a reflection-built
/// <c>JsonObject</c>.
/// </summary>
internal sealed record ExtensionSettingsDto
{
    [JsonPropertyName("type")]
    public string Type { get; } = "settings";

    [JsonPropertyName("defaultVideoQuality")]
    public required int DefaultVideoQuality { get; init; }

    [JsonPropertyName("defaultContainer")]
    public required string DefaultContainer { get; init; }

    [JsonPropertyName("maxConcurrentDownloads")]
    public required int MaxConcurrentDownloads { get; init; }
}
