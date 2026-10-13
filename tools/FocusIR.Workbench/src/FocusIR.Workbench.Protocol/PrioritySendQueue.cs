namespace FocusIR.Workbench.Protocol;

internal sealed class PrioritySendQueue : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<byte[]> _urgent = new();
    private readonly Queue<byte[]> _normal = new();
    private readonly SemaphoreSlim _available = new(0);
    private int _queuedBytes;
    private bool _completed;

    public void Enqueue(byte[] frame, bool urgent)
    {
        lock (_gate)
        {
            if (_completed)
                throw new IOException("Connection send queue is closed.");
            if (_queuedBytes + frame.Length > ProtocolLimits.MaximumQueuedBytes)
                throw new ProtocolException("Connection send queue exceeded the 1 MiB v1 limit.");

            (urgent ? _urgent : _normal).Enqueue(frame);
            _queuedBytes += frame.Length;
            _available.Release();
        }
    }

    public async ValueTask<byte[]?> DequeueAsync(CancellationToken cancellationToken)
    {
        await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (_urgent.Count > 0)
            {
                var frame = _urgent.Dequeue();
                _queuedBytes -= frame.Length;
                return frame;
            }
            if (_normal.Count > 0)
            {
                var frame = _normal.Dequeue();
                _queuedBytes -= frame.Length;
                return frame;
            }
            return null;
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            if (_completed)
                return;
            _completed = true;
            _available.Release();
        }
    }

    public void Dispose() => _available.Dispose();
}
