namespace JustDownload.Core.Abstractions;

/// <summary>
/// Exposes the running app's product version through an injectable seam (mirrors <see cref="IAppInfoProvider"/>),
/// so version-dependent logic — currently the update checker (TASK-080) — can read it without an App→Core
/// dependency inversion. Every csproj in the solution shares one <c>&lt;Version&gt;</c> via
/// Directory.Build.props, so <c>JustDownload.Core</c>'s own assembly reports the same informational version
/// as <c>JustDownload.App</c>'s (see <c>MainWindowViewModel.ResolveVersion</c>, which resolves the same way
/// for the About flyout).
/// </summary>
public interface IAppVersionProvider
{
    /// <summary>The current product version, e.g. <c>"1.0.0"</c> (source-control metadata suffix stripped).</summary>
    string CurrentVersion { get; }
}
