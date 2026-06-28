namespace JustDownload.Core.Transport.Ftp;

/// <summary>
/// A minimal abstraction over an FTP control connection (TASK-033), exposing only what the FTP transport
/// needs: connect, query a file's size, open a (optionally resumed) read stream, and list a directory's
/// names to derive a file name. Keeping this surface small lets the transport's logic — REST resume, size
/// probing, listing-based file names — be unit-tested with a fake, independent of the concrete FluentFTP
/// client (which is exercised against a real server by the fixture task).
/// </summary>
internal interface IFtpConnection : IAsyncDisposable
{
    /// <summary>Opens the control connection (and TLS for FTPS), authenticating as configured.</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>The remote file size in bytes, or <c>-1</c> when the server does not report it.</summary>
    Task<long> GetFileSizeAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a read stream for <paramref name="path"/>, resuming from <paramref name="restartPosition"/> via
    /// <c>REST</c> when it is greater than zero (TASK-033 AC1).
    /// </summary>
    Task<Stream> OpenReadAsync(string path, long restartPosition, CancellationToken cancellationToken);

    /// <summary>Lists the file names in <paramref name="directoryPath"/> (used to derive a name — AC2).</summary>
    Task<IReadOnlyList<string>> ListNamesAsync(string directoryPath, CancellationToken cancellationToken);
}

/// <summary>Creates an <see cref="IFtpConnection"/> for a given <c>ftp://</c>/<c>ftps://</c> URL (TASK-033).</summary>
internal interface IFtpConnectionFactory
{
    /// <summary>Builds a connection (host, port, credentials and TLS taken from <paramref name="uri"/>).</summary>
    IFtpConnection Create(Uri uri);
}
