namespace JustDownload.Core;

/// <summary>
/// An injectable view of whether the app is running in portable mode (TASK-138), so OS-integration code
/// (autostart, native-host registration) can skip registry/system writes and the UI can reflect it — without
/// each consumer calling the static <see cref="PortableMode"/> (which makes the portable branch testable).
/// </summary>
public interface IPortableEnvironment
{
    /// <summary>Whether the app is running self-contained from its own folder (no registry/system writes).</summary>
    bool IsPortable { get; }
}

internal sealed class PortableEnvironment : IPortableEnvironment
{
    public bool IsPortable { get; } = PortableMode.IsPortable();
}
