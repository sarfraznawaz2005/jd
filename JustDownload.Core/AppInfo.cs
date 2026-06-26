namespace JustDownload.Core;

/// <summary>
/// Compile-time metadata about the JustDownload product, shared by every front-end
/// (App, NativeHost, future CLI) so the product name/identity has a single source.
/// </summary>
public static class AppInfo
{
    /// <summary>The user-facing product name.</summary>
    public const string Name = "JustDownload";
}
