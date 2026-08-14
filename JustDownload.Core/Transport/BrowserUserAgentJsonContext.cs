using System.Text.Json.Serialization;

namespace JustDownload.Core.Transport;

/// <summary>
/// Source-generated JSON metadata for the browser-User-Agent cache file, matching the trim/AOT-safe pattern
/// used by <see cref="NativeMessaging.NativeMessagingJsonContext"/> for other small on-disk state.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(BrowserUserAgentCacheEntry))]
internal sealed partial class BrowserUserAgentJsonContext : JsonSerializerContext;
