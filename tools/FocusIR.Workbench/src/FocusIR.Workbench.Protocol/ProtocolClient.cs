using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace FocusIR.Workbench.Protocol;

public sealed class ProtocolClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly TcpClient _tcpClient;
    private readonly NetworkStream _stream;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly PrioritySendQueue _sendQueue = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Task _readerTask;
    private readonly Task _writerTask;
    private Guid? _sessionId;
    private int _disposed;

    private ProtocolClient(TcpClient tcpClient)
    {
        _tcpClient = tcpClient;
        _stream = tcpClient.GetStream();
        _readerTask = Task.Run(ReadLoopAsync);
        _writerTask = Task.Run(WriteLoopAsync);
    }

    public event Action<JsonElement>? EventReceived;
    public event Action<Exception?>? Disconnected;
    public bool HasSession => _sessionId is not null;

    public static async Task<ProtocolClient> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));

        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            return new ProtocolClient(tcp);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    public void SetSessionId(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("Session id cannot be empty.", nameof(sessionId));
        _sessionId = sessionId;
    }

    public async Task<JsonElement> RequestAsync(
        string method,
        JsonObject? parameters = null,
        Guid? requestId = null,
        Guid? leaseId = null,
        string? controlEpoch = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        var id = requestId ?? Guid.NewGuid();
        var request = new JsonObject
        {
            ["protocol"] = "focusir",
            ["version"] = ProtocolLimits.Version,
            ["kind"] = "request",
            ["request_id"] = id.ToString(),
            ["method"] = method,
            ["params"] = parameters?.DeepClone() ?? new JsonObject()
        };

        if (method != "session.open")
        {
            if (_sessionId is null)
                throw new InvalidOperationException("session.open must complete before other RPCs are sent.");
            request["session_id"] = _sessionId.Value.ToString();
        }
        if (leaseId is not null)
            request["lease_id"] = leaseId.Value.ToString();
        if (controlEpoch is not null)
            request["control_epoch"] = controlEpoch;

        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
        var frame = FrameCodec.Encode(bytes);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
            throw new InvalidOperationException($"Request id {id} is already pending.");

        try
        {
            var urgent = method is "motion.stop_all" or "motion.jog.stop" or "task.cancel";
            _sendQueue.Enqueue(frame, urgent);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            linked.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
            try
            {
                return await completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
            {
                throw new RequestTimeoutException(id, method);
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task<JsonElement> GetRequestStatusAsync(Guid originalRequestId, CancellationToken cancellationToken = default) =>
        RequestAsync("request.get_status", new JsonObject { ["request_id"] = originalRequestId.ToString() }, cancellationToken: cancellationToken);

    private async Task ReadLoopAsync()
    {
        Exception? failure = null;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                using var document = await FrameCodec.ReadAsync(_stream, _lifetime.Token).ConfigureAwait(false);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("kind", out var kindElement))
                    throw new ProtocolException("Received message has no v1 kind.");

                var kind = kindElement.GetString();
                if (kind == "response")
                {
                    if (!root.TryGetProperty("request_id", out var requestIdElement) || !Guid.TryParse(requestIdElement.GetString(), out var requestId))
                        throw new ProtocolException("Response request_id is invalid.");
                    if (_pending.TryGetValue(requestId, out var completion))
                        completion.TrySetResult(root.Clone());
                }
                else if (kind is "event" or "telemetry")
                {
                    var clone = root.Clone();
                    foreach (Action<JsonElement> handler in EventReceived?.GetInvocationList() ?? [])
                    {
                        try { handler(clone); }
                        catch { /* Consumer errors do not corrupt the TCP receive loop. */ }
                    }
                }
                else
                {
                    throw new ProtocolException($"Unexpected message kind '{kind}'.");
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            failure = ex;
            _lifetime.Cancel();
            _tcpClient.Dispose();
            foreach (var pending in _pending.Values)
                pending.TrySetException(ex);
        }
        finally
        {
            foreach (Action<Exception?> handler in Disconnected?.GetInvocationList() ?? [])
            {
                try { handler(failure); }
                catch { /* Consumer errors do not affect connection cleanup. */ }
            }
        }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var frame = await _sendQueue.DequeueAsync(_lifetime.Token).ConfigureAwait(false);
                if (frame is null)
                    return;
                await FrameCodec.WriteAsync(_stream, frame, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _lifetime.Cancel();
            _tcpClient.Dispose();
            foreach (var pending in _pending.Values)
                pending.TrySetException(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _sendQueue.Complete();
        _lifetime.Cancel();
        _tcpClient.Dispose();
        foreach (var pending in _pending.Values)
            pending.TrySetException(new IOException("Connection closed."));
        try { await Task.WhenAll(_readerTask, _writerTask).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _sendQueue.Dispose();
        _lifetime.Dispose();
    }
}
