using System.Text.Json.Serialization;

namespace JustDownload.Core.NativeMessaging.Registration;

/// <summary>
/// Source-generated, human-readable (indented) JSON for the native-messaging host manifest. Trim/AOT-safe so
/// the engine publishes cleanly under trimming (TASK-075); browsers read the file, so it stays indented.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(NativeHostManifestDto))]
internal sealed partial class HostManifestJsonContext : JsonSerializerContext;

/// <summary>
/// The native-messaging host manifest shape (per the Chrome/Edge/Firefox spec). Exactly one of
/// <see cref="AllowedOrigins"/> (Chromium) / <see cref="AllowedExtensions"/> (Firefox) is set; the other is
/// null and omitted from the output.
/// </summary>
internal sealed record NativeHostManifestDto
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; } = "stdio";

    [JsonPropertyName("allowed_origins")]
    public IReadOnlyList<string>? AllowedOrigins { get; init; }

    [JsonPropertyName("allowed_extensions")]
    public IReadOnlyList<string>? AllowedExtensions { get; init; }
}
