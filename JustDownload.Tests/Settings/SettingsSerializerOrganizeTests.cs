using FluentAssertions;
using JustDownload.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JustDownload.Tests.Settings;

/// <summary>Round-trip tests for the organize-by-category settings added in TASK-046.</summary>
public sealed class SettingsSerializerOrganizeTests
{
    [Fact]
    public void RoundTrips_OrganizeSettings()
    {
        var settings = new AppSettings
        {
            OrganizeByCategory = true,
            OrganizedRootDirectory = @"C:\Sorted",
        };

        IReadOnlyDictionary<string, string> stored = SettingsSerializer.ToStorage(settings);
        AppSettings restored = SettingsSerializer.FromStorage(
            stored.ToDictionary(kv => kv.Key, kv => (string?)kv.Value),
            NullLogger.Instance);

        restored.OrganizeByCategory.Should().BeTrue();
        restored.OrganizedRootDirectory.Should().Be(@"C:\Sorted");
    }

    [Fact]
    public void DefaultsAreOff_WhenStorageEmpty()
    {
        AppSettings restored = SettingsSerializer.FromStorage(
            new Dictionary<string, string?>(), NullLogger.Instance);

        restored.OrganizeByCategory.Should().BeFalse();
        restored.OrganizedRootDirectory.Should().BeNull();
    }

    [Fact]
    public void EmptyRoot_DeserializesAsNull()
    {
        var stored = new Dictionary<string, string?>
        {
            [SettingsSerializer.OrganizedRootDirectoryKey] = string.Empty,
        };

        SettingsSerializer.FromStorage(stored, NullLogger.Instance).OrganizedRootDirectory.Should().BeNull();
    }
}
