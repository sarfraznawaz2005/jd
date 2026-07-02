using System.Collections.Concurrent;
using System.Security.Cryptography;
using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Downloading;
using JustDownload.Core.Security;
using JustDownload.Core.Transport.Auth;
using JustDownload.Core.Transport.Proxy;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JustDownload.Tests.Transport;

/// <summary>
/// HTTP and proxy authentication (TASK-035, US-7): Basic and Digest succeed for the origin and Basic for
/// the proxy using .NET's real challenge-response with our credentials (AC0); a missing/wrong credential
/// surfaces as an <see cref="AuthenticationRequiredException"/> so the UI re-prompts (AC2); and the
/// credential store keeps the password only in the OS keychain (AC1). NTLM uses the same handler credential
/// mechanism, its plumbing is asserted directly, and its real NTLMv2 handshake against a server is validated
/// end-to-end (TASK-110, via <see cref="LoopbackNtlmAuthServer"/>).
/// </summary>
public sealed class AuthTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jd-auth-" + Guid.NewGuid().ToString("N"));

    public AuthTests() => Directory.CreateDirectory(_dir);

    private static byte[] Bytes(int n)
    {
        var d = new byte[n];
        for (int i = 0; i < n; i++)
        {
            d[i] = (byte)((i * 53 + 17) % 256);
        }

        return d;
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddJustDownloadTransport();
        services.AddJustDownloadDownloading();
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(AuthScheme.Basic)]
    [InlineData(AuthScheme.Digest)]
    public async Task AuthenticatedOrigin_WithCorrectCredentials_Succeeds(AuthScheme scheme)
    {
        byte[] body = Bytes(20_000);
        await using var server = new LoopbackAuthServer(scheme, "alice", "s3cret", body);
        using ServiceProvider provider = BuildProvider();
        var downloader = provider.GetRequiredService<ISegmentedDownloader>();
        string dest = Path.Combine(_dir, $"auth-{scheme}.bin");

        await downloader.DownloadAsync(new DownloadRequest
        {
            Url = server.Url("file.bin"),
            DestinationPath = dest,
            Connections = 1,
            Credentials = new NetworkCredentials("alice", "s3cret"),
        });

        (await File.ReadAllBytesAsync(dest)).Should().Equal(body, $"{scheme} auth should succeed and download fully");
    }

    [Fact]
    public async Task AuthenticatedOrigin_WithoutCredentials_ThrowsAuthRequired()
    {
        await using var server = new LoopbackAuthServer(AuthScheme.Basic, "alice", "s3cret", Bytes(100));
        using ServiceProvider provider = BuildProvider();
        var downloader = provider.GetRequiredService<ISegmentedDownloader>();

        Func<Task> act = () => downloader.DownloadAsync(new DownloadRequest
        {
            Url = server.Url("file.bin"),
            DestinationPath = Path.Combine(_dir, "noauth.bin"),
            Connections = 1,
        });

        AuthenticationRequiredException ex = (await act.Should().ThrowAsync<AuthenticationRequiredException>()).Which;
        ex.IsProxy.Should().BeFalse();
        ex.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task AuthenticatedOrigin_WithWrongCredentials_ThrowsAuthRequired_ToReprompt()
    {
        await using var server = new LoopbackAuthServer(AuthScheme.Digest, "alice", "s3cret", Bytes(100));
        using ServiceProvider provider = BuildProvider();
        var downloader = provider.GetRequiredService<ISegmentedDownloader>();

        Func<Task> act = () => downloader.DownloadAsync(new DownloadRequest
        {
            Url = server.Url("file.bin"),
            DestinationPath = Path.Combine(_dir, "wrong.bin"),
            Connections = 1,
            Credentials = new NetworkCredentials("alice", "WRONG"),
        });

        await act.Should().ThrowAsync<AuthenticationRequiredException>("wrong credentials must re-prompt (AC2)");
    }

    [Fact]
    public async Task AuthenticatedProxy_WithCredentials_RoutesTraffic()
    {
        byte[] body = Bytes(16_000);
        await using var origin = new LoopbackHttpServer { Body = body, SupportRanges = true };
        await using var proxy = new LoopbackHttpProxy { RequiredBasicAuth = "puser:ppass" };
        using ServiceProvider provider = BuildProvider();
        var downloader = provider.GetRequiredService<ISegmentedDownloader>();
        string dest = Path.Combine(_dir, "proxy-auth.bin");

        await downloader.DownloadAsync(new DownloadRequest
        {
            Url = origin.Url("f.bin"),
            DestinationPath = dest,
            Connections = 1,
            Proxy = new ProxyConfiguration(
                ProxyKind.Http, "127.0.0.1", proxy.Port, new NetworkCredentials("puser", "ppass")),
        });

        (await File.ReadAllBytesAsync(dest)).Should().Equal(body);
        proxy.RequestedUrls.Should().NotBeEmpty("the authenticated proxy routed the traffic");
    }

    [Fact]
    public async Task AuthenticatedProxy_Digest_WithCredentials_RoutesTraffic()
    {
        byte[] body = Bytes(16_000);
        await using var origin = new LoopbackHttpServer { Body = body, SupportRanges = true };
        await using var proxy = new LoopbackHttpProxy { RequiredDigestAuth = "puser:ppass" };
        using ServiceProvider provider = BuildProvider();
        var downloader = provider.GetRequiredService<ISegmentedDownloader>();
        string dest = Path.Combine(_dir, "proxy-digest.bin");

        await downloader.DownloadAsync(new DownloadRequest
        {
            Url = origin.Url("f.bin"),
            DestinationPath = dest,
            Connections = 1,
            Proxy = new ProxyConfiguration(
                ProxyKind.Http, "127.0.0.1", proxy.Port, new NetworkCredentials("puser", "ppass")),
        });

        (await File.ReadAllBytesAsync(dest)).Should().Equal(body);
        proxy.RequestedUrls.Should().NotBeEmpty("the Digest-authenticated proxy routed the traffic");
    }

    [Fact]
    public async Task AuthenticatedProxy_WithoutCredentials_ThrowsAuthRequired_ForProxy()
    {
        await using var origin = new LoopbackHttpServer { Body = Bytes(100), SupportRanges = true };
        await using var proxy = new LoopbackHttpProxy { RequiredBasicAuth = "puser:ppass" };
        using ServiceProvider provider = BuildProvider();
        var downloader = provider.GetRequiredService<ISegmentedDownloader>();

        Func<Task> act = () => downloader.DownloadAsync(new DownloadRequest
        {
            Url = origin.Url("f.bin"),
            DestinationPath = Path.Combine(_dir, "proxy-noauth.bin"),
            Connections = 1,
            Proxy = new ProxyConfiguration(ProxyKind.Http, "127.0.0.1", proxy.Port),
        });

        AuthenticationRequiredException ex = (await act.Should().ThrowAsync<AuthenticationRequiredException>()).Which;
        ex.IsProxy.Should().BeTrue("a 407 surfaces as a proxy auth challenge");
        ex.StatusCode.Should().Be(407);
    }

    [Fact]
    public void NtlmCredentials_WithDomain_RequireDedicatedHandler()
    {
        // NTLM/Negotiate credentials (with a domain) are carried on a dedicated handler that .NET uses to
        // answer the challenge — the same mechanism as Basic/Digest. The full handshake against a real
        // server is validated below (TASK-110).
        var profile = new ConnectionProfile(
            ProxyConfiguration.None, new NetworkCredentials("alice", "s3cret", "CORP"));

        profile.RequiresDedicatedHandler.Should().BeTrue();
        profile.Credentials!.Domain.Should().Be("CORP");
    }

    // --- TASK-110: a real NTLMv2 handshake, not just the handler plumbing above -------------------

    [Fact]
    public async Task NtlmAuthenticatedOrigin_WithCorrectCredentials_Succeeds()
    {
        byte[] body = Bytes(20_000);
        await using var server = new LoopbackNtlmAuthServer("alice", "s3cret", "CORP", body);
        using ServiceProvider provider = BuildProvider();
        var downloader = provider.GetRequiredService<ISegmentedDownloader>();
        string dest = Path.Combine(_dir, "ntlm.bin");

        await downloader.DownloadAsync(new DownloadRequest
        {
            Url = server.Url("file.bin"),
            DestinationPath = dest,
            Connections = 1,
            Credentials = new NetworkCredentials("alice", "s3cret", "CORP"),
        });

        (await File.ReadAllBytesAsync(dest)).Should().Equal(body, "a real NTLMv2 handshake should authenticate and download fully");
    }

    [Fact]
    public async Task NtlmAuthenticatedOrigin_WithWrongPassword_ThrowsAuthRequired()
    {
        await using var server = new LoopbackNtlmAuthServer("alice", "s3cret", "CORP", Bytes(100));
        using ServiceProvider provider = BuildProvider();
        var downloader = provider.GetRequiredService<ISegmentedDownloader>();

        Func<Task> act = () => downloader.DownloadAsync(new DownloadRequest
        {
            Url = server.Url("file.bin"),
            DestinationPath = Path.Combine(_dir, "ntlm-wrong.bin"),
            Connections = 1,
            Credentials = new NetworkCredentials("alice", "WRONG", "CORP"),
        });

        await act.Should().ThrowAsync<AuthenticationRequiredException>(
            "the server cryptographically verifies the NTLMv2 response, so a wrong password genuinely fails");
    }

    // --- AC1: credentials stored only in the OS keychain -----------------------------------------

    private sealed class InMemorySecretStore : ISecretStore
    {
        public ConcurrentDictionary<string, string> Vault { get; } = new();

        public Task<string> StoreAsync(string secret, CancellationToken ct = default)
        {
            string reference = "ref:" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret + Vault.Count)));
            Vault[reference] = secret;
            return Task.FromResult(reference);
        }

        public Task<string?> RetrieveAsync(string secretRef, CancellationToken ct = default) =>
            Task.FromResult(Vault.GetValueOrDefault(secretRef));

        public Task<bool> DeleteAsync(string secretRef, CancellationToken ct = default) =>
            Task.FromResult(Vault.TryRemove(secretRef, out _));
    }

    [Fact]
    public async Task CredentialStore_KeepsPasswordOnlyInKeychain()
    {
        var vault = new InMemorySecretStore();
        var store = new CredentialStore(vault);
        var credentials = new NetworkCredentials("alice", "s3cret", "CORP");

        StoredCredential stored = await store.SaveAsync(credentials);

        stored.Username.Should().Be("alice");
        stored.Domain.Should().Be("CORP");
        stored.SecretRef.Should().NotContain("s3cret", "the persistable reference is not the password");
        vault.Vault.Values.Should().ContainSingle().Which.Should().Be("s3cret", "the password lives only in the keychain");

        NetworkCredentials? loaded = await store.LoadAsync(stored);
        loaded.Should().Be(credentials, "loading resolves the password back from the keychain");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
