using System.Collections.Concurrent;
using FluentAssertions;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.NativeMessaging;
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

    private static ExtensionMessageHandler Build(IBlacklistRepository repo) =>
        new(repo, NullLogger<ExtensionMessageHandler>.Instance);

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
    public async Task DownloadLink_IsAcknowledged()
    {
        string? reply = await Build(new InMemoryBlacklist())
            .HandleAsync("{\"type\":\"DOWNLOAD_LINK\",\"url\":\"https://x/a.zip\"}");

        reply.Should().Contain("ok");
    }
}
