using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using FocusIR.Workbench.Application;
using FocusIR.Workbench.Domain;
using FocusIR.Workbench.Infrastructure;

namespace FocusIR.Workbench.App.ViewModels;

public sealed partial class MainViewModel
{
    private readonly HttpClient _updateHttpClient = CreateUpdateHttpClient();
    private readonly IUpdateService _updateService;
    private CancellationTokenSource? _updateCancellation;
    private UpdateRelease? _availableUpdate;
    private UpdateDownloadResult? _downloadedUpdate;
    private bool _updateBusy;
    private string _updateStatus = $"当前版本 v{AppVersion.Current}";

    public string ApplicationBadgeText => $"v{AppVersion.Current} · v1 协议 · .NET 10 · Windows x64";
    public string UpdateStatusText => _updateStatus;
    public bool HasAvailableUpdate => _availableUpdate is not null;
    public bool HasDownloadedUpdate => _downloadedUpdate is not null;
    public bool IsUpdateBusy => _updateBusy;
    public string AvailableUpdateReleaseNotes => _availableUpdate?.ReleaseNotes ?? string.Empty;
    public bool CanUseUpdateAction => true;
    public bool CanApplyDownloadedUpdate => _downloadedUpdate is not null && UpdateApplyBlockedReason.Length == 0;
    public string UpdateActionText => _updateBusy
        ? "取消更新操作"
        : _downloadedUpdate is not null
            ? $"安装 v{_downloadedUpdate.Version}"
            : _availableUpdate is not null
                ? $"下载 v{_availableUpdate.Version}"
                : "检查更新";

    public string UpdateApplyBlockedReason
    {
        get
        {
            if (_downloadedUpdate is null)
                return "尚未下载可安装的升级包。";
            if (_state != ConnectionState.Disconnected)
                return "请先确认设备没有活动任务，然后断开设备连接。";
            if (_lease is not null)
                return "当前仍持有设备控制租约，不能安装更新。";
            if (HasActiveTask() || _factoryTaskFinalizing)
                return "设备任务尚未结束，不能安装更新。";
            if (_operationBusy)
                return "当前操作尚未结束，不能安装更新。";
            if (Charts.IsEditing)
                return "请先完成或取消曲线编辑。";
            return string.Empty;
        }
    }

    public async Task CheckForUpdatesAsync(bool manualCheck)
    {
        if (_updateBusy)
            return;

        _updateBusy = true;
        if (manualCheck)
            SetUpdateStatus("正在从 Gitee 检查稳定版更新…");
        RefreshUpdateProperties();
        var cancellationToken = ReplaceUpdateCancellation(TimeSpan.FromSeconds(20));

        try
        {
            var currentVersion = SemanticVersion.Parse(AppVersion.Current);
            var result = await _updateService.CheckForUpdateAsync(currentVersion, cancellationToken);
            _downloadedUpdate = null;
            _availableUpdate = result.IsUpdateAvailable ? result.Release : null;
            SetUpdateStatus(result.Status switch
            {
                UpdateCheckStatus.UpdateAvailable => $"发现稳定版 v{result.Release!.Version}，可查看说明并下载。",
                UpdateCheckStatus.NoStableRelease => "Gitee 尚未发布可用的稳定版本。",
                _ => $"当前 v{currentVersion} 已是最新稳定版。"
            });
            AddActivity(_updateStatus);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (manualCheck)
                SetUpdateStatus("检查更新超时或已取消。");
        }
        catch (Exception ex)
        {
            AddActivity($"检查更新失败：{ex.Message}");
            if (manualCheck)
                SetUpdateStatus($"检查更新失败：{ex.Message}");
        }
        finally
        {
            _updateBusy = false;
            RefreshUpdateProperties();
        }
    }

