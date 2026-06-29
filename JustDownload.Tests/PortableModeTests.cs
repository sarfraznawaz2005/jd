using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Abstractions;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests;

/// <summary>
/// Portable mode (TASK-138): a marker file beside the executable redirects all state into a "Data" folder
/// next to the app, and the shared app-data resolver honors it.
/// </summary>
public sealed class PortableModeTests : IDisposable
{
    private readonly string _baseDir = Path.Combine(Path.GetTempPath(), "jd-portable-" + Guid.NewGuid().ToString("N"));

    public PortableModeTests() => Directory.CreateDirectory(_baseDir);

    [Fact]
    public void IsPortable_TrueOnlyWhenTheMarkerFileExists()
    {
        PortableMode.IsPortable(_baseDir).Should().BeFalse("no marker yet");

        File.WriteAllText(Path.Combine(_baseDir, PortableMode.MarkerFileName), "");

        PortableMode.IsPortable(_baseDir).Should().BeTrue();
        PortableMode.DataDirectory(_baseDir).Should().Be(Path.Combine(_baseDir, PortableMode.DataFolderName));
    }

    [Fact]
    public void AppDataPaths_Portable_RedirectsBesideTheExecutable()
    {
        var appInfo = Substitute.For<IAppInfoProvider>();
        appInfo.Name.Returns("JustDownload");
        File.WriteAllText(Path.Combine(_baseDir, PortableMode.MarkerFileName), "");

        string dir = AppDataPaths.Directory(appInfo, _baseDir);

        dir.Should().Be(Path.Combine(_baseDir, PortableMode.DataFolderName));
    }

    [Fact]
    public void AppDataPaths_NonPortable_UsesPerOsAppDataUnderTheAppName()
    {
        var appInfo = Substitute.For<IAppInfoProvider>();
        appInfo.Name.Returns("JustDownload");

        string dir = AppDataPaths.Directory(appInfo, _baseDir); // no marker file present

        dir.Should().EndWith("JustDownload");
        dir.Should().NotBe(Path.Combine(_baseDir, PortableMode.DataFolderName));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_baseDir))
            {
                Directory.Delete(_baseDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
