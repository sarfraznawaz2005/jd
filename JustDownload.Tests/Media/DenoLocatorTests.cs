using FluentAssertions;
using JustDownload.Core.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JustDownload.Tests.Media;

/// <summary>
/// <see cref="DenoLocator"/>'s caching contract: the first successful <c>--version</c> probe is cached, and
/// <see cref="DenoLocator.Invalidate"/> forces the next <see cref="DenoLocator.LocateAsync"/> call to
/// re-probe rather than serve the stale cached <see cref="DenoInfo"/> — mirrors
/// <see cref="YtDlpLocatorTests"/> (TASK: stale-vendor-binary fix, 2026-08-14).
/// </summary>
public sealed class DenoLocatorTests : IDisposable
{
    private readonly string _scriptDir = Path.Combine(
        Path.GetTempPath(), "jd-deno-locator-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _scriptPath;

    public DenoLocatorTests()
    {
        Directory.CreateDirectory(_scriptDir);
        _scriptPath = OperatingSystem.IsWindows()
            ? Path.Combine(_scriptDir, "fake-deno.cmd")
            : Path.Combine(_scriptDir, "fake-deno.sh");
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
        var options = new DenoOptions { DenoPath = _scriptPath };
        var locator = new DenoLocator(options, NullLogger<DenoLocator>.Instance);

        DenoInfo? first = await locator.LocateAsync();
        first.Should().NotBeNull();
        first!.Version.Should().Be("1.0.0");

        // Change what the script reports; a cached locator must NOT notice until invalidated.
        WriteVersionScript("2.9.5");
        DenoInfo? stillCached = await locator.LocateAsync();
        stillCached.Should().BeSameAs(first, "the result is cached after the first successful resolution");

        locator.Invalidate();
        DenoInfo? afterInvalidate = await locator.LocateAsync();
        afterInvalidate.Should().NotBeSameAs(first);
        afterInvalidate!.Version.Should().Be("2.9.5", "invalidating drops the cache so the next call re-probes");
    }

    private void WriteVersionScript(string version)
    {
        // Mirrors deno --version's real banner format ("deno X.Y.Z (release, ...)") so DenoLocator.ParseVersion
        // extracts the version token exactly as it would from a real Deno binary.
        string banner = $"deno {version} (release, x86_64-pc-windows-msvc)";
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(_scriptPath, $"@echo off{Environment.NewLine}echo {banner}{Environment.NewLine}exit /b 0{Environment.NewLine}");
        }
        else
        {
            File.WriteAllText(_scriptPath, $"#!/bin/sh{Environment.NewLine}echo \"{banner}\"{Environment.NewLine}exit 0{Environment.NewLine}");
            File.SetUnixFileMode(
                _scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }
}
