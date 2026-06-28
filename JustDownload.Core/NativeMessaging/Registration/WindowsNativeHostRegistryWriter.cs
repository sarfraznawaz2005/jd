using System.Runtime.Versioning;
using Microsoft.Win32;

namespace JustDownload.Core.NativeMessaging.Registration;

/// <summary>
/// Windows <see cref="INativeHostRegistryWriter"/> (TASK-065). Registers the host under the per-user
/// <c>HKCU</c> native-messaging key for each browser, whose default value is the manifest file path —
/// the location Chrome/Edge/Firefox read on Windows. Per-user (HKCU) so no elevation is required.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsNativeHostRegistryWriter : INativeHostRegistryWriter
{
    public void SetHostPath(NativeMessagingBrowser browser, string hostName, string manifestPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(hostName);
        ArgumentException.ThrowIfNullOrEmpty(manifestPath);

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath(browser, hostName));
        key.SetValue(null, manifestPath, RegistryValueKind.String);
    }

    public void Remove(NativeMessagingBrowser browser, string hostName)
    {
        ArgumentException.ThrowIfNullOrEmpty(hostName);
        Registry.CurrentUser.DeleteSubKeyTree(KeyPath(browser, hostName), throwOnMissingSubKey: false);
    }

    private static string KeyPath(NativeMessagingBrowser browser, string hostName)
    {
        string vendor = browser switch
        {
            NativeMessagingBrowser.Chrome => @"Software\Google\Chrome",
            NativeMessagingBrowser.Edge => @"Software\Microsoft\Edge",
            NativeMessagingBrowser.Firefox => @"Software\Mozilla",
            _ => throw new ArgumentOutOfRangeException(nameof(browser)),
        };

        return $@"{vendor}\NativeMessagingHosts\{hostName}";
    }
}
