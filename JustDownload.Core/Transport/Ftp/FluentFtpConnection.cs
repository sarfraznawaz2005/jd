using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using FluentFTP;

namespace JustDownload.Core.Transport.Ftp;

/// <summary>
/// The real <see cref="IFtpConnection"/> over FluentFTP's <see cref="AsyncFtpClient"/> (TASK-033): passive
/// data connections, <c>REST</c>-based resume, and explicit/implicit FTPS auto-negotiation. Credentials
/// come from the URL's user-info (anonymous when absent). This thin wrapper holds no testable logic itself
/// — the transport's behaviour is tested through the <see cref="IFtpConnection"/> seam; this is verified
/// against a real server by the fixture task.
/// </summary>
internal sealed class FluentFtpConnection : IFtpConnection
{
    private readonly AsyncFtpClient _client;

    /// <param name="uri">The <c>ftp://</c>/<c>ftps://</c> URL (host, port, credentials and scheme).</param>
    /// <param name="testCertificateValidator">
    /// Test-only hook (TASK-112): when non-null, wired to FluentFTP's <c>ValidateCertificate</c> event so a
    /// test fixture can pin one specific known certificate instead of trusting the OS chain. Never supplied
    /// by <see cref="FluentFtpConnectionFactory"/> (the DI-registered production factory), so production
    /// FTPS connections always fall through to normal chain validation — an unhandled certificate error
    /// still aborts the connection exactly as before this parameter existed.
    /// </param>
    internal FluentFtpConnection(Uri uri, Func<X509Certificate, X509Chain?, SslPolicyErrors, bool>? testCertificateValidator = null)
    {
        ArgumentNullException.ThrowIfNull(uri);

        (string user, string password) = ParseCredentials(uri);
        int port = uri.IsDefaultPort ? 0 : uri.Port;
        _client = new AsyncFtpClient(uri.Host, user, password, port);

        // Passive mode and resume-friendly defaults; FTPS when the scheme asks for it.
        _client.Config.DataConnectionType = FtpDataConnectionType.AutoPassive;
        if (uri.Scheme.Equals("ftps", StringComparison.OrdinalIgnoreCase))
        {
            _client.Config.EncryptionMode = FtpEncryptionMode.Auto;
            _client.Config.ValidateAnyCertificate = false;
            if (testCertificateValidator is not null)
            {
                _client.ValidateCertificate += (_, e) =>
                    e.Accept = testCertificateValidator(e.Certificate, e.Chain, e.PolicyErrors);
            }
        }
    }

    public Task ConnectAsync(CancellationToken cancellationToken) =>
        _client.Connect(cancellationToken);

    public Task<long> GetFileSizeAsync(string path, CancellationToken cancellationToken) =>
        _client.GetFileSize(path, -1, cancellationToken);

    public async Task<Stream> OpenReadAsync(string path, long restartPosition, CancellationToken cancellationToken) =>
        // checkIfFileExists:false — the size was already probed, so skip the extra round-trip.
        await _client.OpenRead(path, FtpDataType.Binary, restartPosition, false, cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<string>> ListNamesAsync(string directoryPath, CancellationToken cancellationToken)
    {
        string[] names = await _client.GetNameListing(directoryPath, cancellationToken).ConfigureAwait(false);
        return names;
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync().ConfigureAwait(false);
    }

    private static (string User, string Password) ParseCredentials(Uri uri)
    {
        if (string.IsNullOrEmpty(uri.UserInfo))
        {
            return ("anonymous", "anonymous@");
        }

        string[] parts = uri.UserInfo.Split(':', 2);
        string user = WebUtility.UrlDecode(parts[0]);
        string password = parts.Length > 1 ? WebUtility.UrlDecode(parts[1]) : string.Empty;
        return (user, password);
    }
}

/// <summary>Default <see cref="IFtpConnectionFactory"/> creating <see cref="FluentFtpConnection"/> instances.</summary>
internal sealed class FluentFtpConnectionFactory : IFtpConnectionFactory
{
    public IFtpConnection Create(Uri uri) => new FluentFtpConnection(uri);
}
