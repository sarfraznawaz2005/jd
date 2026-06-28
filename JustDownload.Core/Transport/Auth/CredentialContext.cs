namespace JustDownload.Core.Transport.Auth;

/// <summary>
/// Default <see cref="ICredentialContext"/> (TASK-035): an <see cref="AsyncLocal{T}"/> holding the origin
/// credentials for the current download flow, so concurrent downloads keep their own credentials and a
/// scope set at the download's entry reaches its probe and segment workers.
/// </summary>
internal sealed class CredentialContext : ICredentialContext
{
    private readonly AsyncLocal<NetworkCredentials?> _current = new();

    public NetworkCredentials? Effective => _current.Value;

    public IDisposable BeginScope(NetworkCredentials? credentials)
    {
        if (credentials is null)
        {
            return NullScope.Instance;
        }

        NetworkCredentials? previous = _current.Value;
        _current.Value = credentials;
        return new Scope(this, previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly CredentialContext _owner;
        private readonly NetworkCredentials? _previous;

        public Scope(CredentialContext owner, NetworkCredentials? previous)
        {
            _owner = owner;
            _previous = previous;
        }

        public void Dispose() => _owner._current.Value = _previous;
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
