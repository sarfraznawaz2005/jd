using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Data;
using JustDownload.Core.Data.Migrations;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.Settings;

/// <summary>
/// Tests for the typed settings service (TASK-021). Persistence is exercised against a real temp
/// SQLite database so the "survives a restart" criterion is genuine: a value is written through one
/// service instance, the provider is disposed (simulating shutdown), then a brand-new provider/service
/// is built over the same database file and the value is read back.
/// </summary>
public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<ServiceProvider> _providers = new();

    public SettingsServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jd-settings-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    private ServiceProvider NewProvider()
    {
        var pathProvider = Substitute.For<IDatabasePathProvider>();
        pathProvider.DatabaseDirectory.Returns(_tempDir);
        pathProvider.DatabasePath.Returns(Path.Combine(_tempDir, "test.db"));

        var services = new ServiceCollection();
        services.AddSingleton(pathProvider);
        services.AddJustDownloadCore();
        ServiceProvider provider = services.BuildServiceProvider();
        _providers.Add(provider);

        // Stand up the schema the settings repository writes against.
        provider.GetRequiredService<IMigrationRunner>().Migrate();
        return provider;
    }

    [Fact]
    public void Current_BeforeLoad_IsAllDefaults_IncludingLightTheme()
    {
        // AC[0] + AC[3]: typed defaults are available immediately, before any storage hydration.
        var service = NewProvider().GetRequiredService<ISettingsService>();

        AppSettings defaults = service.Current;

        defaults.MaxConcurrentDownloads.Should().Be(4);
        defaults.ConnectionsPerDownload.Should().Be(8);
        defaults.GlobalSpeedLimitBytesPerSecond.Should().Be(0);
        defaults.DefaultVideoQuality.Should().Be(VideoQuality.P1080);
        defaults.DefaultContainer.Should().Be(MediaContainer.Mkv);
        defaults.Density.Should().Be(UiDensity.Comfortable);
        defaults.Theme.Should().Be(AppTheme.Light);
    }

    [Fact]
    public async Task LoadAsync_OnEmptyStore_YieldsDefaultsWithLightTheme()
    {
        // AC[0] + AC[3]: an empty database hydrates to the documented defaults.
        var service = NewProvider().GetRequiredService<ISettingsService>();

        await service.LoadAsync();

        service.Current.Should().Be(new AppSettings());
        service.Current.Theme.Should().Be(AppTheme.Light);
    }

    [Fact]
    public async Task UpdateAsync_ChangesTypedValue_AndIsReadableFromCurrent()
    {
        // AC[0]: typed set then typed get.
        var service = NewProvider().GetRequiredService<ISettingsService>();

        AppSettings result = await service.UpdateAsync(s => s with
        {
            MaxConcurrentDownloads = 2,
            Theme = AppTheme.Dark,
            DefaultContainer = MediaContainer.Mp4,
        });

        result.MaxConcurrentDownloads.Should().Be(2);
        service.Current.MaxConcurrentDownloads.Should().Be(2);
        service.Current.Theme.Should().Be(AppTheme.Dark);
        service.Current.DefaultContainer.Should().Be(MediaContainer.Mp4);
        // Untouched values keep their defaults.
        service.Current.ConnectionsPerDownload.Should().Be(8);
    }

    [Fact]
    public async Task UpdateAsync_RaisesChangedEvent_WithChangedKeys()
    {
        // AC[1]: a real change notifies, exposing the previous/current snapshots and changed keys.
        var service = NewProvider().GetRequiredService<ISettingsService>();
        await service.LoadAsync();

        SettingsChangedEventArgs? captured = null;
        service.Changed += (_, e) => captured = e;

        await service.UpdateAsync(s => s with { Theme = AppTheme.Dark });

        captured.Should().NotBeNull();
        captured!.Previous.Theme.Should().Be(AppTheme.Light);
        captured.Current.Theme.Should().Be(AppTheme.Dark);
        captured.ChangedKeys.Should().ContainSingle().Which.Should().Be(SettingsSerializer.ThemeKey);
    }

    [Fact]
    public async Task UpdateAsync_NoOp_DoesNotRaiseOrChange()
    {
        // AC[1]: setting a value to its current value is a no-op — no notification, no churn.
        var service = NewProvider().GetRequiredService<ISettingsService>();
        await service.LoadAsync();

        int raised = 0;
        service.Changed += (_, _) => raised++;

        AppSettings result = await service.UpdateAsync(s => s with { Theme = AppTheme.Light });

        raised.Should().Be(0);
        result.Should().Be(new AppSettings());
    }

    [Fact]
    public async Task Settings_PersistAcrossRestarts()
    {
        // AC[2]: write through one instance, dispose it, re-open a fresh store, value survives.
        ServiceProvider first = NewProvider();
        var writer = first.GetRequiredService<ISettingsService>();
        await writer.LoadAsync();
        await writer.UpdateAsync(s => s with
        {
            MaxConcurrentDownloads = 7,
            ConnectionsPerDownload = 16,
            GlobalSpeedLimitBytesPerSecond = 1_048_576,
            DefaultVideoQuality = VideoQuality.P2160,
            DefaultContainer = MediaContainer.Mp4,
            Density = UiDensity.Compact,
            Theme = AppTheme.Dark,
        });

        // Simulate application shutdown.
        first.Dispose();
        _providers.Remove(first);
        SqliteConnection.ClearAllPools();

        // Re-open a completely fresh store over the same database file.
        var reopened = NewProvider().GetRequiredService<ISettingsService>();
        await reopened.LoadAsync();

        reopened.Current.Should().Be(new AppSettings
        {
            MaxConcurrentDownloads = 7,
            ConnectionsPerDownload = 16,
            GlobalSpeedLimitBytesPerSecond = 1_048_576,
            DefaultVideoQuality = VideoQuality.P2160,
            DefaultContainer = MediaContainer.Mp4,
            Density = UiDensity.Compact,
            Theme = AppTheme.Dark,
        });
    }

    [Fact]
    public async Task LoadAsync_WithCorruptStoredValue_FallsBackToDefault()
    {
        // Robustness: a garbage value in storage must not crash startup — it degrades to the default.
        ServiceProvider provider = NewProvider();
        var repo = provider.GetRequiredService<ISettingsRepository>();
        await repo.SetAsync(SettingsSerializer.MaxConcurrentDownloadsKey, "not-a-number");
        await repo.SetAsync(SettingsSerializer.ThemeKey, "Dark");

        var service = provider.GetRequiredService<ISettingsService>();
        await service.LoadAsync();

        service.Current.MaxConcurrentDownloads.Should().Be(4, "the corrupt value is ignored");
        service.Current.Theme.Should().Be(AppTheme.Dark, "the valid value is honored");
    }

    [Fact]
    public async Task UpdateAsync_PersistsOnlyChangedKeys()
    {
        // Efficiency contract: a single-field change writes exactly one row, leaving the rest absent.
        ServiceProvider provider = NewProvider();
        var service = provider.GetRequiredService<ISettingsService>();
        await service.LoadAsync();

        await service.UpdateAsync(s => s with { Theme = AppTheme.Dark });

        var repo = provider.GetRequiredService<ISettingsRepository>();
        IReadOnlyDictionary<string, string?> rows = await repo.GetAllAsync();
        rows.Should().ContainSingle();
        rows.Should().ContainKey(SettingsSerializer.ThemeKey).WhoseValue.Should().Be("Dark");
    }

    public void Dispose()
    {
        foreach (ServiceProvider provider in _providers)
        {
            provider.Dispose();
        }

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
