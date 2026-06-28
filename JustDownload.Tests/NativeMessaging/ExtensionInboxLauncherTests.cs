using FluentAssertions;
using JustDownload.Core.NativeMessaging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JustDownload.Tests.NativeMessaging;

/// <summary>
/// Launch-or-queue hand-off (TASK-070, US-11 AC5): the launcher starts the app only when it is not already
/// running (AC0), and the inbox persists links across processes so a queued link is delivered on the next
/// app start (AC1).
/// </summary>
public sealed class ExtensionInboxLauncherTests : IDisposable
{
    private readonly string _file =
        Path.Combine(Path.GetTempPath(), "jd-inbox-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public async Task Launcher_LaunchesOnlyWhenNotRunning()
    {
        int launched = 0;

        await new AppLauncher(() => false, () => launched++, NullLogger<AppLauncher>.Instance).EnsureRunningAsync();
        launched.Should().Be(1, "the app is launched when not running (AC0)");

        await new AppLauncher(() => true, () => launched++, NullLogger<AppLauncher>.Instance).EnsureRunningAsync();
        launched.Should().Be(1, "an already-running app is not launched again");
    }

    [Fact]
    public async Task Inbox_PersistsLinks_DeliveredOnNextStart()
    {
        // The host process enqueues while the app is closed.
        var hostInbox = new ExtensionInbox(_file);
        await hostInbox.EnqueueAsync(new PendingLink { Url = "https://x/a.zip", Referrer = "https://x/p" });
        await hostInbox.EnqueueAsync(new PendingLink { Url = "https://x/b.mp4", MediaKind = "video" });

        // The app starts later (a fresh instance over the same file) and drains the queue (AC1).
        var appInbox = new ExtensionInbox(_file);
        IReadOnlyList<PendingLink> delivered = await appInbox.DrainAsync();

        delivered.Select(l => l.Url).Should().Equal("https://x/a.zip", "https://x/b.mp4");
        delivered[0].Referrer.Should().Be("https://x/p");

        // Draining empties the inbox so links are delivered exactly once.
        (await new ExtensionInbox(_file).DrainAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Inbox_DrainEmpty_ReturnsNothing()
    {
        (await new ExtensionInbox(_file).DrainAsync()).Should().BeEmpty();
    }

    public void Dispose()
    {
        if (File.Exists(_file))
        {
            File.Delete(_file);
        }
    }
}
