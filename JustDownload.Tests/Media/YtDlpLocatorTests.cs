using FluentAssertions;
using JustDownload.Core.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JustDownload.Tests.Media;

/// <summary>
/// <see cref="YtDlpLocator"/>'s caching contract: the first successful <c>--version</c> probe is cached, and
/// <see cref="YtDlpLocator.Invalidate"/> forces the next <see cref="YtDlpLocator.LocateAsync"/> call to
/// re-probe rather than serve the stale cached <see cref="YtDlpInfo"/> — this is what lets
/// <see cref="YtDlpProvisioner"/>'s in-process upgrade actually take effect for every consumer sharing the
/// same singleton locator (TASK: stale-vendor-binary fix, 2026-08-14).
/// </summary>
public sealed class YtDlpLocatorTests : IDisposable
{
    private readonly string _scriptDir = Path.Combine(
        Path.GetTempPath(), "jd-ytdlp-locator-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _scriptPath;
    private readonly string _versionFile;

    public YtDlpLocatorTests()
    {
        Directory.CreateDirectory(_scriptDir);
        _versionFile = Path.Combine(_scriptDir, "version.txt");
        _scriptPath = OperatingSystem.IsWindows()
            ? Path.Combine(_scriptDir, "fake-yt-dlp.cmd")
            : Path.Combine(_scriptDir, "fake-yt-dlp.sh");
        WriteVersionScript("1.0.0");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_scriptDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task LocateAsync_CachesResult_UntilInvalidated()
    {
        var options = new YtDlpOptions { YtDlpPath = _scriptPath };
        var locator = new YtDlpLocator(options, NullLogger<YtDlpLocator>.Instance);

        YtDlpInfo? first = await locator.LocateAsync();
        first.Should().NotBeNull();
        first!.Version.Should().Be("1.0.0");

        // Change what the script reports; a cached locator must NOT notice until invalidated.
        WriteVersionScript("2.0.0");
        YtDlpInfo? stillCached = await locator.LocateAsync();
        stillCached.Should().BeSameAs(first, "the result is cached after the first successful resolution");

        locator.Invalidate();
        YtDlpInfo? afterInvalidate = await locator.LocateAsync();
        afterInvalidate.Should().NotBeSameAs(first);
        afterInvalidate!.Version.Should().Be("2.0.0", "invalidating drops the cache so the next call re-probes");
    }

    private void WriteVersionScript(string version)
    {
        // Windows: a .cmd batch file that echoes the version and exits 0 — matches how the locator would
        // find a real yt-dlp.exe by running it and reading its --version output.
        // Unix: an executable shell script doing the same.
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(_scriptPath, $"@echo off{Environment.NewLine}echo {version}{Environment.NewLine}exit /b 0{Environment.NewLine}");
        }
        else
        {
            File.WriteAllText(_scriptPath, $"#!/bin/sh{Environment.NewLine}echo {version}{Environment.NewLine}exit 0{Environment.NewLine}");
            File.SetUnixFileMode(
                _scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }
}
