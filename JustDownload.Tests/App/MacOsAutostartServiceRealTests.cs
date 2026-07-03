using System.Diagnostics;
using System.Runtime.Versioning;
using FluentAssertions;
using JustDownload.App.Services;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>
/// Real launchd verification (TASK-169 AC0). Unlike <c>AutostartTests.MacOsAutostart_*</c> (pure file I/O —
/// does the plist get written/removed correctly), this actually asks launchd to load the real plist and
/// observes it run <c>RunAtLoad</c>, the exact same mechanism the OS uses at real login — TASK-169's own
/// acceptance criterion explicitly allows "launchctl-loading the agent" as the CI-practical alternative to a
/// full logout/reboot cycle. Runtime-guarded to macOS only; selected in CI via
/// <c>dotnet test --filter "Category=RealAutostart"</c> (<c>.github/workflows/verify-autostart.yml</c>), kept
/// out of the everyday push/PR gate the same way TASK-168's <c>RealSecretStore</c> tests are (this touches
/// the real launchd session, not an isolated fixture).
/// </summary>
[Trait("Category", "RealAutostart")]
public sealed class MacOsAutostartServiceRealTests
{
    [Fact]
    [SupportedOSPlatform("macos")]
    public async Task EnablingAutostart_ThenLaunchctlLoadingTheAgent_ActuallyRunsTheProgram()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        string workDir = Path.Combine(Path.GetTempPath(), $"jd-ci-launchd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        string marker = Path.Combine(workDir, "fired.marker");
        string label = "app.justdownload.citest." + Guid.NewGuid().ToString("N");

        // ProgramArguments in the generated plist is a single-element array (no shell, no args — see
        // MacOsAutostartService.BuildPlist), so the "command" has to be a self-contained script that needs
        // no arguments: write the marker itself via its shebang line.
        string script = Path.Combine(workDir, "run.sh");
        await File.WriteAllTextAsync(script, $"#!/bin/sh\ntouch \"{marker}\"\n");
        File.SetUnixFileMode(
            script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        var service = new MacOsAutostartService(workDir, label, () => script);
        string plistPath = Path.Combine(workDir, $"{label}.plist");

        try
        {
            service.SetEnabled(true);
            File.Exists(plistPath).Should().BeTrue("the plist must exist before launchd can load it");

            (await RunLaunchctlAsync("load", plistPath)).Should().Be(
                0, "launchctl load must accept the generated plist");

            // RunAtLoad fires as soon as the agent is loaded, not just at a future login — that's the same
            // launchd mechanism a real login triggers, just invoked on demand here.
            bool fired = false;
            for (int i = 0; i < 50 && !fired; i++)
            {
                fired = File.Exists(marker);
                if (!fired)
                {
                    await Task.Delay(100);
                }
            }

            fired.Should().BeTrue("launchd must have actually run the program registered via MacOsAutostartService");
        }
        finally
        {
            await RunLaunchctlAsync("unload", plistPath);
            if (Directory.Exists(workDir))
            {
                Directory.Delete(workDir, recursive: true);
            }
        }
    }

    private static async Task<int> RunLaunchctlAsync(string verb, string plistPath)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("launchctl", $"{verb} \"{plistPath}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.Start();
        await process.WaitForExitAsync();
        return process.ExitCode;
    }
}
