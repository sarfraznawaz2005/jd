using System.Text.Json;
using FluentAssertions;
using JustDownload.Core.NativeMessaging.Registration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JustDownload.Tests.NativeMessaging;

/// <summary>
/// Native-messaging host registration (TASK-065, US-11): the manifest JSON per browser, the correct per-OS
/// manifest locations (AC1), and the registrar writing all three browsers' manifests on register and removing
/// them on unregister (AC0/AC2).
/// </summary>
public sealed class NativeHostRegistrationTests
{
    private static readonly NativeHostRegistration Registration = new()
    {
        Name = "app.justdownload.host",
        Description = "JustDownload native messaging host",
        ExecutablePath = "/opt/justdownload/host",
        AllowedOrigins = ["chrome-extension://abcdefghijklmnopabcdefghijklmnop/"],
        AllowedExtensionIds = ["justdownload@justdownload.app"],
    };

    [Theory]
    [InlineData(NativeMessagingBrowser.Chrome)]
    [InlineData(NativeMessagingBrowser.Edge)]
    public void Manifest_Chromium_UsesAllowedOrigins(NativeMessagingBrowser browser)
    {
        string json = NativeHostManifest.Build(browser, Registration);

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        root.GetProperty("name").GetString().Should().Be("app.justdownload.host");
        root.GetProperty("type").GetString().Should().Be("stdio");
        root.GetProperty("path").GetString().Should().Be("/opt/justdownload/host");
        root.GetProperty("allowed_origins")[0].GetString().Should().Be("chrome-extension://abcdefghijklmnopabcdefghijklmnop/");
        root.TryGetProperty("allowed_extensions", out _).Should().BeFalse("Chromium uses allowed_origins");
    }

    [Fact]
    public void Manifest_Firefox_UsesAllowedExtensions()
    {
        string json = NativeHostManifest.Build(NativeMessagingBrowser.Firefox, Registration);

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        root.GetProperty("allowed_extensions")[0].GetString().Should().Be("justdownload@justdownload.app");
        root.TryGetProperty("allowed_origins", out _).Should().BeFalse("Firefox uses allowed_extensions");
    }

    [Theory]
    [InlineData(NativeMessagingBrowser.Chrome, ".config/google-chrome/NativeMessagingHosts/app.justdownload.host.json")]
    [InlineData(NativeMessagingBrowser.Edge, ".config/microsoft-edge/NativeMessagingHosts/app.justdownload.host.json")]
    [InlineData(NativeMessagingBrowser.Firefox, ".mozilla/native-messaging-hosts/app.justdownload.host.json")]
    public void Locations_Linux_UsesPerBrowserConfigDir(NativeMessagingBrowser browser, string expectedSuffix)
    {
        var locations = new NativeHostManifestLocations(HostOsPlatform.Linux, "/home/u", "/home/u/.appdata", "JustDownload");

        string path = locations.ManifestPath(browser, "app.justdownload.host").Replace('\\', '/');

        path.Should().Be("/home/u/" + expectedSuffix);
    }

    [Theory]
    [InlineData(NativeMessagingBrowser.Chrome, "Library/Application Support/Google/Chrome/NativeMessagingHosts/app.justdownload.host.json")]
    [InlineData(NativeMessagingBrowser.Edge, "Library/Application Support/Microsoft Edge/NativeMessagingHosts/app.justdownload.host.json")]
    [InlineData(NativeMessagingBrowser.Firefox, "Library/Application Support/Mozilla/NativeMessagingHosts/app.justdownload.host.json")]
    public void Locations_MacOs_UsesApplicationSupport(NativeMessagingBrowser browser, string expectedSuffix)
    {
        var locations = new NativeHostManifestLocations(HostOsPlatform.MacOs, "/Users/u", "/Users/u/appdata", "JustDownload");

        string path = locations.ManifestPath(browser, "app.justdownload.host").Replace('\\', '/');

        path.Should().Be("/Users/u/" + expectedSuffix);
    }

    [Fact]
    public void Locations_Windows_UsesAppDataDirectory()
    {
        var locations = new NativeHostManifestLocations(HostOsPlatform.Windows, @"C:\Users\u", @"C:\Users\u\AppData\Roaming", "JustDownload");

        string path = locations.ManifestPath(NativeMessagingBrowser.Chrome, "app.justdownload.host").Replace('\\', '/');

        path.Should().Be("C:/Users/u/AppData/Roaming/JustDownload/NativeMessagingHosts/Chrome/app.justdownload.host.json");
    }

    private sealed class FakeRegistryWriter : INativeHostRegistryWriter
    {
        public Dictionary<NativeMessagingBrowser, string> Entries { get; } = [];

        public void SetHostPath(NativeMessagingBrowser browser, string hostName, string manifestPath) =>
            Entries[browser] = manifestPath;

        public void Remove(NativeMessagingBrowser browser, string hostName) => Entries.Remove(browser);
    }

    [Fact]
    public void Register_WritesAllThreeManifests_AndRegistryEntries_ThenUnregisterRemovesThem()
    {
        string dir = Path.Combine(Path.GetTempPath(), "jd-nmh-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Use the Windows layout so files land under our temp app-data dir, and a fake registry writer.
            var locations = new NativeHostManifestLocations(HostOsPlatform.Windows, dir, dir, "JustDownload");
            var registry = new FakeRegistryWriter();
            var registrar = new NativeHostRegistrar(locations, registry, NullLogger<NativeHostRegistrar>.Instance);

            registrar.Register(Registration);

            // AC0: a manifest exists for each of the three browsers, with valid content.
            foreach (NativeMessagingBrowser browser in registrar.Browsers)
            {
                string path = registrar.ManifestPath(browser, Registration.Name);
                File.Exists(path).Should().BeTrue($"the {browser} manifest is written");
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                doc.RootElement.GetProperty("name").GetString().Should().Be(Registration.Name);
            }

            registry.Entries.Should().HaveCount(3, "every browser gets a registry entry on Windows");

            registrar.Unregister(Registration.Name);

            // AC2: uninstall removes both the files and the registry entries.
            foreach (NativeMessagingBrowser browser in registrar.Browsers)
            {
                File.Exists(registrar.ManifestPath(browser, Registration.Name)).Should().BeFalse();
            }

            registry.Entries.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
