using FluentAssertions;
using JustDownload.Cli;
using JustDownload.Core;
using JustDownload.Core.Data;
using JustDownload.Core.Data.Migrations;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Downloading;
using JustDownload.Core.Lifecycle;
using JustDownload.Tests.Transport;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.Cli;

/// <summary>
/// The command-line front-end (TASK-148): commands dispatch to the engine via DI (unit-tested with
/// substitutes), and a real <c>download</c> runs end-to-end through Core against a loopback server (AC0, D5).
/// </summary>
public sealed class CliRunnerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "jd-cli-" + Guid.NewGuid().ToString("N"));
    private readonly StringWriter _output = new();

    public CliRunnerTests() => Directory.CreateDirectory(_tempDir);

    private static ServiceProvider WithServices(IDownloadManager? manager = null, IDownloadRepository? repository = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(manager ?? Substitute.For<IDownloadManager>());
        services.AddSingleton(repository ?? Substitute.For<IDownloadRepository>());
        return services.BuildServiceProvider();
    }

    private static DownloadResult Result(long bytes) => new()
    {
        TotalBytes = bytes,
        FinalUri = new Uri("https://x.example/f"),
        FileName = "f",
        SingleConnection = false,
        InitialSegments = 4,
        Steals = 0,
    };

    [Fact]
    public async Task NoArgs_PrintsUsage()
    {
        int code = await new CliRunner(WithServices(), _output).RunAsync([]);

        code.Should().Be(0);
        _output.ToString().Should().Contain("Usage:");
    }

    [Fact]
    public async Task Unknown_Command_ReturnsNonZero()
    {
        int code = await new CliRunner(WithServices(), _output).RunAsync(["frobnicate"]);

        code.Should().Be(1);
        _output.ToString().Should().Contain("Unknown command");
    }

    [Fact]
    public async Task Download_InvalidUrl_ReturnsError()
    {
        int code = await new CliRunner(WithServices(), _output).RunAsync(["download", "not-a-url"]);

        code.Should().Be(1);
        _output.ToString().Should().Contain("valid http");
    }

    [Fact]
    public async Task Download_EnqueuesAndStarts_ThroughTheManager()
    {
        var manager = Substitute.For<IDownloadManager>();
        manager.EnqueueAsync(Arg.Any<EnqueueDownloadRequest>(), Arg.Any<CancellationToken>()).Returns(9L);
        manager.StartAsync(9L, Arg.Any<CancellationToken>()).Returns(Result(2048));

        int code = await new CliRunner(WithServices(manager), _output)
            .RunAsync(["download", "https://host.example/file.bin", "--dir", _tempDir, "--name", "out.bin"]);

        code.Should().Be(0);
        await manager.Received(1).EnqueueAsync(
            Arg.Is<EnqueueDownloadRequest>(r =>
                r.FileName == "out.bin" && r.DestinationDirectory == _tempDir
                && r.Url == new Uri("https://host.example/file.bin")),
            Arg.Any<CancellationToken>());
        await manager.Received(1).StartAsync(9L, Arg.Any<CancellationToken>());
        _output.ToString().Should().Contain("Done").And.Contain("2048");
    }

    [Fact]
    public async Task List_PrintsDownloads()
    {
        var repo = Substitute.For<IDownloadRepository>();
        repo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            new Download { Id = 1, Url = "u", Status = "complete", Filename = "a.bin" },
        });

        int code = await new CliRunner(WithServices(repository: repo), _output).RunAsync(["list"]);

        code.Should().Be(0);
        _output.ToString().Should().Contain("a.bin").And.Contain("complete");
    }

    [Fact]
    public async Task Download_RealEndToEnd_ThroughCore_WritesTheFile()
    {
        // The headline AC: the CLI enqueues and runs a real download through the composed Core engine (D5).
        byte[] body = new byte[64 * 1024];
        for (int i = 0; i < body.Length; i++)
        {
            body[i] = (byte)((i * 31 + 7) % 256);
        }

        await using var origin = new LoopbackHttpServer { Body = body, SupportRanges = true };

        var pathProvider = Substitute.For<IDatabasePathProvider>();
        pathProvider.DatabaseDirectory.Returns(_tempDir);
        pathProvider.DatabasePath.Returns(Path.Combine(_tempDir, "cli.db"));

        var services = new ServiceCollection();
        services.AddSingleton(pathProvider);
        services.AddJustDownloadCore();
        using ServiceProvider provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IMigrationRunner>().MigrateAsync();

        int code = await new CliRunner(provider, _output)
            .RunAsync(["download", origin.Url("f.bin").ToString(), "--dir", _tempDir, "--name", "cli-out.bin"]);

        code.Should().Be(0, _output.ToString());
        (await File.ReadAllBytesAsync(Path.Combine(_tempDir, "cli-out.bin"))).Should().Equal(body);
    }

    public void Dispose()
    {
        _output.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
