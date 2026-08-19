using Avalonia.Controls;

namespace JustDownload.App;

/// <summary>
/// Marks a non-stable build so it is obvious at a glance which copy of the app is on screen (TASK-264):
/// every window title gets <see cref="TitleSuffix"/> appended. A build counts as "dev" when it is a Debug
/// build, or when <see cref="EnvironmentVariable"/> is set — which <c>run.ps1</c> does, so even a Release
/// build launched from the dev script is labelled. A published stable build has neither and is untouched.
/// </summary>
public static class DevBuild
{
    /// <summary>Appended to every window title on a dev build.</summary>
    public const string TitleSuffix = " - DEV";

    /// <summary>Set by <c>run.ps1</c> to mark the launch as a dev run regardless of build configuration.</summary>
    public const string EnvironmentVariable = "JUSTDOWNLOAD_DEV";

#if DEBUG
    private const bool IsDebugBuild = true;
#else
    private const bool IsDebugBuild = false;
#endif

    /// <summary>Whether this process is a dev build and should label its windows.</summary>
    public static bool IsDev { get; } =
        Resolve(Environment.GetEnvironmentVariable(EnvironmentVariable), IsDebugBuild);

    /// <summary>
    /// The dev verdict for a given environment flag and build configuration. Split out from
    /// <see cref="IsDev"/> so both branches are testable from a single test run.
    /// </summary>
    public static bool Resolve(string? environmentFlag, bool isDebugBuild) =>
        isDebugBuild || IsTruthy(environmentFlag);

    /// <summary>
    /// <paramref name="title"/> with the dev suffix, or unchanged on a stable build or when it already
    /// carries the suffix (a title can be set more than once — bindings, re-shows — and must not stack).
    /// </summary>
    public static string? Decorate(string? title, bool isDev)
    {
        if (!isDev || string.IsNullOrEmpty(title) || title.EndsWith(TitleSuffix, StringComparison.Ordinal))
        {
            return title;
        }

        return title + TitleSuffix;
    }

    /// <summary>
    /// Labels every window the app opens, now and later, via a class handler on the shared
    /// <see cref="Control.LoadedEvent"/> — one registration instead of a line in each window's code-behind,
    /// so a window added later is labelled without anyone remembering to opt it in. Called once at startup;
    /// a no-op on a stable build.
    /// </summary>
    public static void LabelAllWindows()
    {
        if (IsDev)
        {
            RegisterWindowLabeller();
        }
    }

    /// <summary>
    /// Registers the labelling class handler unconditionally and returns its registration, so a test can
    /// exercise the real wiring and then remove it — a class handler is process-global and would otherwise
    /// leak into every later headless test.
    /// </summary>
    public static IDisposable RegisterWindowLabeller() =>
        Control.LoadedEvent.AddClassHandler<Window>((window, _) =>
            window.Title = Decorate(window.Title, isDev: true));

    private static bool IsTruthy(string? value) =>
        value is { Length: > 0 }
        && !string.Equals(value, "0", StringComparison.Ordinal)
        && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
}
