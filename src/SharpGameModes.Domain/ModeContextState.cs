using SharpGameModes.Contracts;

namespace SharpGameModes.Domain;

public sealed class ModeContextState : IModeContext, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<long, Action<ModeContextSnapshot>> _listeners = [];
    private ModeContextSnapshot? _current;
    private long _nextListenerId;
    private bool _disposed;

    public ModeContextSnapshot? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public ModeContextSnapshot Activate(MapSelection selection, string source)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        Action<ModeContextSnapshot>[] listeners;
        ModeContextSnapshot snapshot;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_current is not null && _current.Selection == selection)
            {
                return _current;
            }

            snapshot = new ModeContextSnapshot(
                selection,
                (_current?.Generation ?? 0) + 1,
                DateTimeOffset.UtcNow,
                source.Trim());
            _current = snapshot;
            listeners = [.. _listeners.Values];
        }

        foreach (var listener in listeners)
        {
            listener(snapshot);
        }

        return snapshot;
    }

    public IDisposable Subscribe(Action<ModeContextSnapshot> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var id = ++_nextListenerId;
            _listeners.Add(id, listener);
            return new Subscription(this, id);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _listeners.Clear();
        }
    }

    private void Unsubscribe(long id)
    {
        lock (_gate)
        {
            _listeners.Remove(id);
        }
    }

    private sealed class Subscription(ModeContextState owner, long id) : IDisposable
    {
        private ModeContextState? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Unsubscribe(id);
        }
    }
}
