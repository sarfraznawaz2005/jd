using System.Diagnostics.CodeAnalysis;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Downloading;
using JustDownload.Core.Lifecycle;
using Microsoft.Extensions.DependencyInjection;

namespace JustDownload.Cli;

/// <summary>
/// The command-line front-end's command dispatch (TASK-148). Drives <c>JustDownload.Core</c> through DI so the
/// headless engine is exercised exactly as the GUI uses it (D5). Kept free of <see cref="Console"/> — output
/// goes to an injected <see cref="TextWriter"/> and services come from an <see cref="IServiceProvider"/> — so
/// every command is unit-testable without spawning a process.
/// </summary>
public sealed class CliRunner
{
    private readonly IServiceProvider _services;
    private readonly TextWriter _output;

    public CliRunner(IServiceProvider services, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(output);
        _services = services;
        _output = output;
    }

    /// <summary>Runs the command named by <paramref name="args"/> and returns a process exit code (0 = success).</summary>
    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0)
        {
            return PrintUsage();
        }

        return args[0] switch
        {
            "download" or "dl" => await DownloadAsync(args[1..], cancellationToken).ConfigureAwait(false),
            "list" or "ls" => await ListAsync(cancellationToken).ConfigureAwait(false),
            "help" or "--help" or "-h" => PrintUsage(),
            _ => Unknown(args[0]),
        };
    }

    private async Task<int> DownloadAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || !TryGetUrl(args[0], out Uri? url))
        {
            await _output.WriteLineAsync("error: 'download' needs a valid http(s)/ftp URL.").ConfigureAwait(false);
            return 1;
        }

        string directory = Option(args, "--dir") ?? Directory.GetCurrentDirectory();
        string fileName = Option(args, "--name") ?? DeriveFileName(url);

        var manager = _services.GetRequiredService<IDownloadManager>();
        long id = await manager.EnqueueAsync(
            new EnqueueDownloadRequest { Url = url, DestinationDirectory = directory, FileName = fileName },
            cancellationToken).ConfigureAwait(false);

        await _output.WriteLineAsync($"Downloading {url} -> {Path.Combine(directory, fileName)}").ConfigureAwait(false);
        try
        {
            DownloadResult result = await manager.StartAsync(id, cancellationToken).ConfigureAwait(false);
            await _output.WriteLineAsync($"Done: {fileName} ({result.TotalBytes} bytes).").ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            await _output.WriteLineAsync("Cancelled.").ConfigureAwait(false);
            return 1;
        }
        catch (Exception ex)
        {
            await _output.WriteLineAsync($"Failed: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    private async Task<int> ListAsync(CancellationToken cancellationToken)
    {
        var repository = _services.GetRequiredService<IDownloadRepository>();
        IReadOnlyList<Download> downloads = await repository.GetAllAsync(cancellationToken).ConfigureAwait(false);
        if (downloads.Count == 0)
        {
            await _output.WriteLineAsync("No downloads.").ConfigureAwait(false);
            return 0;
        }

        foreach (Download download in downloads)
        {
            await _output.WriteLineAsync($"{download.Id}\t{download.Status}\t{download.Filename ?? download.Url}")
                .ConfigureAwait(false);
        }

        return 0;
    }

    private int PrintUsage()
    {
        _output.WriteLine("JustDownload CLI (jd) — a command-line front-end over the engine.");
        _output.WriteLine("Usage:");
        _output.WriteLine("  jd download <url> [--dir <folder>] [--name <file>]   Enqueue and run a download.");
        _output.WriteLine("  jd list                                              List downloads and their status.");
        _output.WriteLine("  jd help                                              Show this help.");
        return 0;
    }

    private int Unknown(string command)
    {
        _output.WriteLine($"Unknown command '{command}'. Run 'jd help' for usage.");
        return 1;
    }

    private static string? Option(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static bool TryGetUrl(string value, [NotNullWhen(true)] out Uri? url)
    {
        url = null;
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed)
            && parsed.Scheme is "http" or "https" or "ftp" or "ftps")
        {
            url = parsed;
            return true;
        }

        return false;
    }

    private static string DeriveFileName(Uri url)
    {
        string name = Path.GetFileName(url.AbsolutePath);
        return string.IsNullOrEmpty(name) ? "download" : Uri.UnescapeDataString(name);
    }
}
