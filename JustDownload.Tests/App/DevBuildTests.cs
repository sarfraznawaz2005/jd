using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FluentAssertions;
using JustDownload.App;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>
/// A dev instance must be obvious on screen (TASK-264), and a stable build must be left alone — the whole
/// point is telling the two apart, so a suffix that leaked into a release build would be worse than none.
/// </summary>
public sealed class DevBuildTests
{
    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("yes")]
    public void EnvironmentFlag_MarksAReleaseBuildAsDev(string flag)
    {
        // run.ps1 sets the variable, so even `run.ps1 -Release` is labelled.
        DevBuild.Resolve(flag, isDebugBuild: false).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("FALSE")]
    public void StableBuild_IsNotDev(string? flag)
    {
        DevBuild.Resolve(flag, isDebugBuild: false).Should().BeFalse();
    }

    [Fact]
    public void DebugBuild_IsAlwaysDev_EvenWithoutTheFlag()
    {
        DevBuild.Resolve(null, isDebugBuild: true).Should().BeTrue();
    }

    [Fact]
    public void Decorate_AppendsTheSuffix_OnADevBuild()
    {
        DevBuild.Decorate("New download", isDev: true).Should().Be("New download - DEV");
    }

    [Fact]
    public void Decorate_LeavesAStableBuildAlone()
    {
        DevBuild.Decorate("New download", isDev: false).Should().Be("New download");
    }

    [Fact]
    public void Decorate_DoesNotStack_WhenTheTitleIsSetTwice()
    {
        // Titles can be re-set (a binding updating, a window re-shown), and " - DEV - DEV" is nonsense.
        string? once = DevBuild.Decorate("JustDownload", isDev: true);
        DevBuild.Decorate(once, isDev: true).Should().Be("JustDownload - DEV");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Decorate_LeavesAnEmptyTitleAlone(string? title)
    {
        // A bare " - DEV" with no name reads as a broken window, not a labelled one.
        DevBuild.Decorate(title, isDev: true).Should().Be(title);
    }

    [AvaloniaFact]
    public void RegisteredLabeller_RenamesAnyWindowThatOpens()
    {
        // The wiring, not just the string helper: one registration has to catch every window the app shows,
        // which is the whole reason it is a class handler rather than a line in each window's code-behind.
        using IDisposable registration = DevBuild.RegisterWindowLabeller();
        var window = new Window { Title = "JustDownload" };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.Title.Should().Be("JustDownload - DEV");
        window.Close();
    }

    private sealed class DerivedWindow : Window;

    [AvaloniaFact]
    public void RegisteredLabeller_AlsoCatchesDerivedWindowTypes()
    {
        // Every real window in the app is a subclass (MainWindow, NewDownloadWindow, ...), so matching only
        // the exact Window type would label nothing that ships.
        using IDisposable registration = DevBuild.RegisterWindowLabeller();
        var window = new DerivedWindow { Title = "New download" };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.Title.Should().Be("New download - DEV");
        window.Close();
    }
}
