namespace JustDownload.Core.NativeMessaging.Registration;

/// <summary>
/// Writes the per-browser registry entry that points a Chromium/Firefox browser at the host manifest
/// (TASK-065). Only Windows uses the registry; macOS and Linux discover the manifest purely by its file
/// location, so their implementation is a no-op. Abstracted so the registrar is unit-testable without
/// touching the real registry.
/// </summary>
public interface INativeHostRegistryWriter
{
    /// <summary>Registers <paramref name="manifestPath"/> for <paramref name="hostName"/> under <paramref name="browser"/>.</summary>
    void SetHostPath(NativeMessagingBrowser browser, string hostName, string manifestPath);

    /// <summary>Removes the registry entry for <paramref name="hostName"/> under <paramref name="browser"/>.</summary>
    void Remove(NativeMessagingBrowser browser, string hostName);
}

/// <summary>The no-op writer for platforms that locate the manifest by file path alone (macOS/Linux).</summary>
public sealed class NoOpNativeHostRegistryWriter : INativeHostRegistryWriter
{
    public void SetHostPath(NativeMessagingBrowser browser, string hostName, string manifestPath)
    {
    }

    public void Remove(NativeMessagingBrowser browser, string hostName)
    {
    }
}