    public async Task DownloadAvailableUpdateAsync()
    {
        if (_updateBusy || _availableUpdate is null)
            return;

        var release = _availableUpdate;
        _updateBusy = true;
        SetUpdateStatus($"正在下载 v{release.Version}…");
        RefreshUpdateProperties();
        var cancellationToken = ReplaceUpdateCancellation();

        try
        {
            var progress = new Progress<UpdateDownloadProgress>(value =>
            {
                SetUpdateStatus(value.Percentage is { } percentage
                    ? $"正在下载 v{release.Version}：{percentage:0.0}%"
                    : $"正在下载 v{release.Version}：{value.BytesReceived / 1024d / 1024d:0.0} MiB");
            });
            var result = await _updateService.DownloadUpdateAsync(
                release,
                Path.Combine(ManagedInstallRoot(), "downloads"),
                progress,
                cancellationToken);
            _downloadedUpdate = result;
            SetUpdateStatus($"v{result.Version} 已下载并通过签名、大小和 SHA-256 校验，可在安全状态下安装。");
            AddActivity($"升级包 v{result.Version} 已验证，可在安全状态下安装。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetUpdateStatus("已取消下载更新。");
        }
        catch (Exception ex)
        {
            SetUpdateStatus($"下载或校验更新失败：{ex.Message}");
            AddActivity(_updateStatus);
        }
        finally
        {
            _updateBusy = false;
            RefreshUpdateProperties();
        }
    }

    public void CancelUpdateOperation()
    {
        if (_updateBusy)
            _updateCancellation?.Cancel();
    }

    public bool TryStartDownloadedUpdate(out string error)
    {
        error = UpdateApplyBlockedReason;
        if (error.Length != 0 || _downloadedUpdate is null)
            return false;

        var updaterPath = ResolveUpdaterPath();
        if (updaterPath is null)
        {
            error = "未找到 FocusIR.Workbench.Updater.exe。请先使用 v0.2.0 完整基线包安装工作台。";
            SetUpdateStatus(error);
            return false;
        }

        var minimumUpdaterVersion = SemanticVersion.Parse(_downloadedUpdate.Release.Manifest.MinimumUpdaterVersion);
        var updaterProductVersion = FileVersionInfo.GetVersionInfo(updaterPath).ProductVersion?.Split('+', 2)[0];
        if (!SemanticVersion.TryParse(updaterProductVersion, out var updaterVersion) || updaterVersion < minimumUpdaterVersion)
        {
            error = $"当前启动器版本不足，需要至少 v{minimumUpdaterVersion}。请手工安装完整基线包。";
            SetUpdateStatus(error);
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo(updaterPath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(updaterPath)!
            };
            startInfo.ArgumentList.Add("--apply-package");
            startInfo.ArgumentList.Add(_downloadedUpdate.PackagePath);
            startInfo.ArgumentList.Add("--version");
            startInfo.ArgumentList.Add(_downloadedUpdate.Version.ToString());
            startInfo.ArgumentList.Add("--parent-pid");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--sha256");
            startInfo.ArgumentList.Add(_downloadedUpdate.Sha256);

            if (Process.Start(startInfo) is null)
                throw new InvalidOperationException("启动器进程未能启动。");

            SetUpdateStatus($"正在退出并安装 v{_downloadedUpdate.Version}…");
            System.Windows.Application.Current.Shutdown();
            return true;
        }
        catch (Exception ex)
        {
            error = $"无法启动更新器：{ex.Message}";
            SetUpdateStatus(error);
            return false;
        }
    }

    private IUpdateService CreateUpdateService() =>
        new GiteeUpdateService(_updateHttpClient, UpdateSigningKeys.TrustedPublicKeys);

    private CancellationToken ReplaceUpdateCancellation(TimeSpan? timeout = null)
    {
        _updateCancellation?.Cancel();
        _updateCancellation?.Dispose();
        _updateCancellation = new CancellationTokenSource();
        if (timeout is { } value)
            _updateCancellation.CancelAfter(value);
        return _updateCancellation.Token;
    }

    private void SetUpdateStatus(string value)
    {
        _updateStatus = value;
        OnPropertyChanged(nameof(UpdateStatusText));
    }

    private void RefreshUpdateProperties()
    {
        OnPropertyChanged(nameof(ApplicationBadgeText));
        OnPropertyChanged(nameof(UpdateActionText));
        OnPropertyChanged(nameof(UpdateStatusText));
        OnPropertyChanged(nameof(HasAvailableUpdate));
        OnPropertyChanged(nameof(HasDownloadedUpdate));
        OnPropertyChanged(nameof(IsUpdateBusy));
        OnPropertyChanged(nameof(AvailableUpdateReleaseNotes));
        OnPropertyChanged(nameof(CanUseUpdateAction));
        OnPropertyChanged(nameof(CanApplyDownloadedUpdate));
        OnPropertyChanged(nameof(UpdateApplyBlockedReason));
    }

    private static HttpClient CreateUpdateHttpClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FocusIR.Workbench", AppVersion.Current));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static string ManagedInstallRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ThermEye",
        "FocusIRWorkbench");

    private static string? ResolveUpdaterPath()
    {
        var candidates = new[]
        {
            Path.Combine(ManagedInstallRoot(), "launcher", "FocusIR.Workbench.Updater.exe"),
            Path.Combine(AppContext.BaseDirectory, "FocusIR.Workbench.Updater.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
