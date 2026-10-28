namespace Pinboard.App.Services;

/// <summary>Owns cancellable generations; an older completion cannot publish results.</summary>
public sealed class SearchRequestController : IDisposable
{
    private readonly object _gate = new();
    private SearchRequest? _current;
    private long _generation;
    private bool _disposed;

    public SearchRequest Begin(string query, CancellationToken lifetimeToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _current?.Cancel();
            _current = new SearchRequest(this, ++_generation, query,
                CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken));
            return _current;
        }
    }

    public bool IsCurrent(SearchRequest request)
    {
        lock (_gate)
        {
            return !_disposed && ReferenceEquals(_current, request) && !request.Token.IsCancellationRequested;
        }
    }

    private void Finish(SearchRequest request)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_current, request)) _current = null;
            request.DisposeSource();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _current?.Cancel();
            _current = null;
        }
    }

    public sealed class SearchRequest : IDisposable
    {
        private readonly SearchRequestController _owner;
        private readonly CancellationTokenSource _source;
        private bool _disposed;

        internal SearchRequest(SearchRequestController owner, long generation, string query, CancellationTokenSource source)
        {
            _owner = owner;
            _source = source;
            Generation = generation;
            Query = query;
            Token = source.Token;
        }

        public long Generation { get; }
        public string Query { get; }
        public CancellationToken Token { get; }
        internal void Cancel() => _source.Cancel();
        internal void DisposeSource()
        {
            if (_disposed) return;
            _disposed = true;
            _source.Dispose();
        }
        public void Dispose() => _owner.Finish(this);
    }
}
