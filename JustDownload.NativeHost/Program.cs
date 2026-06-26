using JustDownload.Core;
using JustDownload.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

// Native Messaging Host entry point (stub).
// Real stdin/stdout length-prefixed framing for the browser extension lands in a later task.
// The host builds its service provider from Core's single composition root (§6).
using ServiceProvider provider = new ServiceCollection()
    .AddJustDownloadCore()
    .BuildServiceProvider();

IAppInfoProvider appInfo = provider.GetRequiredService<IAppInfoProvider>();
Console.WriteLine($"{appInfo.Name} Native Messaging Host");
return 0;
