using FluentAssertions;
using JustDownload.Core.NativeMessaging;
using Xunit;

namespace JustDownload.Tests.NativeMessaging;

/// <summary>
/// Real observed extension contact (TASK-175): the native host records a timestamp per browser family
/// whenever a browser actually launches it with a valid, allow-listed origin — independent of whether the
/// host manifest file merely exists (that gets written on every app startup regardless).
/// </summary>
public sealed class ExtensionContactTrackerTests : IDisposable
{
    private readonly string _file =
        Path.Combine(Path.GetTempPath(), "jd-contacts-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void GetLastContact_ReturnsNull_WhenNeverRecorded()
    {
        var tracker = new ExtensionContactTracker(_file);

        tracker.GetLastContact(ExtensionContactOrigin.Chromium).Should().BeNull();
        tracker.GetLastContact(ExtensionContactOrigin.Firefox).Should().BeNull();
    }

    [Fact]
    public async Task RecordContactAsync_PersistsPerOrigin_VisibleFromAFreshInstance()
    {
        // The host process (short-lived) records contact...
        var hostTracker = new ExtensionContactTracker(_file);
        await hostTracker.RecordContactAsync(ExtensionContactOrigin.Chromium);

        // ...and the app process (a fresh instance over the same file) reads it back (cross-process state,
        // same pattern as ExtensionInbox).
        var appTracker = new ExtensionContactTracker(_file);
        appTracker.GetLastContact(ExtensionContactOrigin.Chromium).Should().NotBeNull()
            .And.Subject.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        appTracker.GetLastContact(ExtensionContactOrigin.Firefox).Should().BeNull("only Chromium contacted it");
    }

    [Fact]
    public async Task RecordContactAsync_TracksBothOrigins_Independently()
    {
        var tracker = new ExtensionContactTracker(_file);

        await tracker.RecordContactAsync(ExtensionContactOrigin.Firefox);
        tracker.GetLastContact(ExtensionContactOrigin.Chromium).Should().BeNull();
        tracker.GetLastContact(ExtensionContactOrigin.Firefox).Should().NotBeNull();

        await tracker.RecordContactAsync(ExtensionContactOrigin.Chromium);
        tracker.GetLastContact(ExtensionContactOrigin.Chromium).Should().NotBeNull("recording one origin must not erase the other");
        tracker.GetLastContact(ExtensionContactOrigin.Firefox).Should().NotBeNull();
    }

    [Fact]
    public async Task RecordContactAsync_LaterCall_OverwritesTheTimestamp()
    {
        var tracker = new ExtensionContactTracker(_file);
        await tracker.RecordContactAsync(ExtensionContactOrigin.Chromium);
        DateTimeOffset first = tracker.GetLastContact(ExtensionContactOrigin.Chromium)!.Value;

        await Task.Delay(50);
        await tracker.RecordContactAsync(ExtensionContactOrigin.Chromium);

        tracker.GetLastContact(ExtensionContactOrigin.Chromium)!.Value.Should().BeAfter(first);
    }

    [Fact]
    public void GetLastContact_ToleratesACorruptFile_ByTreatingItAsNeverContacted()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, "{ not valid json");

        var tracker = new ExtensionContactTracker(_file);

        tracker.GetLastContact(ExtensionContactOrigin.Chromium).Should().BeNull();
    }

    public void Dispose()
    {
        if (File.Exists(_file))
        {
            File.Delete(_file);
        }
    }
}
