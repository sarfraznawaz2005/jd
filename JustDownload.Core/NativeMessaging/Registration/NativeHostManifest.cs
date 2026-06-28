using System.Text.Json;
using System.Text.Json.Nodes;

namespace JustDownload.Core.NativeMessaging.Registration;

/// <summary>
/// Builds the native-messaging host manifest JSON (TASK-065, per the Chrome/Edge/Firefox spec). Chromium
/// browsers use <c>allowed_origins</c> (extension origins); Firefox uses <c>allowed_extensions</c> (gecko
/// ids). Both carry the host <c>name</c>, <c>description</c>, the launched <c>path</c>, and <c>type:"stdio"</c>.
/// Pure and deterministic so the exact output is unit-testable.
/// </summary>
public static class NativeHostManifest
{
    // Copy from Default so a type-info resolver is present (required by ToJsonString with custom options).
    private static readonly JsonSerializerOptions WriteOptions =
        new(JsonSerializerOptions.Default) { WriteIndented = true };

    /// <summary>Builds the manifest JSON for <paramref name="browser"/> from <paramref name="registration"/>.</summary>
    public static string Build(NativeMessagingBrowser browser, NativeHostRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        var manifest = new JsonObject
        {
            ["name"] = registration.Name,
            ["description"] = registration.Description,
            ["path"] = registration.ExecutablePath,
            ["type"] = "stdio",
        };

        if (browser == NativeMessagingBrowser.Firefox)
        {
            manifest["allowed_extensions"] = ToArray(registration.AllowedExtensionIds);
        }
        else
        {
            manifest["allowed_origins"] = ToArray(registration.AllowedOrigins);
        }

        return manifest.ToJsonString(WriteOptions);
    }

    private static JsonArray ToArray(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (string value in values)
        {
            array.Add(value);
        }

        return array;
    }
}
