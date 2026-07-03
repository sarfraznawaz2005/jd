using JustDownload.Core;
using JustDownload.Core.Abstractions;
using JustDownload.Core.Diagnostics;
using JustDownload.Core.NativeMessaging;
using Microsoft.Extensions.DependencyInjection;

// Native Messaging Host entry point (TASK-064, D8). The browser launches this process, passes the
// calling extension's origin/id as an argument, and exchanges length-prefixed JSON over stdin/stdout.
// The host builds its service provider from Core's single composition root (§6) and opens no socket.
using ServiceProvider provider = new ServiceCollection()
    .AddJustDownloadCore()
    .BuildServiceProvider();

// Capture and surface unhandled exceptions — no silent failures (§1).
provider.GetRequiredService<IGlobalErrorHandler>().Install();

// Run Core's async startup initialisation (schema + persisted categorization overrides).
await provider.InitializeJustDownloadCoreAsync();

string[] launchArgs = args;
var options = provider.GetRequiredService<NativeHostOptions>();
string? origin = ExtensionOrigin.FromArguments(launchArgs);

// No browser-supplied origin → a manual/diagnostic launch. Print info and exit cleanly rather than
// blocking on stdin.
if (origin is null)
{
    IAppInfoProvider appInfo = provider.GetRequiredService<IAppInfoProvider>();
    await Console.Error.WriteLineAsync(
        $"{appInfo.Name} Native Messaging Host — launched by the browser extension over stdio.");
    return 0;
}

// A browser launch: validate the calling extension against the allowlist before reading any message.
if (!ExtensionOrigin.IsAllowed(origin, options.AllowedExtensionIds))
{
    await Console.Error.WriteLineAsync($"Refusing connection from unauthorized extension origin: {origin}");
    return 1;
}

// A real, browser-initiated launch from a known extension — record contact (TASK-175) before the message
// loop starts, so the app's Browsers panel can show a genuine "extension installed and talking to us"
// signal instead of just "we wrote a manifest file". Best-effort: a write failure here must never block
// the actual native-messaging conversation the browser is waiting on.
if (ExtensionOrigin.Categorize(origin) is { } contactOrigin)
{
    try
    {
        await provider.GetRequiredService<IExtensionContactTracker>().RecordContactAsync(contactOrigin);
    }
    catch (IOException)
    {
    }
}

var host = provider.GetRequiredService<NativeMessageHost>();
await using Stream stdin = Console.OpenStandardInput();
await using Stream stdout = Console.OpenStandardOutput();
await host.RunAsync(stdin, stdout);
return 0;
