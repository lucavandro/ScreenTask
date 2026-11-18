namespace ScreenTask.Core.Capture
{
    /// <summary>Transfers ownership through one pending slot; superseded frames are disposed.</summary>
    internal sealed class LatestFrameQueue<T> : IDisposable where T : class, IDisposable
    {
        private readonly object _gate = new();
        private readonly AutoResetEvent _available = new(false);
        private readonly CancellationToken _token;
        private readonly WaitHandle[] _waitHandles;
        private T? _pending;
        private bool _completed;
        private bool _disposed;
        private long _dropped;

        public LatestFrameQueue(CancellationToken token)
        {
            _token = token;
            _waitHandles = new WaitHandle[] { token.WaitHandle, _available };
        }

        public int PendingCount { get { lock (_gate) return _pending == null ? 0 : 1; } }
        public long DroppedCount => Interlocked.Read(ref _dropped);
        public bool IsCompleted { get { lock (_gate) return _completed && _pending == null; } }

        // Takes ownership even if the queue has already been completed.
        public void Publish(T frame)
        {
            T? discarded;
            lock (_gate)
            {
                if (_completed || _disposed || _token.IsCancellationRequested)
                {
                    discarded = frame;
                }
                else
                {
                    discarded = _pending;
                    _pending = frame;
                    if (discarded != null) Interlocked.Increment(ref _dropped);
                    _available.Set();
                }
            }
            discarded?.Dispose();
        }

        public T? Take(int timeoutMilliseconds)
        {
            lock (_gate)
            {
                if (_disposed || _token.IsCancellationRequested) return null;
                if (_pending != null) return TakePending();
                if (_completed) return null;
            }
            if (WaitHandle.WaitAny(_waitHandles, timeoutMilliseconds) != 1) return null;
            lock (_gate)
            {
                if (_disposed || _token.IsCancellationRequested) return null;
                return TakePending();
            }
        }

        private T? TakePending()
        {
            var frame = _pending;
            _pending = null;
            return frame;
        }

        public void Wake()
        {
            lock (_gate) { if (!_disposed) _available.Set(); }
        }

        public void Complete()
        {
            lock (_gate)
            {
                _completed = true;
                if (!_disposed) _available.Set();
            }
        }

        // The owner must join producer/consumer workers before disposing their wait handles.
        public void Dispose()
        {
            T? discarded;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = _completed = true;
                discarded = TakePending();
            }
            discarded?.Dispose();
            _available.Dispose();
        }
    }
}
