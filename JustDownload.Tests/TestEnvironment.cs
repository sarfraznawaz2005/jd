using System.Runtime.CompilerServices;
using JustDownload.Core;

namespace JustDownload.Tests;

/// <summary>
/// Process-wide test isolation: redirects <see cref="AppDataPaths.OverrideEnvironmentVariable"/> to a temp
/// directory before any test runs, so tests that build the real DI graph (<c>AddJustDownloadCore</c>, which
/// always registers <c>ErrorLogFileProvider</c>) never write into the real user's
/// <c>%APPDATA%\JustDownload\errors.log</c> — including exceptions raised process-wide via
/// <see cref="AppDomain.UnhandledException"/>/<see cref="TaskScheduler.UnobservedTaskException"/> while a test
/// happens to have a handler installed. Tests that deliberately exercise the no-override fallback (see
/// <c>PortableModeTests</c>) clear/restore this for their own scope.
/// </summary>
internal static class TestEnvironment
{
    [ModuleInitializer]
    public static void Initialize()
    {
        string dir = Path.Combine(Path.GetTempPath(), "JustDownloadTests-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(AppDataPaths.OverrideEnvironmentVariable, dir);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        };
    }
}
