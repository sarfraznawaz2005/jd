using System.Security.Cryptography.X509Certificates;
using JustDownload.Core.Transport.Ftp;

namespace JustDownload.Tests.Transport;

/// <summary>
/// A test-only <see cref="IFtpConnectionFactory"/> (TASK-112) that pins FTPS certificate validation to one
/// specific known certificate's raw bytes, so <see cref="LoopbackFtpServer"/>'s runtime-generated self-signed
/// certificate can be trusted for a real TLS handshake in tests without installing anything into an OS trust
/// store. This is certificate <b>pinning</b>, not disabling validation: any certificate other than the exact
/// pinned one is still rejected. It is never registered by <c>AddJustDownloadTransport</c> — the production
/// path (<see cref="FluentFtpConnectionFactory"/>) has no pinning capability and is unchanged by this class.
/// </summary>
internal sealed class PinnedCertificateFtpConnectionFactory : IFtpConnectionFactory
{
    private readonly byte[] _pinnedRawData;

    public PinnedCertificateFtpConnectionFactory(X509Certificate2 pinnedCertificate)
    {
        ArgumentNullException.ThrowIfNull(pinnedCertificate);
        _pinnedRawData = pinnedCertificate.GetRawCertData();
    }

    public IFtpConnection Create(Uri uri) =>
        new FluentFtpConnection(uri, (certificate, _, _) => certificate.GetRawCertData().AsSpan().SequenceEqual(_pinnedRawData));
}
