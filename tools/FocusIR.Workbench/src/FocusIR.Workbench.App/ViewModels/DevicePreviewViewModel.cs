using System.ComponentModel;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using FocusIR.Workbench.App.Services;
using LibVLCSharp.Shared;

namespace FocusIR.Workbench.App.ViewModels;

/// <summary>预览独立于标定业务；设备连接切换只取消网络，原生解码资源由后台串行回收。</summary>
public sealed class DevicePreviewViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly object _sessionGate = new();
    private CancellationTokenSource? _runCancellation;
    private Task _runTask = Task.CompletedTask;
    private DevicePreviewSession? _activeSession;
    private string? _host;
    private long _version;
    private bool _disposed;
    private MediaPlayer? _mediaPlayer;
    private string _statusText = "连接设备后自动显示实时图像";
    private string _endpointText = "设备实时预览";
    private double _videoAspectRatio = 16.0 / 9;

    public event PropertyChangedEventHandler? PropertyChanged;
    public MediaPlayer? MediaPlayer => _mediaPlayer;
    public string StatusText => _statusText;
    public string EndpointText => _endpointText;
    public double VideoAspectRatio => _videoAspectRatio;
    public bool CanReconnect => !_disposed && _host is not null;

    public void SetConnection(string? host)
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        host = string.IsNullOrWhiteSpace(host) ? null : host.Trim().Trim('[', ']');
        if (string.Equals(_host, host, StringComparison.OrdinalIgnoreCase)) return;
        ChangeConnection(host);
    }

    public void Reconnect()
    {
        _dispatcher.VerifyAccess();
        if (CanReconnect) ChangeConnection(_host);
    }

    public Task StopAsync()
    {
        _dispatcher.VerifyAccess();
        if (!_disposed) ChangeConnection(null);
        return _runTask;
    }

    public ValueTask DisposeAsync()
    {
        _dispatcher.VerifyAccess();
        if (!_disposed)
        {
            ChangeConnection(null);
            _disposed = true;
            Changed(nameof(CanReconnect));
        }
        return new ValueTask(_runTask);
    }

    private void ChangeConnection(string? host)
    {
        var version = ++_version;
        _host = host;
        var previousCancellation = _runCancellation;
        var previousTask = _runTask;
        previousCancellation?.Cancel();
        lock (_sessionGate) _activeSession?.RequestStop();
        _runCancellation = null;
        SetPlayer(null);
        _endpointText = "设备实时预览";
        _statusText = "连接设备后自动显示实时图像";
        _videoAspectRatio = 16.0 / 9;
        Changed(nameof(VideoAspectRatio));

        Uri? endpoint = null;
        if (host is not null)
        {
            try
            {
                if (Uri.CheckHostName(host) == UriHostNameType.Unknown) throw new UriFormatException();
                endpoint = new UriBuilder("ws", host, 8877, "infrared_mainstream").Uri;
                _endpointText = endpoint.AbsoluteUri;
                _statusText = "正在连接设备预览…";
                _runCancellation = new CancellationTokenSource();
            }
            catch (UriFormatException) { _statusText = "设备预览地址无效"; }
        }
        Changed(nameof(EndpointText));
        Changed(nameof(StatusText));
        Changed(nameof(CanReconnect));

        var runCancellation = _runCancellation;
        _runTask = Task.Run(async () =>
        {
            try { await previousTask.ConfigureAwait(false); }
            catch (Exception error) { Trace.TraceWarning("上一轮视频预览退出失败：{0}", error.Message); }
            finally { previousCancellation?.Dispose(); }
            if (endpoint is not null && runCancellation is not null)
                await RunPreviewAsync(endpoint, version, runCancellation.Token).ConfigureAwait(false);
        });
    }

    private async Task RunPreviewAsync(Uri endpoint, long version, CancellationToken cancellationToken)
    {
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var session = new DevicePreviewSession();
            lock (_sessionGate)
            {
                if (cancellationToken.IsCancellationRequested) session.RequestStop();
                _activeSession = session;
            }
            try
            {
                PublishStatus(version, "正在连接设备预览…");
                await session.RunAsync(endpoint, player => PublishPlayerAsync(version, player),
                    status => PublishStatus(version, status),
                    ratio => PublishAspectRatio(version, ratio), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                failures = session.HasDisplayedFrames ? 1 : Math.Min(failures + 1, 3);
                var delay = failures switch { 1 => 1, 2 => 2, _ => 5 };
                var detail = error switch
                {
                    WebSocketException => "设备预览连接不可用",
                    DllNotFoundException => "缺少 VLC 解码组件，请使用完整安装包",
                    _ => error.Message
                };
                PublishStatus(version, $"{detail}；{delay} 秒后自动重连");
                try { await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            finally
            {
                lock (_sessionGate)
                    if (ReferenceEquals(_activeSession, session)) _activeSession = null;
            }
        }
    }

    private async Task PublishPlayerAsync(long version, MediaPlayer? player)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (version == _version && !_disposed) SetPlayer(player);
            }, DispatcherPriority.DataBind).Task.ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (_dispatcher.HasShutdownStarted) { }
    }

    private void PublishStatus(long version, string status)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
        try
        {
            _dispatcher.BeginInvoke(() =>
            {
                if (version != _version || _disposed) return;
                _statusText = status;
                Changed(nameof(StatusText));
            }, DispatcherPriority.Background);
        }
        catch (InvalidOperationException) when (_dispatcher.HasShutdownStarted) { }
    }

    private void SetPlayer(MediaPlayer? player)
    {
        if (ReferenceEquals(_mediaPlayer, player)) return;
        _mediaPlayer = player;
        Changed(nameof(MediaPlayer));
    }

    private void PublishAspectRatio(long version, double ratio)
    {
        if (!double.IsFinite(ratio) || ratio <= 0 || _dispatcher.HasShutdownStarted) return;
        try
        {
            _dispatcher.BeginInvoke(() =>
            {
                if (version != _version || _disposed) return;
                _videoAspectRatio = ratio;
                Changed(nameof(VideoAspectRatio));
            }, DispatcherPriority.Background);
        }
        catch (InvalidOperationException) when (_dispatcher.HasShutdownStarted) { }
    }

    private void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
