using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.ComponentModel;
using FocusIR.Workbench.App.ViewModels;

namespace FocusIR.Workbench.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private UpdateWindow? _updateWindow;
    private bool _startupUpdateCheckScheduled;
    private bool _closing;
    private bool _closeCompleted;
    private bool? _previewPanelsSideBySide;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        Closing += Window_Closing;
        Deactivated += async (_, _) => await _viewModel.EndManualJogAsync("窗口失去焦点");
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.ConnectAsync(HostBox.Text, PortBoxText());
    }

    private async void RefreshProfiles_Click(object sender,RoutedEventArgs e) => await _viewModel.RefreshProfilesAsync();
    private async void CreateLensModel_Click(object sender,RoutedEventArgs e) => await _viewModel.CreateLensModelAsync();
    private async void DeleteLensModel_Click(object sender,RoutedEventArgs e)
    {
        if (_viewModel.SelectedLensModel is not { } lens) return;
        var message=$"确定删除镜头型号“{lens.Model}”吗？\n\n该型号下的 {lens.ProfileCount} 个物距配置、曲线和速度表都会删除。若它当前已激活，设备运行曲线会立即置为闲置。此操作无法撤销。";
        if(MessageBox.Show(this,message,"确认删除镜头型号",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)==MessageBoxResult.Yes)
            await _viewModel.DeleteLensModelAsync();
    }
    private async void ActivateLensModel_Click(object sender,RoutedEventArgs e) => await _viewModel.StartLensTaskAsync(false);
    private async void InitializeLens_Click(object sender,RoutedEventArgs e) => await _viewModel.StartLensTaskAsync(true);
    private void NewProfile_Click(object sender,RoutedEventArgs e) => _viewModel.NewProfile();
    private async void DeleteProfile_Click(object sender,RoutedEventArgs e)
    {
        if (_viewModel.SelectedLensModel is not { } lens || _viewModel.SelectedProfile is not { } profile) return;
        var active=profile.Active?"\n\n该物距当前已激活，删除后设备运行曲线会立即置为闲置。":"";
        var message=$"确定删除“{lens.Model} / {profile.DisplayName}”物距配置吗？\n\n对应曲线模型和速度数据将一并删除。{active}\n\n此操作无法撤销。";
        if(MessageBox.Show(this,message,"确认删除物距配置",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)==MessageBoxResult.Yes)
            await _viewModel.DeleteProfileAsync();
    }
    private void RecalibrateProfile_Click(object sender,RoutedEventArgs e) => _viewModel.RecalibrateProfile();
    private async void ActivateProfile_Click(object sender,RoutedEventArgs e) => await _viewModel.StartProfileTaskAsync(false);
    private async void VerifyProfile_Click(object sender,RoutedEventArgs e) => await _viewModel.StartProfileTaskAsync(true);

    private async void Disconnect_Click(object sender, RoutedEventArgs e) => await _viewModel.DisconnectAsync();

    private void ReconnectPreview_Click(object sender, RoutedEventArgs e) => _viewModel.Preview.Reconnect();

    private void PreviewWorkspace_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (PreviewPanel is null || DetailsPanel is null || PreviewSplitter is null) return;
        var width = e.NewSize.Width;
        var height = e.NewSize.Height;
        // 使用迟滞区间，避免在临界宽度反复切换，也保留同一布局内用户拖动的比例。
        var sideBySide = _previewPanelsSideBySide == true
            ? width >= 1040 && width >= height * 1.65
            : width >= 1120 && width >= height * 1.8;
        if (_previewPanelsSideBySide == sideBySide) return;
        _previewPanelsSideBySide = sideBySide;

        PreviewColumn.MinWidth = sideBySide ? 400 : 0;
        DetailsColumn.MinWidth = sideBySide ? 420 : 0;
        PreviewColumn.Width = new GridLength(sideBySide ? 58 : 1, GridUnitType.Star);
        PreviewDividerColumn.Width = new GridLength(sideBySide ? 8 : 0);
        DetailsColumn.Width = sideBySide ? new GridLength(42, GridUnitType.Star) : new GridLength(0);
        PreviewRow.MinHeight = sideBySide ? 0 : 230;
        DetailsRow.MinHeight = sideBySide ? 0 : 180;
        PreviewRow.Height = new GridLength(sideBySide ? 1 : 55, GridUnitType.Star);
        PreviewDividerRow.Height = new GridLength(sideBySide ? 0 : 8);
        DetailsRow.Height = sideBySide ? new GridLength(0) : new GridLength(45, GridUnitType.Star);
        Grid.SetRow(PreviewSplitter, sideBySide ? 0 : 1);
        Grid.SetColumn(PreviewSplitter, sideBySide ? 1 : 0);
        Grid.SetRow(DetailsPanel, sideBySide ? 0 : 2);
        Grid.SetColumn(DetailsPanel, sideBySide ? 2 : 0);
        PreviewSplitter.Width = sideBySide ? 6 : double.NaN;
        PreviewSplitter.Height = sideBySide ? double.NaN : 6;
        PreviewSplitter.HorizontalAlignment = sideBySide ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        PreviewSplitter.VerticalAlignment = sideBySide ? VerticalAlignment.Stretch : VerticalAlignment.Center;
        PreviewSplitter.ResizeDirection = sideBySide ? GridResizeDirection.Columns : GridResizeDirection.Rows;
        PreviewSplitter.Cursor = sideBySide ? Cursors.SizeWE : Cursors.SizeNS;
        PreviewSplitter.ToolTip = sideBySide ? "左右拖动调整预览和标定信息的宽度" : "上下拖动调整预览和标定信息的高度";
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeCompleted) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        IsEnabled = false;
        try
        {
            await _viewModel.DisposeAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"关闭工作台时释放连接失败：{ex}");
        }
        finally
        {
            _closeCompleted = true;
            // 退出当前 Closing 回调后再关闭窗口，保留异步清理所需的 Dispatcher。
            await Dispatcher.InvokeAsync(() => { if (IsLoaded) Close(); });
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e) => await _viewModel.StopAllAsync();

    private async void AcquireControl_Click(object sender, RoutedEventArgs e) => await _viewModel.AcquireOperationControlAsync();

    private async void AcquireSceneControl_Click(object sender, RoutedEventArgs e) => await _viewModel.AcquireSceneControlAsync();

    private async void ReleaseControl_Click(object sender, RoutedEventArgs e) => await _viewModel.ReleaseControlAsync();

    private async void StartAutofocus_Click(object sender, RoutedEventArgs e) => await _viewModel.StartAutofocusAsync();

    private async void RefreshAutofocusConfiguration_Click(object sender, RoutedEventArgs e) => await _viewModel.RefreshAutofocusConfigurationAsync();

    private async void SaveAutofocusConfiguration_Click(object sender, RoutedEventArgs e) => await _viewModel.SaveAutofocusConfigurationAsync();

    private async void StartSceneInitialization_Click(object sender, RoutedEventArgs e) => await _viewModel.StartSceneInitializationAsync();

    private async void StartFactoryCalibration_Click(object sender, RoutedEventArgs e) => await _viewModel.StartFactoryCalibrationAsync();

    private async void RefreshTask_Click(object sender, RoutedEventArgs e) => await _viewModel.RefreshTaskAsync();

    private async void CancelTask_Click(object sender, RoutedEventArgs e) => await _viewModel.CancelCurrentTaskAsync();

    private async void ReadAutofocusResult_Click(object sender, RoutedEventArgs e) => await _viewModel.ReadAutofocusResultAsync();

    private async void RefreshTracking_Click(object sender, RoutedEventArgs e) => await _viewModel.RefreshTrackingAsync();
    private async void EnableTracking_Click(object sender, RoutedEventArgs e) => await _viewModel.EnableTrackingAsync();
    private async void VerifyTracking_Click(object sender, RoutedEventArgs e) => await _viewModel.StartTrackingAsync(true);
    private async void MoveTracking_Click(object sender, RoutedEventArgs e) => await _viewModel.StartTrackingAsync(false);
    private async void StopTracking_Click(object sender, RoutedEventArgs e) => await _viewModel.StopTrackingAsync();

    private async void JogButton_PreviewMouseLeftButtonDown(object sender,MouseButtonEventArgs e)
    {
        if(sender is not Button button || button.Tag is not string tag) return;
        var parts=tag.Split(':');if(parts.Length!=2) return;
        e.Handled=true;button.CaptureMouse();
        await _viewModel.BeginManualJogAsync(parts[0],parts[1]);
    }

    private async void JogButton_PreviewMouseLeftButtonUp(object sender,MouseButtonEventArgs e)
    {
        e.Handled=true;await _viewModel.EndManualJogAsync();
        if(sender is Button button && button.IsMouseCaptured) button.ReleaseMouseCapture();
    }

    private async void JogButton_LostMouseCapture(object sender,MouseEventArgs e)
    {
        if(!_viewModel.IsManualJogHeld) return;
        if(Mouse.LeftButton==MouseButtonState.Pressed && sender is Button button && button.IsEnabled)
        {
            await button.Dispatcher.InvokeAsync(() =>
            {
                if(_viewModel.IsManualJogHeld && Mouse.LeftButton==MouseButtonState.Pressed && !button.IsMouseCaptured)
                    button.CaptureMouse();
            });
            return;
        }
        await _viewModel.EndManualJogAsync("按钮已松开或捕获已结束");
    }

    private async void ReadTemperature_Click(object sender, RoutedEventArgs e) => await _viewModel.ReadTemperatureAsync();

    private async void RefreshSnapshot_Click(object sender, RoutedEventArgs e) => await _viewModel.RefreshSnapshotAsync();

    private async void QueryUncertainRequest_Click(object sender, RoutedEventArgs e) => await _viewModel.QueryUncertainRequestAsync();

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F12)
            return;
        e.Handled = true;
        await _viewModel.StopAllAsync();
    }

    private async void Window_ContentRendered(object? sender, EventArgs e)
    {
        if (_startupUpdateCheckScheduled)
            return;
        _startupUpdateCheckScheduled = true;
        await UpdateHealthReporter.TryReportHealthyAsync();
        await Task.Delay(TimeSpan.FromSeconds(5));
        if (IsLoaded)
            await _viewModel.CheckForUpdatesAsync(manualCheck: false);
    }

    private void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_updateWindow is not null)
        {
            _updateWindow.Activate();
            return;
        }

        _updateWindow = new UpdateWindow(_viewModel) { Owner = this };
        _updateWindow.Closed += (_, _) => _updateWindow = null;
        _updateWindow.Show();
    }

    private void AboutWorkbench_Click(object sender, RoutedEventArgs e)
    {
        new AboutWindow { Owner = this }.ShowDialog();
    }

    private string PortBoxText() => ((MainViewModel)DataContext).Port;
}
