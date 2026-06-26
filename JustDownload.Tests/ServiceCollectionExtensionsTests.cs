using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests;

/// <summary>
/// Verifies the single Core composition root (TASK-017): services register, resolve, and are
/// mockable through their interfaces.
/// </summary>
public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddJustDownloadCore_RegistersAndResolves_CoreServices()
    {
        using ServiceProvider provider = new ServiceCollection()
            .AddJustDownloadCore()
            .BuildServiceProvider();

        provider.GetRequiredService<IAppInfoProvider>().Name.Should().Be("JustDownload");
        provider.GetRequiredService<IClock>().UtcNow.Should().BeAfter(DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void AddJustDownloadCore_ReturnsSameCollection_ForChaining()
    {
        var services = new ServiceCollection();

        IServiceCollection result = services.AddJustDownloadCore();

        result.Should().BeSameAs(services);
    }

    [Fact]
    public void AddJustDownloadCore_AllowsSubstitution_WithNSubstitute()
    {
        IClock fakeClock = Substitute.For<IClock>();
        fakeClock.UtcNow.Returns(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var services = new ServiceCollection();
        // Pre-register the substitute; AddJustDownloadCore uses TryAdd so the mock wins.
        services.AddSingleton(fakeClock);
        services.AddJustDownloadCore();

        using ServiceProvider provider = services.BuildServiceProvider();

        IClock resolved = provider.GetRequiredService<IClock>();
        resolved.Should().BeSameAs(fakeClock);
        resolved.UtcNow.Should().Be(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }
}
