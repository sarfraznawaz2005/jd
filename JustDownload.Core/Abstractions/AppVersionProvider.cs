using System.Reflection;

namespace JustDownload.Core.Abstractions;

/// <summary>Default <see cref="IAppVersionProvider"/>, sourced from this assembly's informational version.</summary>
internal sealed class AppVersionProvider : IAppVersionProvider
{
    public string CurrentVersion { get; } = Resolve();

    private static string Resolve()
    {
        Assembly assembly = typeof(AppVersionProvider).Assembly;
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // Strip any source-control metadata suffix (e.g. "1.0.0+abc123").
            int plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus >= 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString() ?? "0.0.0";
    }
}
