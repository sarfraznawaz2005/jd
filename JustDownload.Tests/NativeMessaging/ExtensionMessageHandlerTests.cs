using System.Collections.Concurrent;
using FluentAssertions;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.NativeMessaging;
using JustDownload.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JustDownload.Tests.NativeMessaging;

/// <summary>
/// The extension message router (TASK-067/069): ping/pong, and the blacklist sync that reconciles the
/// per-site blacklist into the app's <c>site_blacklist</c> (US-12) — adding new domains and removing
/// button-scoped ones the extension dropped, while leaving other scopes alone.
/// </summary>
public sealed class ExtensionMessageHandlerTests
{
    private sealed class InMemoryBlacklist : IBlacklistRepository
    {
        public ConcurrentDictionary<(string, string), BlacklistEntry> Entries { get; } = new();

        public Task AddAsync(BlacklistEntry entry, CancellationToken ct = default)
        {
            Entries[(entry.Domain, entry.Scope)] = entry;
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string domain, string scope, CancellationToken ct = default) =>
            Task.FromResult(Entries.ContainsKey((domain, scope)));

        public Task<IReadOnlyList<BlacklistEntry>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BlacklistEntry>>(Entries.Values.ToArray());

        public Task<bool> DeleteAsync(string domain, string scope, CancellationToken ct = default) =>
            Task.FromResult(Entries.TryRemove((domain, scope), out _));
    }

    private sealed class FakeInbox : IExtensionInbox
    {
        public List<PendingLink> Links { get; } = [];

        public Task EnqueueAsync(PendingLink link, CancellationToken ct = default)
        {
            Links.Add(link);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PendingLink>> DrainAsync(CancellationToken ct = default)
        {
            IReadOnlyList<PendingLink> copy = Links.ToArray();
            Links.Clear();
            return Task.FromResult(copy);
        }
    }

    private sealed class CountingLauncher : IAppLauncher
    {
        public int Calls { get; private set; }

        public Task EnsureRunningAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private static ExtensionMessageHandler Build(
        IBlacklistRepository repo,
        IExtensionInbox? inbox = null,
        IAppLauncher? launcher = null,
        ILogger<ExtensionMessageHandler>? logger = null) =>
        new(repo, inbox ?? new FakeInbox(), launcher ?? new CountingLauncher(), new StubSettings(),
            logger ?? NullLogger<ExtensionMessageHandler>.Instance);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed class StubSettings : ISettingsService
    {
        public AppSettings Current { get; private set; } = new() { DefaultVideoQuality = VideoQuality.P720 };

#pragma warning disable CS0067
        public event EventHandler<SettingsChangedEventArgs>? Changed;
#pragma warning restore CS0067

        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken ct = default)
        {
            Current = mutate(Current);
            return Task.FromResult(Current);
        }
    }

    [Fact]
    public async Task Ping_RepliesPong()
    {
        string? reply = await Build(new InMemoryBlacklist()).HandleAsync("{\"type\":\"ping\"}");

        reply.Should().Contain("pong");
    }

    [Fact]
    public async Task Malformed_RepliesError()
    {
        string? reply = await Build(new InMemoryBlacklist()).HandleAsync("not json");

        reply.Should().Contain("error");
    }

    [Fact]
    public async Task BlacklistSync_AddsDomains_AndRemovesStaleButtonScoped_LeavesOtherScopes()
    {
        var repo = new InMemoryBlacklist();
        // Pre-existing: a stale button-scoped entry to be removed, and an app-scoped one to be kept.
        await repo.AddAsync(new BlacklistEntry { Domain = "stale.com", Scope = ExtensionMessageHandler.ButtonScope });
        await repo.AddAsync(new BlacklistEntry { Domain = "keep.com", Scope = "app" });

        await Build(repo).HandleAsync("{\"type\":\"BLACKLIST_SYNC\",\"domains\":[\"a.com\",\"b.com\"]}");

        var buttonDomains = repo.Entries.Values
            .Where(e => e.Scope == ExtensionMessageHandler.ButtonScope)
            .Select(e => e.Domain)
            .OrderBy(d => d)
            .ToArray();
        buttonDomains.Should().Equal("a.com", "b.com");
        (await repo.ExistsAsync("stale.com", ExtensionMessageHandler.ButtonScope)).Should().BeFalse("dropped domain removed");
        (await repo.ExistsAsync("keep.com", "app")).Should().BeTrue("other scopes are untouched");
    }

    [Fact]
    public async Task GetSettings_ReturnsAppSettings()
    {
        string? reply = await Build(new InMemoryBlacklist()).HandleAsync("{\"type\":\"get_settings\"}");

        reply.Should().Contain("settings");
        reply.Should().Contain("720", "the app's default quality is surfaced to the popup");
    }

    [Fact]
    public async Task DownloadLink_QueuesLink_AndEnsuresAppRunning()
    {
        var inbox = new FakeInbox();
        var launcher = new CountingLauncher();
        ExtensionMessageHandler handler = Build(new InMemoryBlacklist(), inbox, launcher);

        string? reply = await handler.HandleAsync(
            "{\"type\":\"DOWNLOAD_LINK\",\"url\":\"https://x/a.zip\",\"referrer\":\"https://x/p\",\"cookies\":\"sid=1\"}");

        reply.Should().Contain("ok");
        inbox.Links.Should().ContainSingle();
        inbox.Links[0].Url.Should().Be("https://x/a.zip");
        inbox.Links[0].Referrer.Should().Be("https://x/p");
        inbox.Links[0].Cookies.Should().Be("sid=1");
        launcher.Calls.Should().Be(1, "the app is launched/ensured running for the handed-off link");
    }

    [Fact]
    public async Task DownloadLink_LogsHostOnly_NeverTheSignedQueryString()
    {
        // TASK-099, §5: a handed-off media URL often carries signed tokens in its query string; they must
        // never reach the host log.
        var logger = new RecordingLogger<ExtensionMessageHandler>();
        ExtensionMessageHandler handler = Build(new InMemoryBlacklist(), logger: logger);
        const string signed =
            "https://cdn.example.com/video.mp4?token=SUPERSECRET123&X-Amz-Signature=deadbeef&Expires=99";

        await handler.HandleAsync($"{{\"type\":\"download_link\",\"url\":\"{signed}\"}}");

        string log = string.Join("\n", logger.Messages);
        log.Should().Contain("cdn.example.com", "the source host is still logged for diagnostics");
        log.Should().NotContain("SUPERSECRET123");
        log.Should().NotContain("X-Amz-Signature");
        log.Should().NotContain("token=");
        log.Should().NotContain("?", "the query string is dropped entirely");
    }
}
