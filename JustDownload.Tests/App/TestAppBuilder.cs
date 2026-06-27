using Avalonia;
using Avalonia.Headless;
using JustDownload.Tests.App;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace JustDownload.Tests.App;

/// <summary>
/// Configures the real <see cref="JustDownload.App.App"/> under the Avalonia headless platform so UI shell
/// tests (TASK-047) exercise the actual styles, design tokens and theme variants — not a stand-in.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<JustDownload.App.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .WithInterFont();
}
