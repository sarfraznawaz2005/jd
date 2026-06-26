namespace JustDownload.Core.Abstractions;

/// <summary>Default <see cref="IAppInfoProvider"/> sourced from the compile-time <see cref="AppInfo"/>.</summary>
internal sealed class AppInfoProvider : IAppInfoProvider
{
    public string Name => AppInfo.Name;
}
