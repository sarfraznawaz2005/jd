using JustDownload.Core;
using JustDownload.Core.Abstractions;
using JustDownload.Core.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

// Native Messaging Host entry point (stub).
// Real stdin/stdout length-prefixed framing for the browser extension lands in a later task.
// The host builds its service provider from Core's single composition root (§6).
using ServiceProvider provider = new ServiceCollection()
    .AddJustDownloadCore()
    .BuildServiceProvider();

// Capture and surface unhandled exceptions — no silent failures (§1).
provider.GetRequiredService<IGlobalErrorHandler>().Install();

// Run Core's async startup initialisation (applies persisted categorization overrides, TASK-085).
await provider.InitializeJustDownloadCoreAsync();

IAppInfoProvider appInfo = provider.GetRequiredService<IAppInfoProvider>();
Console.WriteLine($"{appInfo.Name} Native Messaging Host");
return 0;
