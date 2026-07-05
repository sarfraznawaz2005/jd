using System.Diagnostics;
using FluentAssertions;
using JustDownload.Core.NativeMessaging;
using Xunit;

namespace JustDownload.Tests.NativeMessaging;

/// <summary>
/// End-to-end browser → native-host → app-delivery test (TASK-092). Spawns the real
/// <c>JustDownload.NativeHost</c> executable and speaks the exact Native Messaging wire protocol a browser
/// uses (4-byte little-endian length + UTF-8 JSON over stdio), proving: the host accepts an allowlisted
/// extension, answers <c>ping</c> with <c>pong</c>, processes a <c>download_link</c>, and queues it to the
/// hand-off inbox the desktop app drains — and rejects a non-allowlisted extension. The host's data dir is
/// redirected to a temp folder so the real app data is never touched.
/// </summary>
[Trait("Category", "NativeHostE2e")]
public sealed class NativeHostE2eTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly string _tempData;

    public NativeHostE2eTests()
    {
        _tempData = Path.Combine(Path.GetTempPath(), "jd-host-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempData);
    }

    [Fact]
    public async Task AllowlistedExtension_PingPongs_AndQueuesDownloadLink_ToInbox()
    {
        // Ping now answers "pong" only when the desktop app is actually running (TASK-185) — simulate that
        // by holding the same mutex AppLauncher/AppRunningProbe check, so this test still exercises the
        // real ping-pong success path rather than the (separately tested) "app not running" case.
        using var appRunning = new Mutex(initiallyOwned: true, AppLauncher.RunningMutexName);

        using var cts = new CancellationTokenSource(Timeout);
        Process host = StartHost(NativeHostIdentity.FirefoxExtensionId);
        try
        {
            Stream toHost = host.StandardInput.BaseStream;
            Stream fromHost = host.StandardOutput.BaseStream;

            // ping -> pong
            await NativeMessageCodec.WriteAsync(toHost, "{\"type\":\"ping\"}", cts.Token);
            string? pong = await NativeMessageCodec.ReadAsync(fromHost, cancellationToken: cts.Token);
            pong.Should().NotBeNull().And.Contain("pong");

            // download_link -> ok, with auth context carried
            const string url = "https://example.com/video.mp4?sig=abc";
            string link =
                "{\"type\":\"download_link\",\"url\":\"" + url +
                "\",\"referrer\":\"https://example.com/watch\",\"cookies\":\"session=xyz\"}";
            await NativeMessageCodec.WriteAsync(toHost, link, cts.Token);
            string? ack = await NativeMessageCodec.ReadAsync(fromHost, cancellationToken: cts.Token);
            ack.Should().NotBeNull().And.Contain("ok");

            // Closing stdin ends the host loop; wait for a clean exit.
            host.StandardInput.Close();
            (await WaitForExitAsync(host, cts.Token)).Should().BeTrue("the host exits when its stdin closes");

            // The link reached the hand-off inbox the desktop app drains on start.
            PendingLink delivered = await DrainInboxAsync();
            delivered.Url.Should().Be(url);
            delivered.Referrer.Should().Be("https://example.com/watch");
            delivered.Cookies.Should().Be("session=xyz");
        }
        finally
        {
            Kill(host);
        }
    }

    [Fact]
    public async Task Ping_WhenNoAppIsRunning_AnswersAppNotRunning_NotPong()
    {
        // TASK-185: before this fix, ping always answered pong regardless of whether the desktop app was
        // actually running, so the extension popup showed "App connected" even with the app fully closed.
        //
        // The sibling AllowlistedExtension_PingPongs_AndQueuesDownloadLink_ToInbox test simulates a running
        // app by holding AppLauncher.RunningMutexName for the lifetime of its method; xUnit runs facts in
        // this class sequentially so that mutex is disposed well before this one starts, but observed once
        // on a loaded ubuntu runner as still visible to AppRunningProbe immediately afterward. Wait for the
        // mutex to genuinely disappear before asserting on it, so a benign OS-level release delay can't be
        // mistaken for the real regression this test guards against.
        await WaitForNoRunningAppMutexAsync();

        using var cts = new CancellationTokenSource(Timeout);
        Process host = StartHost(NativeHostIdentity.FirefoxExtensionId);
        try
        {
            await NativeMessageCodec.WriteAsync(host.StandardInput.BaseStream, "{\"type\":\"ping\"}", cts.Token);
            string? reply = await NativeMessageCodec.ReadAsync(host.StandardOutput.BaseStream, cancellationToken: cts.Token);

            reply.Should().NotBeNull().And.NotContain("pong").And.Contain("app_not_running");
        }
        finally
        {
            Kill(host);
        }
    }

    [Fact]
    public async Task NonAllowlistedExtension_IsRejected_WithoutQueueing()
    {
        Process host = StartHost("chrome-extension://unknownunknownunknownunknownun/");
        try
        {
            (await WaitForExitAsync(host, new CancellationTokenSource(Timeout).Token))
                .Should().BeTrue("the host exits immediately for a disallowed origin");
            host.ExitCode.Should().Be(1, "a non-allowlisted extension is refused");

            Directory.EnumerateFiles(_tempData, "extension-inbox.json", SearchOption.AllDirectories)
                .Should().BeEmpty("a rejected extension never queues anything");
        }
        finally
        {
            Kill(host);
        }
    }

    private Process StartHost(string origin)
    {
        string exe = ResolveHostExecutable();
        File.Exists(exe).Should().BeTrue($"the native host must be built at {exe}");

        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(origin);
        // Redirect the host's app-data dir (DB + inbox) to the temp folder so the real app data is untouched.
        psi.Environment["JUSTDOWNLOAD_DATA_DIR"] = _tempData;

        return Process.Start(psi) ?? throw new InvalidOperationException("Failed to start the native host.");
    }

    private async Task<PendingLink> DrainInboxAsync()
    {
        string inboxPath = Directory
            .EnumerateFiles(_tempData, "extension-inbox.json", SearchOption.AllDirectories)
            .FirstOrDefault() ?? throw new FileNotFoundException("The host did not write an inbox file.");

        using var inbox = new ExtensionInbox(inboxPath);
        IReadOnlyList<PendingLink> links = await inbox.DrainAsync();
        return links.Should().ContainSingle().Subject;
    }

    private static string ResolveHostExecutable()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "JustDownload.sln")))
        {
            dir = dir.Parent;
        }

        string repoRoot = dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found");
        string config = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            ? "Release"
            : "Debug";
        string name = OperatingSystem.IsWindows() ? "JustDownload.NativeHost.exe" : "JustDownload.NativeHost";
        return Path.Combine(repoRoot, "JustDownload.NativeHost", "bin", config, "net8.0", name);
    }

    private static async Task WaitForNoRunningAppMutexAsync()
    {
        var probe = new AppRunningProbe();
        for (int attempt = 0; attempt < 20 && probe.IsRunning(); attempt++)
        {
            await Task.Delay(100);
        }
    }

    private static async Task<bool> WaitForExitAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }

        process.Dispose();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempData, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
