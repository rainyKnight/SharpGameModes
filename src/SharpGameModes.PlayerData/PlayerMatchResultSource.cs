using SharpGameModes.Contracts;

namespace SharpGameModes.PlayerData;

internal sealed class PlayerMatchResultSource : IPlayerMatchResultSource, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<long, Action<IReadOnlyList<PlayerMatchResultSnapshot>>> _listeners = [];
    private long _nextListenerId;
    private bool _disposed;

    public IDisposable Subscribe(Action<IReadOnlyList<PlayerMatchResultSnapshot>> listener)
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

    public IReadOnlyList<Exception> Publish(IReadOnlyList<PlayerMatchResultSnapshot> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        Action<IReadOnlyList<PlayerMatchResultSnapshot>>[] listeners;
        lock (_gate)
        {
            if (_disposed)
            {
                return [];
            }

            listeners = [.. _listeners.Values];
        }

        List<Exception>? errors = null;
        foreach (var listener in listeners)
        {
            try
            {
                listener(results);
            }
            catch (Exception exception)
            {
                errors ??= [];
                errors.Add(exception);
            }
        }

        return errors ?? [];
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

    private sealed class Subscription(PlayerMatchResultSource owner, long id) : IDisposable
    {
        private PlayerMatchResultSource? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Unsubscribe(id);
        }
    }
}
