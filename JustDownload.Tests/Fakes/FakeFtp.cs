using System.Collections.Concurrent;
using JustDownload.Core.Transport.Ftp;

namespace JustDownload.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IFtpConnectionFactory"/> for FTP transport tests (TASK-033): every connection it
/// creates serves the same configured <see cref="Data"/>, supports <c>REST</c> resume by slicing from the
/// restart offset, and records the restart positions and concurrency so a test can prove segmentation and
/// resume used REST. No sockets — the concrete FluentFTP client is exercised against a real server by the
/// fixture task.
/// </summary>
internal sealed class FakeFtpFactory : IFtpConnectionFactory
{
    private int _current;
    private int _peak;

    /// <summary>The file bytes every connection serves.</summary>
    public byte[] Data { get; set; } = [];

    /// <summary>Names returned by a directory listing.</summary>
    public IReadOnlyList<string> Names { get; set; } = [];

    /// <summary>Every <c>REST</c> restart offset requested across all connections.</summary>
    public ConcurrentBag<long> Restarts { get; } = [];

    /// <summary>The peak number of concurrently-open connections.</summary>
    public int PeakConcurrency => Volatile.Read(ref _peak);

    public IFtpConnection Create(Uri uri) => new FakeFtpConnection(this);

    private void Enter()
    {
        int now = Interlocked.Increment(ref _current);
        int peak;
        do
        {
            peak = Volatile.Read(ref _peak);
            if (now <= peak)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _peak, now, peak) != peak);
    }

    private void Leave() => Interlocked.Decrement(ref _current);

    private sealed class FakeFtpConnection : IFtpConnection
    {
        private readonly FakeFtpFactory _owner;
        private bool _entered;

        public FakeFtpConnection(FakeFtpFactory owner) => _owner = owner;

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            _owner.Enter();
            _entered = true;
            return Task.CompletedTask;
        }

        public Task<long> GetFileSizeAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult<long>(_owner.Data.Length);

        public Task<Stream> OpenReadAsync(string path, long restartPosition, CancellationToken cancellationToken)
        {
            _owner.Restarts.Add(restartPosition);
            return Task.FromResult<Stream>(new MemoryStream(_owner.Data[(int)restartPosition..], writable: false));
        }

        public Task<IReadOnlyList<string>> ListNamesAsync(string directoryPath, CancellationToken cancellationToken) =>
            Task.FromResult(_owner.Names);

        public ValueTask DisposeAsync()
        {
            if (_entered)
            {
                _owner.Leave();
                _entered = false;
            }

            return ValueTask.CompletedTask;
        }
    }
}
