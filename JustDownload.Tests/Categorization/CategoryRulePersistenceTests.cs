using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Categorization;
using JustDownload.Core.Data;
using JustDownload.Core.Data.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.Categorization;

/// <summary>
/// Integration tests for persisting categorization rule overrides (TASK-085) against a real temp SQLite
/// database. Each test builds one or more service providers over the SAME database directory to simulate
/// app restarts, proving overrides are saved through the data layer and re-applied over the seeded
/// defaults at startup. The database lives in an isolated temp directory cleaned up on dispose.
/// </summary>
public sealed class CategoryRulePersistenceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<ServiceProvider> _providers = [];

    public CategoryRulePersistenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jd-catrules-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    /// <summary>
    /// Builds a fresh provider over the shared temp database and migrates it. A fresh provider has its
    /// own seeded-default <see cref="CategorizationRules"/> (no overrides applied yet) — modelling a cold
    /// app start that must reload persisted overrides.
    /// </summary>
    private ServiceProvider BuildProvider()
    {
        var pathProvider = Substitute.For<IDatabasePathProvider>();
        pathProvider.DatabaseDirectory.Returns(_tempDir);
        pathProvider.DatabasePath.Returns(Path.Combine(_tempDir, "test.db"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(pathProvider);
        services.AddJustDownloadData();
        services.AddJustDownloadCategorization();

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        provider.GetRequiredService<IMigrationRunner>().Migrate();
        return provider;
    }

    [Fact]
    public async Task SetOverride_AppliesImmediately_AndSurvivesRestart()
    {
        // AC[0] + AC[1] + AC[2]: edit persists, and a fresh start reloads and applies it.
        ServiceProvider first = BuildProvider();
        await first.GetRequiredService<ICategoryRuleService>()
            .SetOverrideAsync(new CategoryRuleOverride(CategoryRuleScope.Extension, "dat", FileCategory.Audio));

        // Applied immediately to the live rules of the editing session.
        first.GetRequiredService<IFileCategorizer>().Categorize("clip.dat").Should().Be(FileCategory.Audio);

        // A brand-new session starts from the seeded defaults (.dat is unknown → Other)...
        ServiceProvider restarted = BuildProvider();
        restarted.GetRequiredService<IFileCategorizer>().Categorize("clip.dat").Should().Be(FileCategory.Other);

        // ...until startup initialisation reloads the persisted override.
        await restarted.GetRequiredService<ICategoryRuleService>().ApplyPersistedOverridesAsync();
        restarted.GetRequiredService<IFileCategorizer>().Categorize("clip.dat").Should().Be(FileCategory.Audio);
    }

    [Fact]
    public async Task InitializeJustDownloadCoreAsync_AppliesPersistedMimeOverride()
    {
        // AC[1]: the host startup seam applies persisted overrides — proven for the MIME dimension too.
        ServiceProvider first = BuildProvider();
        await first.GetRequiredService<ICategoryRuleService>()
            .SetOverrideAsync(new CategoryRuleOverride(
                CategoryRuleScope.MimeType, "application/x-foo", FileCategory.Compressed));

        ServiceProvider restarted = BuildProvider();
        await restarted.InitializeJustDownloadCoreAsync();

        restarted.GetRequiredService<IFileCategorizer>()
            .Categorize(fileNameOrExtension: null, "application/x-foo")
            .Should().Be(FileCategory.Compressed);
    }

    [Fact]
    public async Task Store_RoundTrips_AllScopes()
    {
        // AC[0]: the persistence encoding round-trips every override scope without loss.
        ServiceProvider provider = BuildProvider();
        var store = provider.GetRequiredService<ICategoryRuleStore>();

        var overrides = new List<CategoryRuleOverride>
        {
            new(CategoryRuleScope.Extension, "dat", FileCategory.Audio),
            new(CategoryRuleScope.MimeType, "application/x-foo", FileCategory.Compressed),
            new(CategoryRuleScope.MimeTopLevelType, "model", FileCategory.Other),
        };

        await store.SaveAsync(overrides);
        IReadOnlyList<CategoryRuleOverride> loaded = await store.LoadAsync();

        loaded.Should().BeEquivalentTo(overrides);
    }

    [Fact]
    public async Task Load_ReturnsEmpty_WhenNothingPersisted()
    {
        ServiceProvider provider = BuildProvider();
        IReadOnlyList<CategoryRuleOverride> loaded =
            await provider.GetRequiredService<ICategoryRuleStore>().LoadAsync();

        loaded.Should().BeEmpty();
    }

    [Fact]
    public async Task SetOverride_OnSameKey_Dedupes_LastWins_AndPersistsOne()
    {
        // Re-setting the same (case/dot-insensitive) key replaces rather than appends, in memory and on disk.
        ServiceProvider first = BuildProvider();
        var service = first.GetRequiredService<ICategoryRuleService>();
        await service.SetOverrideAsync(new CategoryRuleOverride(CategoryRuleScope.Extension, "dat", FileCategory.Audio));
        await service.SetOverrideAsync(new CategoryRuleOverride(CategoryRuleScope.Extension, ".DAT", FileCategory.Video));

        service.AppliedOverrides.Should().ContainSingle();
        first.GetRequiredService<IFileCategorizer>().Categorize("clip.dat").Should().Be(FileCategory.Video);

        ServiceProvider restarted = BuildProvider();
        await restarted.InitializeJustDownloadCoreAsync();
        restarted.GetRequiredService<ICategoryRuleService>().AppliedOverrides.Should().ContainSingle();
        restarted.GetRequiredService<IFileCategorizer>().Categorize("clip.dat").Should().Be(FileCategory.Video);
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
