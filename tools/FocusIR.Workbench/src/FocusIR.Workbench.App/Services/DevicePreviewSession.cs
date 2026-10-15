using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using LibVLCSharp.Shared;

namespace FocusIR.Workbench.App.Services;

/// <summary>一次 WebSocket / H.264 解码生命周期；所有原生创建与销毁均由后台任务持有。</summary>
internal sealed class DevicePreviewSession
{
    private readonly ClientWebSocket _socket = new();
    private readonly LiveVideoRelay _relay = new();
    private readonly CancellationTokenSource _stop = new();
    private long _lastReceived;
    private int _stopped;

    public bool HasDisplayedFrames { get; private set; }

    public void RequestStop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        // 先关闭直通流解除 VLC 的读取等待，再停止原生播放器。
        _relay.Dispose();
        _socket.Abort();
        _stop.Cancel();
    }

    public async Task RunAsync(Uri endpoint, Func<MediaPlayer?, Task> publishPlayer,
        Action<string> publishStatus, Action<double> publishAspectRatio, CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(RequestStop);
        LibVLC? libVlc = null;
        Media? media = null;
        MediaPlayer? player = null;
        Task? receiveTask = null;
        Task? monitorTask = null;
        var decoderEnded = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<EventArgs> onError = (_, _) => decoderEnded.TrySetResult("视频解码失败");
        EventHandler<EventArgs> onEnd = (_, _) => decoderEnded.TrySetResult("设备视频流已结束");
        try
        {
            _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            _stop.Token.ThrowIfCancellationRequested();
            publishStatus("正在准备低延迟预览…");
            var nativeDirectory = Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64");
            if (!File.Exists(Path.Combine(nativeDirectory, "libvlc.dll")))
                throw new DllNotFoundException("缺少 VLC 解码组件，请使用完整安装包");
            Core.Initialize(nativeDirectory);
            // 不预存视频帧：关闭时间缓存和时钟补偿，限制原生字节预读并避免帧并行解码等待。
            libVlc = new LibVLC("--no-audio", "--no-video-title-show", "--no-snapshot-preview", "--stats",
                "--avcodec-threads=1", "--avcodec-options={flags=+low_delay}",
                "--prefetch-buffer-size=4", "--prefetch-read-size=4096", "--drop-late-frames");
            var localEndpoint = _relay.Start();
            media = new Media(libVlc, localEndpoint.AbsoluteUri, FromType.FromLocation,
                ":demux=h264", ":live-caching=0", ":file-caching=0",
                ":network-caching=0", ":clock-jitter=0", ":clock-synchro=0");
            player = new MediaPlayer(libVlc)
            {
                Mute = true,
                AspectRatio = null,
                Scale = 0,
                EnableKeyInput = false,
                EnableMouseInput = false
            };
            player.EncounteredError += onError;
            player.EndReached += onEnd;
            _stop.Token.ThrowIfCancellationRequested();
            await publishPlayer(player).ConfigureAwait(false);
            _stop.Token.ThrowIfCancellationRequested();

            _lastReceived = Stopwatch.GetTimestamp();
            receiveTask = ReceiveAsync(endpoint, publishStatus, _stop.Token);
            if (!player.Play(media)) throw new InvalidOperationException("无法启动 H.264 预览解码器");
            monitorTask = MonitorAsync(media, player, publishStatus, publishAspectRatio, _stop.Token);
            var completed = await Task.WhenAny(receiveTask, monitorTask, decoderEnded.Task).ConfigureAwait(false);
            if (completed == decoderEnded.Task)
                throw new InvalidOperationException(await decoderEnded.Task.ConfigureAwait(false));
            await completed.ConfigureAwait(false);
        }
        finally
        {
            RequestStop();
            // 先移除 VideoView 绑定，再在后台停止和释放，事件回调本身只负责发出信号。
            try { await publishPlayer(null).ConfigureAwait(false); }
            finally
            {
                await ObserveCompletionAsync(receiveTask).ConfigureAwait(false);
                await ObserveCompletionAsync(monitorTask).ConfigureAwait(false);
                if (player is not null)
                {
                    player.EncounteredError -= onError;
                    player.EndReached -= onEnd;
                    ReleaseNative(player.Stop);
                    ReleaseNative(player.Dispose);
                }
                if (media is not null) ReleaseNative(media.Dispose);
                _relay.Dispose();
                if (libVlc is not null) ReleaseNative(libVlc.Dispose);
                _socket.Dispose();
                _stop.Dispose();
            }
        }
    }

    private async Task ReceiveAsync(Uri endpoint, Action<string> publishStatus, CancellationToken cancellationToken)
    {
        using var stream = await _relay.AcceptAsync(cancellationToken).ConfigureAwait(false);
        // 播放器准备好读取后才建立 WS，避免解码器初始化期间设备数据提前堆积。
        publishStatus("正在连接设备预览…");
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(6));
            try { await _socket.ConnectAsync(endpoint, connectTimeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new TimeoutException("预览连接超时"); }
        }
        Interlocked.Exchange(ref _lastReceived, Stopwatch.GetTimestamp());
        publishStatus("预览已连接，等待 H.264 关键帧…");
        var buffer = new byte[64 * 1024];
        while (true)
        {
            // 每个接收片段立刻交给解码器；不等待消息结束、完整 NAL 或下一帧。
            var result = await _socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", closeTimeout.Token).ConfigureAwait(false);
                throw new IOException("设备已关闭预览连接");
            }
            if (result.MessageType != WebSocketMessageType.Binary)
                throw new InvalidDataException("设备预览返回了非二进制数据，需要 H.264 Annex B 码流");
            if (result.Count == 0) continue;
            Interlocked.Exchange(ref _lastReceived, Stopwatch.GetTimestamp());
            await stream.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task MonitorAsync(Media media, MediaPlayer player, Action<string> publishStatus,
        Action<double> publishAspectRatio, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var lastFrame = started;
        var frames = 0;
        var aspectRatio = 0.0;
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            if (_socket.State == WebSocketState.Open
                && Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastReceived)) > TimeSpan.FromSeconds(6))
                throw new TimeoutException("设备超过 6 秒未发送视频数据");

            var displayed = media.Statistics.DisplayedPictures;
            if (displayed != frames)
            {
                frames = displayed;
                lastFrame = Stopwatch.GetTimestamp();
                if (!HasDisplayedFrames)
                {
                    HasDisplayedFrames = true;
                    publishStatus("实时预览 · 低延迟 H.264");
                }
                var currentRatio = ReadAspectRatio(media, player);
                if (currentRatio > 0 && Math.Abs(currentRatio - aspectRatio) > 0.001)
                {
                    aspectRatio = currentRatio;
                    publishAspectRatio(aspectRatio);
                }
            }
            if (!HasDisplayedFrames && Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(15))
                throw new TimeoutException("尚未获得可播放图像，请确认设备发送 Annex B 码流及 SPS/PPS 和关键帧");
            if (HasDisplayedFrames && Stopwatch.GetElapsedTime(lastFrame) > TimeSpan.FromSeconds(10))
                throw new TimeoutException("预览图像超过 10 秒未更新");
        }
    }

    private static double ReadAspectRatio(Media media, MediaPlayer player)
    {
        foreach (var track in media.Tracks)
        {
            if (track.TrackType != TrackType.Video) continue;
            var video = track.Data.Video;
            if (video.Width == 0 || video.Height == 0) continue;
            var ratio = (double)video.Width / video.Height;
            if (video.SarNum > 0 && video.SarDen > 0) ratio *= (double)video.SarNum / video.SarDen;
            return ratio;
        }
        uint width = 0, height = 0;
        return player.Size(0, ref width, ref height) && width > 0 && height > 0 ? (double)width / height : 0;
    }

    private static async Task ObserveCompletionAsync(Task? task)
    {
        if (task is null) return;
        try { await task.ConfigureAwait(false); }
        catch (Exception) { /* 退出原因由胜出的任务报告；这里回收其余任务。 */ }
    }

    private static void ReleaseNative(Action release)
    {
        try { release(); }
        catch (Exception error) { Trace.TraceWarning("视频预览资源释放失败：{0}", error.Message); }
    }
}
