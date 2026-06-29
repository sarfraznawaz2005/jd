using System.Text.Json;

namespace JustDownload.Core.NativeMessaging.Registration;

/// <summary>
/// Builds the native-messaging host manifest JSON (TASK-065, per the Chrome/Edge/Firefox spec). Chromium
/// browsers use <c>allowed_origins</c> (extension origins); Firefox uses <c>allowed_extensions</c> (gecko
/// ids). Both carry the host <c>name</c>, <c>description</c>, the launched <c>path</c>, and <c>type:"stdio"</c>.
/// Pure and deterministic so the exact output is unit-testable.
/// </summary>
public static class NativeHostManifest
{
    /// <summary>Builds the manifest JSON for <paramref name="browser"/> from <paramref name="registration"/>.</summary>
    public static string Build(NativeMessagingBrowser browser, NativeHostRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        bool firefox = browser == NativeMessagingBrowser.Firefox;
        var manifest = new NativeHostManifestDto
        {
            Name = registration.Name,
            Description = registration.Description,
            Path = registration.ExecutablePath,
            AllowedOrigins = firefox ? null : registration.AllowedOrigins,
            AllowedExtensions = firefox ? registration.AllowedExtensionIds : null,
        };

        return JsonSerializer.Serialize(manifest, HostManifestJsonContext.Default.NativeHostManifestDto);
    }
}
