using FluentAssertions;
using JustDownload.App.Services;
using JustDownload.Core.Settings;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>
/// The opt-in clipboard watcher (TASK-133): a newly-copied supported URL is offered exactly once, while
/// non-URLs, repeats, the app's own copies, and empty clipboards are ignored. Driven via PollOnceAsync so the
/// timer and platform clipboard are not needed.
/// </summary>
public sealed class ClipboardMonitorTests
{
    private static (ClipboardMonitor Monitor, IClipboardService Clipboard, List<string> Detected) Build(
        string? clipboardText, string? lastCopied = null)
    {
        var clipboard = Substitute.For<IClipboardService>();
        clipboard.GetTextAsync().Returns(Task.FromResult(clipboardText));
        clipboard.LastCopiedText.Returns(lastCopied);

        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings());

        var monitor = new ClipboardMonitor(clipboard, settings);
        var detected = new List<string>();
        monitor.UrlDetected += (_, url) => detected.Add(url);
        return (monitor, clipboard, detected);
    }

    [Fact]
    public async Task PollOnce_NewSupportedUrl_OffersIt()
    {
        (ClipboardMonitor monitor, _, List<string> detected) = Build("https://example.com/file.zip");

        bool raised = await monitor.PollOnceAsync();

        raised.Should().BeTrue();
        detected.Should().ContainSingle().Which.Should().Be("https://example.com/file.zip");
    }

    [Fact]
    public async Task PollOnce_NonUrlText_IsIgnored()
    {
        (ClipboardMonitor monitor, _, List<string> detected) = Build("just some copied prose, not a link");

        (await monitor.PollOnceAsync()).Should().BeFalse();
        detected.Should().BeEmpty();
    }

    [Fact]
    public async Task PollOnce_EmptyClipboard_IsIgnored()
    {
        (ClipboardMonitor monitor, _, List<string> detected) = Build(null);

        (await monitor.PollOnceAsync()).Should().BeFalse();
        detected.Should().BeEmpty();
    }

    [Fact]
    public async Task PollOnce_SameUrlTwice_OffersOnlyOnce()
    {
        (ClipboardMonitor monitor, _, List<string> detected) = Build("https://example.com/file.zip");

        (await monitor.PollOnceAsync()).Should().BeTrue();
        (await monitor.PollOnceAsync()).Should().BeFalse("the same clipboard text is not offered again");
        detected.Should().ContainSingle();
    }

    [Fact]
    public async Task PollOnce_AppsOwnCopy_IsIgnored()
    {
        // The app just copied this URL (e.g. the "copy link" action), so it must not be offered back.
        (ClipboardMonitor monitor, _, List<string> detected) =
            Build("https://example.com/file.zip", lastCopied: "https://example.com/file.zip");

        (await monitor.PollOnceAsync()).Should().BeFalse();
        detected.Should().BeEmpty();
    }
}
