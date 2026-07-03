using System.IO.Pipes;
using System.Text;

namespace JustDownload.App.Services;

/// <summary>
/// Ensures a single running instance and forwards a second launch's arguments (URLs) to it (TASK-061 AC2).
/// The first instance claims a named mutex and listens on a named pipe; a later launch detects the existing
/// owner, sends its arguments over the pipe, and exits — so clicking a download link always lands in the one
/// window. The pipe transport is cross-platform (named pipes map to a Unix domain socket on macOS/Linux).
/// </summary>
public interface ISingleInstanceCoordinator : IDisposable
{
    /// <summary>Whether this process owns the single instance (claimed the mutex).</summary>
    bool IsOwner { get; }

    /// <summary>Raised on the owning instance when another launch forwards its arguments.</summary>
    event EventHandler<IReadOnlyList<string>>? ArgumentsReceived;

    /// <summary>
    /// Tries to become the single instance. Returns <see langword="true"/> if this process is the owner
    /// (and starts listening for forwarded arguments); <see langword="false"/> if another instance owns it.
    /// </summary>
    bool TryClaimOwnership();

    /// <summary>Forwards <paramref name="arguments"/> to the already-running owner instance.</summary>
    Task ForwardArgumentsAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
}

/// <summary>Default <see cref="ISingleInstanceCoordinator"/> over a named mutex + named pipe (TASK-061).</summary>
public sealed class SingleInstanceCoordinator : ISingleInstanceCoordinator
{
    private readonly string _name;
    private readonly CancellationTokenSource _cts = new();
    private Mutex? _mutex;
    private Task? _listener;
    private bool _disposed;

    /// <summary>Creates a coordinator using <paramref name="name"/> for the mutex and pipe (per app + user).</summary>
    public SingleInstanceCoordinator(string name = "JustDownload.SingleInstance")
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _name = name;
    }

    public bool IsOwner { get; private set; }

    public event EventHandler<IReadOnlyList<string>>? ArgumentsReceived;

    public bool TryClaimOwnership()
    {
        _mutex = new Mutex(initiallyOwned: true, $"{_name}.mutex", out bool createdNew);
        IsOwner = createdNew;
        if (IsOwner)
        {
            StartListening();
        }

        return IsOwner;
    }

    /// <summary>Starts the pipe listener (used by the owner; exposed for testing the transport directly).</summary>
    public void StartListening()
    {
        _listener ??= Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    public async Task ForwardArgumentsAsync(
        IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        await using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000, cancellationToken).ConfigureAwait(false);

        byte[] payload = Encoding.UTF8.GetBytes(string.Join('\n', arguments));
        await client.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await client.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private string PipeName => $"{_name}.pipe";

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8);
                string text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                string[] args = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (args.Length > 0)
                {
                    ArgumentsReceived?.Invoke(this, args);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                // A broken/incomplete connection — keep listening for the next launch.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        try
        {
            _listener?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
        }

        // Deliberately no ReleaseMutex() here (TASK-173): it requires the exact thread that acquired the
        // mutex in TryClaimOwnership(), which async continuations elsewhere in this class's lifetime (or a
        // caller awaiting before disposing) can't guarantee — release throws ApplicationException
        // ("...called from an unsynchronized block of code") from any other thread. Disposing the handle is
        // sufficient and thread-agnostic: it's this instance's only handle to the named mutex, so closing it
        // destroys the underlying OS object (or, if somehow still referenced, abandons it) either way making
        // it immediately available to the next TryClaimOwnership() caller, same end state as an explicit
        // release.
        _mutex?.Dispose();
        _cts.Dispose();
    }
}
