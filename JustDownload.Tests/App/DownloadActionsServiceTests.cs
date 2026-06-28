using FluentAssertions;
using JustDownload.App.Services;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Lifecycle;
using JustDownload.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>
/// The action surface's removal path (TASK-091): deleting a download also removes any keychain-stored cookies
/// for it, so no secret is orphaned in the OS vault when the record goes (§5).
/// </summary>
public sealed class DownloadActionsServiceTests
{
    [Fact]
    public async Task RemoveAsync_DeletesKeychainCookieSecret_ThenRecord()
    {
        var manager = Substitute.For<IDownloadManager>();
        var repository = Substitute.For<IDownloadRepository>();
        var secrets = Substitute.For<ISecretStore>();
        repository.GetAsync(7, Arg.Any<CancellationToken>()).Returns(Task.FromResult<Download?>(new Download
        {
            Url = "https://example.com/x",
            Status = DownloadStatusCodes.Paused,
            CookieSecretRef = "ref-7",
        }));

        using var service = new DownloadActionsService(
            manager, repository, secrets, NullLogger<DownloadActionsService>.Instance);

        await service.RemoveAsync(7);

        await secrets.Received(1).DeleteAsync("ref-7", Arg.Any<CancellationToken>());
        await repository.Received(1).DeleteAsync(7, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveAsync_WithoutCookies_SkipsSecretDelete()
    {
        var manager = Substitute.For<IDownloadManager>();
        var repository = Substitute.For<IDownloadRepository>();
        var secrets = Substitute.For<ISecretStore>();
        repository.GetAsync(3, Arg.Any<CancellationToken>()).Returns(Task.FromResult<Download?>(new Download
        {
            Url = "https://example.com/y",
            Status = DownloadStatusCodes.Paused,
        }));

        using var service = new DownloadActionsService(
            manager, repository, secrets, NullLogger<DownloadActionsService>.Instance);

        await service.RemoveAsync(3);

        await secrets.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await repository.Received(1).DeleteAsync(3, Arg.Any<CancellationToken>());
    }
}
