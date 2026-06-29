using JustDownload.Cli;
using JustDownload.Core;
using JustDownload.Core.Data.Migrations;
using Microsoft.Extensions.DependencyInjection;

// Compose the headless engine exactly as the GUI host does (D5), migrate the database, then dispatch the
// command. The CLI is a thin shell over CliRunner so all behaviour stays unit-testable.
using ServiceProvider provider = new ServiceCollection()
    .AddJustDownloadCore()
    .BuildServiceProvider();

await provider.GetRequiredService<IMigrationRunner>().MigrateAsync().ConfigureAwait(false);

var runner = new CliRunner(provider, Console.Out);
return await runner.RunAsync(args).ConfigureAwait(false);
