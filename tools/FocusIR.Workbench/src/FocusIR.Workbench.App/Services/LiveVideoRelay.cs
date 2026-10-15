using System.Net;
using System.Net.Sockets;

namespace FocusIR.Workbench.App.Services;

/// <summary>
/// 用本机直通流接入 VLC 的零缓存网络输入，绕开 MediaInput/imem 固定的 PTS 延迟。
/// 仅监听回环地址，片段写入完成后才接收下一段，不建立视频帧队列。
/// </summary>
internal sealed class LiveVideoRelay : IDisposable
{
    private readonly object _gate = new();
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private TcpClient? _client;
    private bool _stopped;

    public Uri Start()
    {
        lock (_gate)
        {
            if (_stopped) throw new OperationCanceledException();
            _listener.Start(1);
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            return new Uri($"tcp://127.0.0.1:{port}");
        }
    }

    public async Task<NetworkStream> AcceptAsync(CancellationToken cancellationToken)
    {
        var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (_stopped || cancellationToken.IsCancellationRequested)
            {
                client.Dispose();
                throw new OperationCanceledException(cancellationToken);
            }
            _client = client;
            _listener.Stop(); // 接受唯一播放器后关闭监听，不保留待处理连接。
            client.NoDelay = true;
            client.SendBufferSize = 4096;
            return client.GetStream();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            _listener.Stop();
            _client?.Dispose();
            _client = null;
        }
    }
}
