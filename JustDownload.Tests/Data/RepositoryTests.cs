using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Data;
using JustDownload.Core.Data.Migrations;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.Data;

/// <summary>
/// Integration tests for the data-layer repositories (TASK-020) against a real temp SQLite database
/// migrated to the PRD §4.4 schema. Each repository is round-tripped create → read → update → delete.
/// The database lives in an isolated temp directory; connection pools are cleared on dispose so the
/// files can be removed cleanly (no orphaned handles).
/// </summary>
public sealed class RepositoryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ServiceProvider _provider;

    public RepositoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jd-repo-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var pathProvider = Substitute.For<IDatabasePathProvider>();
        pathProvider.DatabaseDirectory.Returns(_tempDir);
        pathProvider.DatabasePath.Returns(Path.Combine(_tempDir, "test.db"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(pathProvider);
        services.AddJustDownloadData();
        _provider = services.BuildServiceProvider();

        // Stand up the schema the repositories write against.
        _provider.GetRequiredService<IMigrationRunner>().Migrate();
    }

    private static Download SampleDownload() => new()
    {
        Url = "https://example.com/file.iso",
        Referrer = "https://example.com/page",
        Filename = "file.iso",
        Directory = "/downloads",
        TotalBytes = 1024,
        Status = "queued",
        CategoryType = "Program",
        CategoryStatus = "Incomplete",
        ETag = "\"abc123\"",
        LastModified = "Wed, 21 Oct 2026 07:28:00 GMT",
        CreatedAt = new DateTimeOffset(2026, 6, 26, 12, 0, 0, TimeSpan.Zero),
        CompletedAt = null,
        Error = null,
        MaxConnections = 8,
        SpeedLimit = null,
    };

    [Fact]
    public async Task DownloadRepository_RoundTrips_CreateReadUpdateDelete()
    {
        var repo = _provider.GetRequiredService<IDownloadRepository>();

        long id = await repo.AddAsync(SampleDownload());
        id.Should().BePositive();

        Download? read = await repo.GetAsync(id);
        read.Should().NotBeNull();
        read!.Url.Should().Be("https://example.com/file.iso");
        read.Status.Should().Be("queued");
        read.TotalBytes.Should().Be(1024);
        read.MaxConnections.Should().Be(8);
        read.CreatedAt.Should().Be(new DateTimeOffset(2026, 6, 26, 12, 0, 0, TimeSpan.Zero));
        read.CompletedAt.Should().BeNull();

        (await repo.GetAllAsync()).Should().ContainSingle(d => d.Id == id);

        Download updated = read with
        {
            Status = "complete",
            CompletedAt = new DateTimeOffset(2026, 6, 26, 12, 30, 0, TimeSpan.Zero),
            TotalBytes = 2048,
            Error = null,
        };
        (await repo.UpdateAsync(updated)).Should().BeTrue();

        Download? afterUpdate = await repo.GetAsync(id);
        afterUpdate!.Status.Should().Be("complete");
        afterUpdate.TotalBytes.Should().Be(2048);
        afterUpdate.CompletedAt.Should().Be(new DateTimeOffset(2026, 6, 26, 12, 30, 0, TimeSpan.Zero));

        (await repo.DeleteAsync(id)).Should().BeTrue();
        (await repo.GetAsync(id)).Should().BeNull();
        (await repo.DeleteAsync(id)).Should().BeFalse("the row is already gone");
    }

    [Fact]
    public async Task DownloadRepository_Update_OnMissingRow_ReturnsFalse()
    {
        var repo = _provider.GetRequiredService<IDownloadRepository>();

        bool updated = await repo.UpdateAsync(SampleDownload() with { Id = 999_999 });

        updated.Should().BeFalse();
    }

    [Fact]
    public async Task SegmentRepository_RoundTrips_CreateReadUpdateDelete()
    {
        var downloads = _provider.GetRequiredService<IDownloadRepository>();
        var segments = _provider.GetRequiredService<ISegmentRepository>();

        long downloadId = await downloads.AddAsync(SampleDownload());

        long segId = await segments.AddAsync(new DownloadSegment
        {
            DownloadId = downloadId,
            Index = 0,
            Start = 0,
            End = 511,
            Downloaded = 0,
            State = "pending",
        });
        segId.Should().BePositive();

        DownloadSegment? read = await segments.GetAsync(segId);
        read.Should().NotBeNull();
        read!.DownloadId.Should().Be(downloadId);
        read.Start.Should().Be(0);
        read.End.Should().Be(511);
        read.State.Should().Be("pending");

        (await segments.GetByDownloadAsync(downloadId)).Should().ContainSingle(s => s.Id == segId);

        (await segments.UpdateAsync(read with { Downloaded = 256, State = "active" })).Should().BeTrue();
        DownloadSegment? afterUpdate = await segments.GetAsync(segId);
        afterUpdate!.Downloaded.Should().Be(256);
        afterUpdate.State.Should().Be("active");

        (await segments.DeleteAsync(segId)).Should().BeTrue();
        (await segments.GetAsync(segId)).Should().BeNull();
        (await segments.DeleteAsync(segId)).Should().BeFalse();
    }

    [Fact]
    public async Task SegmentRepository_GetByDownload_OrdersByIndex()
    {
        var downloads = _provider.GetRequiredService<IDownloadRepository>();
        var segments = _provider.GetRequiredService<ISegmentRepository>();

        long downloadId = await downloads.AddAsync(SampleDownload());

        // Insert out of order to prove the ORDER BY "index".
        await segments.AddAsync(new DownloadSegment
        {
            DownloadId = downloadId,
            Index = 2,
            Start = 1024,
            End = 1535,
            Downloaded = 0,
            State = "pending",
        });
        await segments.AddAsync(new DownloadSegment
        {
            DownloadId = downloadId,
            Index = 0,
            Start = 0,
            End = 511,
            Downloaded = 0,
            State = "pending",
        });
        await segments.AddAsync(new DownloadSegment
        {
            DownloadId = downloadId,
            Index = 1,
            Start = 512,
            End = 1023,
            Downloaded = 0,
            State = "pending",
        });

        IReadOnlyList<DownloadSegment> ordered = await segments.GetByDownloadAsync(downloadId);

        ordered.Select(s => s.Index).Should().ContainInOrder(0, 1, 2);
    }

    [Fact]
    public async Task DownloadRepository_SetPrioritiesAsync_BatchUpdatesEach()
    {
        var repo = _provider.GetRequiredService<IDownloadRepository>();
        long a = await repo.AddAsync(SampleDownload());
        long b = await repo.AddAsync(SampleDownload());
        long c = await repo.AddAsync(SampleDownload());

        await repo.SetPrioritiesAsync(new[]
        {
            new DownloadPriority(a, 30),
            new DownloadPriority(b, 20),
            new DownloadPriority(c, 10),
        });

        (await repo.GetAsync(a))!.Priority.Should().Be(30);
        (await repo.GetAsync(b))!.Priority.Should().Be(20);
        (await repo.GetAsync(c))!.Priority.Should().Be(10);

        // An empty batch is a no-op (does not throw, changes nothing).
        await repo.SetPrioritiesAsync(Array.Empty<DownloadPriority>());
        (await repo.GetAsync(a))!.Priority.Should().Be(30);
    }

    [Fact]
    public async Task DownloadRepository_MarkAllAsync_MovesOnlyMatchingStatus()
    {
        var repo = _provider.GetRequiredService<IDownloadRepository>();
        long a1 = await repo.AddAsync(SampleDownload() with { Status = "active" });
        long a2 = await repo.AddAsync(SampleDownload() with { Status = "active" });
        long p = await repo.AddAsync(SampleDownload() with { Status = "paused" });

        int changed = await repo.MarkAllAsync("active", "paused");

        changed.Should().Be(2);
        (await repo.GetAsync(a1))!.Status.Should().Be("paused");
        (await repo.GetAsync(a2))!.Status.Should().Be("paused");
        (await repo.GetAsync(p))!.Status.Should().Be("paused", "it was already paused and untouched");
    }

    [Fact]
    public async Task SegmentRepository_DeleteByDownloadAsync_RemovesOnlyThatDownloadsSegments()
    {
        var downloads = _provider.GetRequiredService<IDownloadRepository>();
        var segments = _provider.GetRequiredService<ISegmentRepository>();
        long d1 = await downloads.AddAsync(SampleDownload());
        long d2 = await downloads.AddAsync(SampleDownload());

        for (int i = 0; i < 3; i++)
        {
            await segments.AddAsync(new DownloadSegment
            {
                DownloadId = d1,
                Index = i,
                Start = i * 512,
                End = (i * 512) + 511,
                Downloaded = 0,
                State = "pending",
            });
        }

        await segments.AddAsync(new DownloadSegment
        {
            DownloadId = d2,
            Index = 0,
            Start = 0,
            End = 511,
            Downloaded = 0,
            State = "pending",
        });

        int removed = await segments.DeleteByDownloadAsync(d1);

        removed.Should().Be(3);
        (await segments.GetByDownloadAsync(d1)).Should().BeEmpty();
        (await segments.GetByDownloadAsync(d2)).Should().ContainSingle("the other download's segments are untouched");
    }

    [Fact]
    public async Task SegmentRepository_ReplaceForDownloadAsync_SwapsTheWholeSetAtomically()
    {
        var downloads = _provider.GetRequiredService<IDownloadRepository>();
        var segments = _provider.GetRequiredService<ISegmentRepository>();
        long d1 = await downloads.AddAsync(SampleDownload());
        long d2 = await downloads.AddAsync(SampleDownload());

        // Pre-existing rows that the replace must wipe, plus an untouched neighbour download.
        await segments.AddAsync(new DownloadSegment
        {
            DownloadId = d1,
            Index = 0,
            Start = 0,
            End = 99,
            Downloaded = 100,
            State = "complete",
        });
        await segments.AddAsync(new DownloadSegment
        {
            DownloadId = d2,
            Index = 0,
            Start = 0,
            End = 511,
            Downloaded = 0,
            State = "pending",
        });

        var replacement = new[]
        {
            new DownloadSegment { DownloadId = d1, Index = 0, Start = 0, End = 511, Downloaded = 512, State = "complete" },
            new DownloadSegment { DownloadId = d1, Index = 1, Start = 512, End = 1023, Downloaded = 512, State = "complete" },
        };
        await segments.ReplaceForDownloadAsync(d1, replacement);

        IReadOnlyList<DownloadSegment> after = await segments.GetByDownloadAsync(d1);
        after.Select(s => s.Index).Should().ContainInOrder(0, 1);
        after.Select(s => s.End).Should().ContainInOrder(511, 1023);
        (await segments.GetByDownloadAsync(d2)).Should().ContainSingle("the other download's segments are untouched");

        // An empty replacement clears the checkpoint without throwing.
        await segments.ReplaceForDownloadAsync(d1, Array.Empty<DownloadSegment>());
        (await segments.GetByDownloadAsync(d1)).Should().BeEmpty();
    }

    [Fact]
    public async Task SettingsRepository_RoundTrips_CreateReadUpdateDelete()
    {
        var settings = _provider.GetRequiredService<ISettingsRepository>();

        await settings.SetAsync("theme", "dark");
        (await settings.GetAsync("theme")).Should().Be("dark");

        // SetAsync is an upsert: a second call on the same key updates rather than throwing.
        await settings.SetAsync("theme", "light");
        (await settings.GetAsync("theme")).Should().Be("light");

        await settings.SetAsync("max_connections", "8");
        IReadOnlyDictionary<string, string?> all = await settings.GetAllAsync();
        all.Should().ContainKey("theme").WhoseValue.Should().Be("light");
        all.Should().ContainKey("max_connections").WhoseValue.Should().Be("8");

        (await settings.DeleteAsync("theme")).Should().BeTrue();
        (await settings.GetAsync("theme")).Should().BeNull();
        (await settings.DeleteAsync("theme")).Should().BeFalse();
    }

    [Fact]
    public async Task SettingsRepository_SupportsNullValue()
    {
        var settings = _provider.GetRequiredService<ISettingsRepository>();

        await settings.SetAsync("flag", null);

        (await settings.GetAsync("flag")).Should().BeNull();
        (await settings.GetAllAsync()).Should().ContainKey("flag");
    }

    [Fact]
    public async Task BlacklistRepository_RoundTrips_CreateReadUpdateDelete()
    {
        var blacklist = _provider.GetRequiredService<IBlacklistRepository>();

        await blacklist.AddAsync(new BlacklistEntry { Domain = "ads.example.com", Scope = "button" });
        (await blacklist.ExistsAsync("ads.example.com", "button")).Should().BeTrue();

        // Idempotent re-add (the composite key has no separately-mutable field): no throw, no dup.
        await blacklist.AddAsync(new BlacklistEntry { Domain = "ads.example.com", Scope = "button" });

        await blacklist.AddAsync(new BlacklistEntry { Domain = "ads.example.com", Scope = "app" });
        IReadOnlyList<BlacklistEntry> all = await blacklist.GetAllAsync();
        all.Should().HaveCount(2);
        all.Should().ContainEquivalentOf(new BlacklistEntry { Domain = "ads.example.com", Scope = "button" });

        (await blacklist.DeleteAsync("ads.example.com", "button")).Should().BeTrue();
        (await blacklist.ExistsAsync("ads.example.com", "button")).Should().BeFalse();
        (await blacklist.ExistsAsync("ads.example.com", "app")).Should().BeTrue();
        (await blacklist.DeleteAsync("ads.example.com", "button")).Should().BeFalse();
    }

    [Fact]
    public async Task DeletingDownload_CascadesToSegments()
    {
        var downloads = _provider.GetRequiredService<IDownloadRepository>();
        var segments = _provider.GetRequiredService<ISegmentRepository>();

        long downloadId = await downloads.AddAsync(SampleDownload());
        await segments.AddAsync(new DownloadSegment
        {
            DownloadId = downloadId,
            Index = 0,
            Start = 0,
            End = 511,
            Downloaded = 0,
            State = "pending",
        });

        (await downloads.DeleteAsync(downloadId)).Should().BeTrue();

        (await segments.GetByDownloadAsync(downloadId)).Should().BeEmpty(
            "ON DELETE CASCADE should remove the download's segments");
    }

    [Fact]
    public void Repositories_AreRegisteredInCompositionRoot()
    {
        // AC[1] guard: the composition root exposes the data-access seam so callers never touch SQL.
        _provider.GetService<IDownloadRepository>().Should().NotBeNull();
        _provider.GetService<ISegmentRepository>().Should().NotBeNull();
        _provider.GetService<ISettingsRepository>().Should().NotBeNull();
        _provider.GetService<IBlacklistRepository>().Should().NotBeNull();
    }

    public void Dispose()
    {
        _provider.Dispose();
        SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; the OS temp dir is reclaimed regardless.
        }
    }
}
