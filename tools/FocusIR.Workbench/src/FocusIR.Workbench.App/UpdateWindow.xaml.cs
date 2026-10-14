using System.Windows;
using FocusIR.Workbench.App.ViewModels;

namespace FocusIR.Workbench.App;

public partial class UpdateWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _initialCheckStarted;

    public UpdateWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private async void Window_ContentRendered(object? sender, EventArgs e)
    {
        if (_initialCheckStarted)
            return;
        _initialCheckStarted = true;
        if (!_viewModel.IsUpdateBusy && !_viewModel.HasDownloadedUpdate)
            await _viewModel.CheckForUpdatesAsync(manualCheck: true);
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsUpdateBusy)
        {
            _viewModel.CancelUpdateOperation();
            return;
        }

        if (!_viewModel.HasAvailableUpdate)
        {
            await _viewModel.CheckForUpdatesAsync(manualCheck: true);
            return;
        }

        if (!_viewModel.HasDownloadedUpdate)
        {
            if (MessageBox.Show(this, "是否下载并验证最新版本？", "下载工作台更新",
                    MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
                return;
            await _viewModel.DownloadAvailableUpdateAsync();
            // 下载完成后由用户点击安装，以便先完成设备操作并断开连接。
            return;
        }

        if (!_viewModel.CanApplyDownloadedUpdate)
        {
            MessageBox.Show(this, _viewModel.UpdateApplyBlockedReason, "暂时不能安装更新",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (MessageBox.Show(this,
                "安装更新需要退出工作台。请确认设备已断开且镜头已经人工确认停稳。是否继续？",
                "安装工作台更新", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        if (!_viewModel.TryStartDownloadedUpdate(out var error))
            MessageBox.Show(this, error, "启动更新失败", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
