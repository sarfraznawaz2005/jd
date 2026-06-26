namespace JustDownload.Core.Abstractions;

/// <summary>
/// Exposes product identity to front-ends through an injectable seam, so consumers depend on an
/// interface (mockable in tests) rather than the static <see cref="AppInfo"/> type directly.
/// </summary>
public interface IAppInfoProvider
{
    /// <summary>The user-facing product name.</summary>
    string Name { get; }
}
