namespace JustDownload.Core.NativeMessaging;

/// <summary>
/// The browser families the native host can actually distinguish a connecting extension as (TASK-175).
/// Chrome and
/// Edge share one Chromium extension id (<see cref="NativeHostIdentity.ChromiumExtensionId"/>), so a
/// launch argument alone can't tell them apart — both surface as <see cref="Chromium"/>. Firefox has its
/// own id and is tracked separately.
/// </summary>
public enum ExtensionContactOrigin
{
    Chromium,
    Firefox,
}
