using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using FocusIR.Workbench.Application;
using FocusIR.Workbench.Domain;
using FocusIR.Workbench.Infrastructure;
using FocusIR.Workbench.Protocol;

namespace FocusIR.Workbench.App.ViewModels;

public sealed partial class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IWorkbenchConnectionService _connection = new WorkbenchConnectionService();
    private bool _isBusy;
    private int _disposed;
    private ConnectionState _state = ConnectionState.Disconnected;
    private string? _connectedHost;
    private bool _previewConnectionEnabled;
    private CancellationTokenSource? _connectCancellation;
    private DeviceSummary? _device;
    private JsonElement? _snapshot;
    private long? _snapshotReceivedTick;
    private JsonElement? _temperature;
    private ControlLeaseInfo? _lease;
    private JsonElement? _latestTaskStatus;
    private JsonElement? _autofocusResult;
    private JsonElement? _factoryValidation;
    private JsonElement? _factoryResult;
    private JsonElement? _tracking;
    private string _trackingZoomTarget = "";
    public string TrackingZoomTarget { get => _trackingZoomTarget; set { _trackingZoomTarget=value; OnPropertyChanged(); RefreshOperationProperties(); } }
    public Visibility TrackingControlsVisibility => OperationControlsVisibility == Visibility.Visible && Supports("tracking.get") ? Visibility.Visible : Visibility.Collapsed;
    public bool CanRefreshTracking => !_operationBusy && _state == ConnectionState.Connected && Supports("tracking.get");
    public bool CanVerifyTracking => !_operationBusy && !Charts.IsEditing && !_factoryTaskFinalizing && _state == ConnectionState.Connected && _lease?.Purpose == "operation"
        && Supports("tracking.verify") && ReadBoolean(_snapshot,"factory_model_valid") == true && NoActiveTaskConfirmed();
    public bool CanMoveTracking => CanVerifyTracking
        && ReadBoolean(_tracking,"enabled") == true && int.TryParse(TrackingZoomTarget, out _) && Supports("tracking.move");
    private bool _trackingEnabled=true;
    private string _trackingDefaultGear="1",_trackingSpeedRatios="1, 1, 1, 1, 1, 1, 1, 1, 1";
    public bool TrackingEnabled { get=>_trackingEnabled; set { _trackingEnabled=value;OnPropertyChanged();RefreshOperationProperties(); } }
    public string TrackingDefaultGear { get=>_trackingDefaultGear; set { _trackingDefaultGear=value;OnPropertyChanged();RefreshOperationProperties(); } }
    public string TrackingSpeedRatios { get=>_trackingSpeedRatios; set { _trackingSpeedRatios=value;OnPropertyChanged();RefreshOperationProperties(); } }
    public bool TrackingSettingsSupported => ReadBoolean(_tracking,"settings_supported")==true;
    public bool CanEnableTracking => !_operationBusy && _state==ConnectionState.Connected && _lease?.Purpose=="operation"
        && Supports("tracking.set") && NoActiveTaskConfirmed() && TrackingSettingsSupported;
    private double[] ParseTrackingRatios() {
        var parts=TrackingSpeedRatios.Replace('，',',').Split(',',StringSplitOptions.TrimEntries);
        if(parts.Length!=9) throw new InvalidOperationException("请按ZOOM档位1～9填写九个速度比例，用逗号分隔。");
        var values=parts.Select(p=>double.TryParse(p,NumberStyles.Float,CultureInfo.InvariantCulture,out var v)?v:double.NaN).ToArray();
        if(values.Any(v=>!double.IsFinite(v) || v<0.1 || v>3.0)) throw new InvalidOperationException("速度比例须为0.1～3.0。");
        return values;
    }
    private void ApplyTrackingState(JsonElement state,bool loadSettings=false) {
        _tracking=state;
        if(loadSettings && ReadBoolean(state,"settings_supported")==true) {
            TrackingEnabled=ReadBoolean(state,"enabled")==true;
            TrackingDefaultGear=ReadScalarText(state,"default_zoom_gear") ?? "1";
            if(state.TryGetProperty("focus_speed_ratios",out var ratios) && ratios.ValueKind==JsonValueKind.Array)
                TrackingSpeedRatios=string.Join(", ",ratios.EnumerateArray().Select(v=>v.GetDouble().ToString("0.###",CultureInfo.InvariantCulture)));
        }
        OnPropertyChanged(nameof(TrackingStatusText));OnPropertyChanged(nameof(TrackingSpeedTableText));RefreshOperationProperties();
    }
    public bool CanStopTracking => CanCancelTask && _lastTaskKind == "tracking" && Supports("tracking.stop");
    public string TrackingStatusText => _tracking is not { } t ? "尚未读取联动状态。标定成功后默认启用，可保存开关与速度比例。" :
        $"曲线：{(ReadBoolean(_snapshot,"factory_model_valid")==true?"已激活":"未激活")} · 硬件联动：{(ReadBoolean(t,"hardware_verified")==true?"已验证":"未验证")} · 联动：{(ReadBoolean(t,"enabled")==true?"已启用":"未启用")} · 档位速度表：{(ReadBoolean(t,"speed_table_valid")==true?(ReadString(t,"speed_table_method")=="three_point_linear"?"三档实测＋插值":"已实测"):"需重新标定")} · 阶段 {FactoryDataReader.StageLabel(ReadString(t,"stage") ?? "未知")} · 追焦容差 {ReadScalarText(t,"allowed_error") ?? "未知"} raw · 动态清晰效果待人工验收。 {ReadString(t,"degraded_reason")}";

    public string TrackingSpeedTableText {
        get {
            if(_tracking is not { } t || ReadBoolean(t,"speed_table_valid")!=true ||
               !t.TryGetProperty("motor_speed_table",out var table) || table.ValueKind!=JsonValueKind.Array)
                return "本模型暂无实测档位速度表。重新工厂标定会在扫焦前测量；手动ZOOM仍按指定速度运行。";
            return (ReadString(t,"speed_table_method")=="three_point_linear"?
                "档位速度 raw/s（1、5、9档实测，其余线性插值）\n":"实测速度 raw/s（按档位1～9排列）\n") + string.Join("\n", table.EnumerateArray()
                .GroupBy(row=>$"{ReadString(row,"axis")} 方向{ReadScalarText(row,"board_direction")}")
                .Select(group=>group.Key+"："+string.Join(", ",group.OrderBy(row=>row.GetProperty("gear").GetInt32())
                    .Select(row=>row.GetProperty("raw_per_second").GetDouble().ToString("0.0",CultureInfo.InvariantCulture)))));
        }
    }

    private string? _factoryRecipeHash;
    private string _factoryLensModel = string.Empty;
    private string _factoryTargetDistanceMeters = string.Empty;
    private string _factoryTargetIdentifier = string.Empty;
    private string _factorySampleCount = "5";
    private string _factoryMaxNodes = "64";
    public string FactoryNodeLimitLabel => $"每方向节点上限（2–{(FactoryUseHybrid && _device?.FactorySamplingStrategy=="hybrid_simple"?64:FactoryBothDirections?16:33)}）";
    public bool FactorySingleSweep => _device?.FactorySimpleFlow=="single_sweep_maximum";
    public Visibility FactorySingleSweepVisibility => FactorySingleSweep ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FactoryAdaptiveControlsVisibility => FactorySingleSweep ? Visibility.Collapsed : Visibility.Visible;
    public string FactoryAcquisitionNodesLabel => FactorySingleSweep ? "每方向采集节点（2–64）" : "每方向初始节点";
    public string FactorySweepStrategyLabel => FactorySingleSweep ? "单次连续扫焦" : "连续扫焦";
    public string FactorySweepStrategyDescription => FactorySingleSweep ? "每节点粗扫；局部最高分位于曲线边缘时扩大范围再扫一次。随后只做一次局部精调，取所有有效点中的最高分位置。" : "设备按公布的连续扫焦策略执行。";
    private string _factoryInitialNodes = "9";
    private string _factorySweepGear = "1";
    public string FactorySweepGear { get=>_factorySweepGear; set=>SetFactoryInput(ref _factorySweepGear,value); }
    public Visibility FactorySweepGearVisibility => (FactorySingleSweep || FactoryUseHybrid) && _device?.FactorySweepGearSupported==true ? Visibility.Visible : Visibility.Collapsed;
    private bool _factoryUseHybrid = true;
    public CalibrationChartsViewModel Charts { get; } = new();
    public DevicePreviewViewModel Preview { get; } = new();
    private string _factoryBudgetMilliseconds = "900000";
    private string _factoryClearScoreRatio = "0.95";
    private string _factoryAmbientTemperatureC = string.Empty;
    private bool _factoryBothDirections = true;
    private string? _lastTaskId;
    private string? _lastTaskKind;
    private readonly List<Guid> _uncertainRequestIds = [];
    private readonly Dictionary<Guid, string> _uncertainRequestMethods = new();
    private bool _operationBusy;
    private readonly SemaphoreSlim _manualJogGate = new(1,1);
    private CancellationTokenSource? _manualJogKeepaliveCancellation;
    private string? _manualJogId;
    private bool _manualJogHeld;
    private int _manualJogGear=3;
    private string _manualJogStatus = "默认3档；获取设备操作权后，按住按钮连续运动，松开立即停止。";
    private string _sceneBudgetMilliseconds = "180000";
    private readonly DispatcherTimer _snapshotAgeTimer;
    // 任务事件仅用于唤醒状态查询；已有待处理通知时合并后续通知。
    private readonly Channel<bool> _taskRefreshNotifications = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private CancellationTokenSource? _taskMonitorCancellation;
    private Task? _taskMonitorTask;
    private bool _factoryTaskFinalizing;
    private string _statusNotice = "尚未连接设备。";

    public MainViewModel()
    {
        _updateService = CreateUpdateService();
        Charts.SaveCurveRequested = SaveEditedCurveAsync;
        Charts.PropertyChanged += (_, args) => { if(args.PropertyName==nameof(Charts.IsEditing)) RefreshOperationProperties(); };
        _connection.StateChanged += state => OnUi(() =>
        {
            _state = state;
            // 使用本次实际连接的地址；编辑输入框不会将预览切到另一台设备。
            Preview.SetConnection(state == ConnectionState.Connected && _previewConnectionEnabled
                && _disposed == 0 && _connection.State == ConnectionState.Connected ? _connectedHost : null);
            if (state is ConnectionState.Disconnected or ConnectionState.Faulted or ConnectionState.Reconnecting)
                ClearAutofocusConfiguration();
            else if (state == ConnectionState.Connected)
                QueueAutofocusConfigurationRefresh(force: true);
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(StateBrush));
            OnPropertyChanged(nameof(CanConnect));
            OnPropertyChanged(nameof(CanDisconnect));
            RefreshOperationProperties();
        });
        _connection.DeviceUpdated += device => OnUi(() =>
        {
            var bootChanged = _device is not null && _device.BootId != device.BootId;
            if (_state == ConnectionState.Reconnecting || bootChanged)
            {
                ClearProfiles();
                _taskMonitorCancellation?.Cancel();
                _lastTaskId = null;
                _lastTaskKind = null;
                _latestTaskStatus = null;
                _autofocusResult = null;
                _factoryValidation = null;
                _factoryResult = null;
                _factoryRecipeHash = null;
                _lease = null;
                _temperature = null;
                _tracking = null;
                ClearAutofocusConfiguration();
            }
            if (bootChanged)
            {
                _uncertainRequestIds.Clear();
                _uncertainRequestMethods.Clear();
            }
            _device = device;
            QueueAutofocusConfigurationRefresh(force: true);
            if(_factoryMaxNodes is "16" or "64") {
                _factoryMaxNodes=device.FactorySamplingStrategy=="hybrid_simple"?"64":"16";
                OnPropertyChanged(nameof(FactoryMaxNodes));
            }
            RefreshDeviceProperties();
            RefreshOperationProperties();
        });
        _connection.SnapshotUpdated += (snapshot, isFresh) => OnUi(() =>
        {
            _snapshot = snapshot;
            if (isFresh)
            {
                _snapshotReceivedTick = Environment.TickCount64;
                QueueAutofocusConfigurationRefresh();
            }
            ObserveTaskSnapshot(snapshot);
            RefreshSnapshotProperties();
            RefreshOperationProperties();
        });
        _connection.LeaseUpdated += lease => OnUi(() => { _lease = lease; RefreshOperationProperties(); });
        _connection.EventReceived += item => OnUi(() =>
        {
            AddActivity($"事件 {item.EventSequence} · {item.Topic}");
            if (item.Topic.StartsWith("config", StringComparison.Ordinal))
                QueueAutofocusConfigurationRefresh(force: true);
            if (item.Topic.StartsWith("task", StringComparison.Ordinal))
            {
                _taskRefreshNotifications.Writer.TryWrite(true);
            }
        });
        _connection.Notice += message => OnUi(() => { _statusNotice = message; AddActivity(message); OnPropertyChanged(nameof(StatusNotice)); });
        _snapshotAgeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _snapshotAgeTimer.Tick += (_, _) =>
        {
            // 仅更新缓存数据龄显示，不发起状态或串口查询。
            OnPropertyChanged(nameof(ZoomPosition));
            OnPropertyChanged(nameof(FocusPosition));
            RefreshOperationProperties();
        };
        _snapshotAgeTimer.Start();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<string> RecentActivity { get; } = [];
    public string Host { get; set; } = "127.0.0.1";
    public string Port { get; set; } = "23233";
    public string WorkbenchTag => ProtocolLimits.WorkbenchTag;
    public bool CanConnect => !_isBusy && (_state is ConnectionState.Disconnected or ConnectionState.Faulted);
    public bool CanDisconnect => !_isBusy && (_state is ConnectionState.Connected or ConnectionState.Reconnecting);
    public bool CanStop => !_operationBusy && _state == ConnectionState.Connected && StopButtonVisibility == Visibility.Visible;
    public bool CanManualJog => !_operationBusy && !Charts.IsEditing && !_factoryTaskFinalizing
        && _state == ConnectionState.Connected && _lease?.Purpose == "operation"
        && (_manualJogHeld || (!HasActiveTask() && NoActiveTaskConfirmed()))
        && _device?.AxisRangesValid == true && ReadBoolean(_snapshot,"mechanical_ready")==true
        && Supports("motion.jog.start") && Supports("motion.jog.keepalive") && Supports("motion.jog.stop");
    public bool IsManualJogHeld => _manualJogHeld;
    public bool CanAdjustManualJogSpeed => CanManualJog && !_manualJogHeld && _manualJogId is null;
    public int ManualJogGear
    {
        get => _manualJogGear;
        set
        {
            var gear=Math.Clamp(value,1,9);if(_manualJogGear==gear) return;
            _manualJogGear=gear;OnPropertyChanged();
            if(!_manualJogHeld && _manualJogId is null)
            {
                _manualJogStatus=$"已选择{gear}档；按住按钮后，本次连续运动使用该速度。";
                OnPropertyChanged(nameof(ManualJogStatus));
            }
        }
    }
    public string ManualJogStatus => _manualJogStatus;
    public Visibility StopButtonVisibility => _device is not null
        && _device.SupportedMethods.Contains("motion.stop_all")
        && (_device.Role is "operator" or "factory" or "engineer")
            ? Visibility.Visible
            : Visibility.Collapsed;
    public Visibility OperationControlsVisibility => _device is not null
        && _device.SupportedMethods.Contains("control.acquire")
        && (_device.Role is "operator" or "factory" or "engineer")
            ? Visibility.Visible
            : Visibility.Collapsed;
    public Visibility SceneControlsVisibility => OperationControlsVisibility == Visibility.Visible
        && Supports("scene.initialize") ? Visibility.Visible : Visibility.Collapsed;
    public bool CanAcquireControl => !_operationBusy && !Charts.IsEditing && !_factoryTaskFinalizing && _state == ConnectionState.Connected
        && OperationControlsVisibility == Visibility.Visible && _lease is null;
    public bool CanAcquireSceneControl => !_operationBusy && !Charts.IsEditing && !_factoryTaskFinalizing && _state == ConnectionState.Connected
        && SceneControlsVisibility == Visibility.Visible && _lease is null
        && ReadBoolean(_snapshot, "factory_model_valid") == true;
    public bool CanReleaseControl => !_operationBusy && _state == ConnectionState.Connected
        && _lease is not null && !HasActiveTask();
    public bool CanStartAutofocus => !_operationBusy && !Charts.IsEditing && !_factoryTaskFinalizing && _state == ConnectionState.Connected && _lease?.Purpose == "operation"
        && Supports("af.start") && Supports("task.get") && Supports("af.get_result")
        && _device?.AxisRangesValid == true
        && ReadBoolean(_snapshot, "mechanical_ready") == true
        && ReadBoolean(_snapshot, "image_ready") == true && AxesPositionAvailable()
        && NoActiveTaskConfirmed();
    public bool CanStartSceneInitialization => !_operationBusy && !Charts.IsEditing && !_factoryTaskFinalizing && _state == ConnectionState.Connected
        && _lease?.Purpose == "scene_init" && Supports("scene.initialize")
        && Supports("task.get") && AxesPositionAvailable()
        && ReadBoolean(_snapshot, "mechanical_ready") == true && ReadBoolean(_snapshot, "image_ready") == true
        && ReadBoolean(_snapshot, "factory_model_valid") == true
        && int.TryParse(SceneBudgetMilliseconds, out var budget) && budget is >= 1 and <= 180_000 && NoActiveTaskConfirmed();
    public Visibility FactoryControlsVisibility => _device is not null
        && _device.Role is "factory" or "engineer"
        && OperationControlsVisibility == Visibility.Visible
        && Supports("factory.validate_recipe") && Supports("factory.start") && Supports("factory.get_result")
        && Supports("state.get_snapshot") && Supports("event.subscribe")
            ? Visibility.Visible
            : Visibility.Collapsed;
    public bool CanStartFactoryCalibration => !_operationBusy && _state == ConnectionState.Connected
        && FactoryControlsVisibility == Visibility.Visible
        && _lease is null && !HasActiveTask()
        && FactoryHardwareReady()
        && NoActiveTaskConfirmed()
        && !Charts.IsEditing && !_factoryTaskFinalizing && IsFactoryCalibrationEditorVisible && SelectedLensModel is not null && SelectedLensIsActive
        && ProfileNameValid && SelectedProfileDistanceMatches && TryBuildFactoryRecipe(out _, out _);
    public bool CanRefreshTask => !_operationBusy && _state == ConnectionState.Connected
        && CurrentTaskId() is not null
        && ((_lastTaskKind == "factory" && Supports("factory.get_result")) || Supports("task.get"));
    public bool CanQueryRequestStatus => !_operationBusy && _state == ConnectionState.Connected
        && Supports("request.get_status") && _uncertainRequestIds.Count > 0;
    public string RequestStatusButtonText => _uncertainRequestIds.Count > 1
        ? $"查询最近一次超时请求（{_uncertainRequestIds.Count} 条未确认）"
        : "查询最近一次超时请求";
    public bool CanCancelTask => !_operationBusy && _state == ConnectionState.Connected && _lease is not null
        && Supports("task.cancel") && CurrentTaskId() is not null && HasActiveTask();
    public bool CanReadAutofocusResult => !_operationBusy && _state == ConnectionState.Connected
        && _lastTaskKind == "af" && Supports("af.get_result") && CurrentTaskId() is not null && IsCurrentTaskTerminal();
    public string FactoryCalibrationStatusText => FormatFactoryCalibrationResult();
    public bool CanReadTemperature => !_operationBusy && _state == ConnectionState.Connected && Supports("temperature.get");
    public bool CanRefreshSnapshot => !_operationBusy && _state == ConnectionState.Connected && Supports("state.get_snapshot");
    public string ControlLeaseText => _lease is null
        ? "本会话未持有控制租约"
        : $"{_lease.Purpose switch
        {
            "scene_init" => "场景初始化租约",
            "factory" => "工厂标定租约",
            _ => "操作租约"
        }}已获取 · epoch {_lease.ControlEpoch} · TTL {_lease.LeaseTtlMilliseconds} ms（自动续期）";
    public string SceneBudgetMilliseconds
    {
        get => _sceneBudgetMilliseconds;
        set
        {
            if (_sceneBudgetMilliseconds == value)
                return;
            _sceneBudgetMilliseconds = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanStartSceneInitialization));
        }
    }
    public string FactoryLensModel { get => _factoryLensModel; set => SetFactoryInput(ref _factoryLensModel, value); }
    public string FactoryTargetDistanceMeters { get => _factoryTargetDistanceMeters; set => SetFactoryInput(ref _factoryTargetDistanceMeters, value); }
    public string FactoryTargetIdentifier { get => _factoryTargetIdentifier; set => SetFactoryInput(ref _factoryTargetIdentifier, value); }
    public string FactorySampleCount { get => _factorySampleCount; set => SetFactoryInput(ref _factorySampleCount, value); }
    public string FactoryInitialNodes { get=>_factoryInitialNodes; set=>SetFactoryInput(ref _factoryInitialNodes,value); }
    public bool FactoryUseHybrid { get=>_factoryUseHybrid; set { _factoryUseHybrid=value; OnPropertyChanged(); OnPropertyChanged(nameof(FactorySweepGearVisibility)); OnPropertyChanged(nameof(FactoryAdaptiveControlsVisibility)); OnPropertyChanged(nameof(FactorySingleSweepVisibility)); OnPropertyChanged(nameof(FactoryAcquisitionNodesLabel)); OnPropertyChanged(nameof(FactorySweepStrategyLabel)); OnPropertyChanged(nameof(FactorySweepStrategyDescription)); OnPropertyChanged(nameof(CanStartFactoryCalibration)); } }
    public string FactoryMaxNodes { get => _factoryMaxNodes; set => SetFactoryInput(ref _factoryMaxNodes, value); }
    public string FactoryBudgetMilliseconds { get => _factoryBudgetMilliseconds; set => SetFactoryInput(ref _factoryBudgetMilliseconds, value); }
    public string FactoryClearScoreRatio { get => _factoryClearScoreRatio; set => SetFactoryInput(ref _factoryClearScoreRatio, value); }
    public string FactoryAmbientTemperatureC { get => _factoryAmbientTemperatureC; set => SetFactoryInput(ref _factoryAmbientTemperatureC, value); }
    public bool FactoryBothDirections
    {
        get => _factoryBothDirections;
        set
        {
            if (_factoryBothDirections == value)
                return;
            _factoryBothDirections = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanStartFactoryCalibration));
            OnPropertyChanged(nameof(FactoryNodeLimitLabel));
            OnPropertyChanged(nameof(FactorySweepGearVisibility)); OnPropertyChanged(nameof(FactoryAdaptiveControlsVisibility)); OnPropertyChanged(nameof(FactorySingleSweepVisibility)); OnPropertyChanged(nameof(FactoryAcquisitionNodesLabel)); OnPropertyChanged(nameof(FactorySweepStrategyLabel)); OnPropertyChanged(nameof(FactorySweepStrategyDescription));
        OnPropertyChanged(nameof(FactoryReadinessText));
        }
    }
    public string SceneModelStatus
    {
        get
        {
            var valid = ReadBoolean(_snapshot, "scene_model_valid");
            if (valid == true)
                return "设备标记有效（仍须进行光学验收）";
            if (valid == false)
                return "设备未标记有效";
            if (_snapshot is { } snapshot)
            {
                var state = ReadScalarText(snapshot, "scene_state");
                var revision = ReadScalarText(snapshot, "scene_model_revision");
                if (state is not null || revision is not null)
                    return $"设备模型已变化；scene_state {state ?? "未知"} · revision {revision ?? "未知"}，请刷新状态确认有效性";
            }
            return "设备未提供场景模型状态";
        }
    }
    public string TemperatureStatus => FormatTemperature();
    public string AutofocusResultText => FormatAutofocusResult();
    public string AutofocusReadinessText => AxesPositionAvailable() && _device?.AxisRangesValid == true
        ? "双轴行程和位置有效；停稳由人工确认，确认后再启动。"
        : $"位置前置条件未满足：{AxesReadinessProblems()}；停稳由人工确认。";

    public string FactoryReadinessText => FormatFactoryReadiness();
    public string JogCapabilityText => Supports("motion.jog.start") && Supports("motion.jog.keepalive") && Supports("motion.jog.stop")
        ? "设备支持长按连续运动：按下启动、按住保活、松开停止；设备看门狗会在保活中断后自动停止。"
        : "设备未完整公布长按运动接口；当前不发送手动连续运动。";
    public string StateText => _state switch
    {
        ConnectionState.Disconnected => "未连接",
        ConnectionState.Connecting => "正在连接",
        ConnectionState.Connected => "已连接",
        ConnectionState.Reconnecting => "连接中断，正在重连",
        ConnectionState.Faulted => "连接失败",
        _ => "未知状态"
    };
    public Brush StateBrush => _state switch
    {
        ConnectionState.Connected => Brushes.ForestGreen,
        ConnectionState.Reconnecting or ConnectionState.Connecting => Brushes.DarkOrange,
        ConnectionState.Faulted => Brushes.Firebrick,
        _ => Brushes.Gray
    };
    public string DeviceIdentity => _device is null ? "暂无设备" : $"{_device.SerialNumber} · {_device.ServiceVersion}";
    public string RoleText => _device is null ? "未认证" : $"设备认证角色：{_device.Role}";
    public string LensModel => _device?.LensModel ?? "—";
    public string FirmwareVersion => _device?.FirmwareVersion ?? "—";
    public string ClarityMetricVersion => _device?.ClarityMetricVersion ?? "—";
    public string SnapshotSequence => _device?.SnapshotSequence ?? "—";
    public string MechanicalStatus => FormatBoolean("mechanical_ready");
    public string ImageStatus => FormatBoolean("image_ready");
    public string ZoomPosition => FormatAxis("zoom");
    public string FocusPosition => FormatAxis("focus");
    public string ControlStatus => FormatControl();
    public string TaskStatus => FormatTask();
    public string FaultStatus
    {
        get
        {
            var eventErrorCode = TryGetProperty(_snapshot, "fault", out var fault)
                ? ReadScalarText(fault, "error_code")
                : null;
            var controllerErrorCode = _snapshot is { } snapshot ? ReadScalarText(snapshot, "last_error") : null;
            if (controllerErrorCode is { Length: > 0 } && controllerErrorCode != "0")
                return $"控制器错误码 {controllerErrorCode}";
            if (eventErrorCode is { Length: > 0 } && eventErrorCode != "0")
                return controllerErrorCode == "0"
                    ? $"最近故障事件码 {eventErrorCode}；控制器当前错误码为 0"
                    : $"最近故障事件码 {eventErrorCode}";
            return controllerErrorCode == "0" ? "设备未报告活动故障" : "设备未提供故障状态";
        }
    }
    public string CapabilitySummary
    {
        get
        {
            if (_device is null)
                return "等待读取设备能力";
            var notesText = string.Join("；", _device.CapabilityNotes);
            var notes = string.IsNullOrWhiteSpace(notesText) ? string.Empty : "；" + notesText;
            var ranges = _device.AxisRangesValid ? "双轴原始行程有效" : "双轴原始行程未确认";
            return $"设备声明支持 {_device.SupportedMethods.Count} 个方法；{ranges}；未声明的操作入口不会开放{notes}";
        }
    }
    public string StatusNotice => _statusNotice;
    public string SourceBadgeText => _device?.DataSource switch
    {
        DataSourceKind.Simulator => "模拟数据",
        DataSourceKind.Device => "真实设备",
        _ => "来源未声明"
    };
    public Brush SourceBadgeBrush => _device?.DataSource == DataSourceKind.Simulator ? Brushes.LightGoldenrodYellow : Brushes.LightSteelBlue;

    public async Task ConnectAsync(string host, string portText)
    {
        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
        {
            _statusNotice = "端口必须是 1 到 65535 之间的整数。";
            OnPropertyChanged(nameof(StatusNotice));
            return;
        }
        _isBusy = true;
        using var connectCancellation = new CancellationTokenSource();
        _connectCancellation = connectCancellation;
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanDisconnect));
        try
        {
            var settings = new ConnectionSettings(host.Trim(), port, ProtocolLimits.WorkbenchTag, GetStableClientId(), "FocusIR.Workbench", AppVersion.Current);
            _connectedHost = settings.Host;
            _previewConnectionEnabled = true;
            var summary = await _connection.ConnectAsync(settings, connectCancellation.Token);
            connectCancellation.Token.ThrowIfCancellationRequested();
            _device = summary;
            _statusNotice = summary.ConnectionNotice;
            AddActivity($"连接成功：{summary.SerialNumber}，来源：{SourceBadgeText}。");
            RefreshDeviceProperties();
            if(Supports("profile.list")) {
                try { await RefreshProfilesCoreAsync(true); }
                catch(Exception ex) { AddActivity($"连接成功，物距配置读取失败：{ex.Message}；可点击刷新配置重试。"); }
            }
            connectCancellation.Token.ThrowIfCancellationRequested();
            if (Supports("tracking.get"))
            {
                try { ApplyTrackingState(await _connection.GetTrackingAsync(),true); }
                catch (Exception ex) { AddActivity($"连接已成功，联动状态读取失败：{ex.Message}；可点击刷新联动状态重试。"); }
            }
            connectCancellation.Token.ThrowIfCancellationRequested();
            if (Supports("af.get_config"))
            {
                try { await ReadAutofocusConfigurationCoreAsync(); }
                catch (Exception ex) { AddActivity($"连接已成功，AF 设置读取失败：{ex.Message}；可点击读取 AF 设置重试。"); }
            }
        }
        catch (Exception ex)
        {
            _previewConnectionEnabled = false;
            _connectedHost = null;
            await Preview.StopAsync();
            if (_disposed != 0) return;
            _statusNotice = ex.Message;
            AddActivity($"连接失败：{ex.Message}");
            OnPropertyChanged(nameof(StatusNotice));
        }
        finally
        {
            if (ReferenceEquals(_connectCancellation, connectCancellation)) _connectCancellation = null;
            _isBusy = false;
            RefreshAutofocusConfigurationProperties();
            OnPropertyChanged(nameof(CanConnect));
            OnPropertyChanged(nameof(CanDisconnect));
        }
    }

    public async Task DisconnectAsync()
    {
        _previewConnectionEnabled = false;
        _connectedHost = null;
        _connectCancellation?.Cancel();
        // 先撤销视频连接，再等待运动停止、任务监视与主会话清理。
        var previewStop = Preview.StopAsync();
        try
        {
            await EndManualJogAsync("连接断开");
            await StopTaskMonitorAsync();
            await _connection.DisconnectAsync();
            ClearProfiles();
            ClearAutofocusConfiguration();
            _tracking = null;
            _device = null;
            _snapshot = null;
            _snapshotReceivedTick = null;
            _temperature = null;
            _lastTaskId = null;
            _lastTaskKind = null;
            _latestTaskStatus = null;
            _autofocusResult = null;
            _factoryValidation = null;
            _factoryResult = null;
            _factoryRecipeHash = null;
            _lease = null;
            _statusNotice = "连接已断开。";
            RefreshDeviceProperties();
            RefreshSnapshotProperties();
            OnPropertyChanged(nameof(StatusNotice));
        }
        finally
        {
            await previewStop;
        }
    }

    public async Task StopAllAsync()
    {
        if (!CanStop)
        {
            _statusNotice = "当前连接、设备能力或角色不允许发送软件停止请求。";
            OnPropertyChanged(nameof(StatusNotice));
            return;
        }

        _operationBusy = true;
        RefreshOperationProperties();
        _statusNotice = "正在优先发送立即停止请求……";
        OnPropertyChanged(nameof(StatusNotice));
        try
        {
            _manualJogHeld=false;_manualJogKeepaliveCancellation?.Cancel();
            var result = await _connection.RequestStopAllAsync("workbench_immediate_stop");
            _manualJogId=null;
            var requested = ReadBoolean(result, "stop_requested");
            _statusNotice = requested switch
            {
                true => "停止请求已发送；请人工确认 ZOOM 和 FOCUS 轴已停稳。",
                false => "设备未确认停止请求已发送，请检查设备并人工确认停稳。",
                _ => "停止响应缺少请求确认字段；请检查设备并人工确认停稳。"
            };
            AddActivity(_statusNotice);
        }
        catch (RequestTimeoutException ex)
        {
            RememberUncertainRequest(ex.RequestId, ex.Method);
            _statusNotice = $"停止响应超时，执行结果未知（request_id {ex.RequestId}）。客户端不会自动重发；请查询请求状态并检查设备。";
            AddActivity("立即停止未获响应；结果未知，未自动重发。");
        }
        catch (Exception ex)
        {
            _statusNotice = $"立即停止未确认：{ex.Message}";
            AddActivity(_statusNotice);
        }
        finally
        {
            _operationBusy = false;
            OnPropertyChanged(nameof(StatusNotice));
            RefreshOperationProperties();
        }
    }

    public async Task AcquireOperationControlAsync()
    {
        if (!CanAcquireControl)
            return;
        await RunOperationAsync(async () =>
        {
            var lease = await _connection.AcquireControlAsync("operation");
            _lease = lease;
            _statusNotice = $"已获取设备操作权，租约每秒续期；epoch {lease.ControlEpoch}。";
            AddActivity(_statusNotice);
            await _connection.RefreshSnapshotAsync();
        });
    }

    public async Task ReleaseControlAsync()
    {
        if (!CanReleaseControl)
            return;
        await RunOperationAsync(async () =>
        {
            await EndManualJogAsync("释放操作权");
            await _connection.ReleaseControlAsync();
            _lease = null;
            _statusNotice = "已请求释放本会话的控制租约。";
            AddActivity(_statusNotice);
        });
    }

    public async Task BeginManualJogAsync(string axis,string direction)
    {
        if(!CanManualJog || axis is not ("ZOOM" or "FOCUS") || direction is not ("positive" or "negative")) return;
        _manualJogHeld=true;var stopImmediately=false;var gear=ManualJogGear;
        OnPropertyChanged(nameof(CanManualJog));OnPropertyChanged(nameof(CanAdjustManualJogSpeed));
        await _manualJogGate.WaitAsync();
        try
        {
            if(!_manualJogHeld || _manualJogId is not null) return;
            var result=await _connection.StartJogAsync(axis,direction,gear,800);
            _manualJogId=ReadString(result,"jog_id") ?? throw new InvalidOperationException("设备未返回连续运动编号。");
            var trackingFollow=ReadBoolean(result,"tracking_follow")==true;
            if(!_manualJogHeld) stopImmediately=true;
            else
            {
                _manualJogKeepaliveCancellation?.Cancel();
                _manualJogKeepaliveCancellation=new CancellationTokenSource();
                _=KeepaliveManualJogAsync(_manualJogId,_manualJogKeepaliveCancellation.Token);
                _manualJogStatus=$"正在连续{(axis=="ZOOM"?"变倍":"聚焦")} {(direction=="positive"?"+":"-")}（{gear}档）{(axis=="ZOOM"?(trackingFollow?"，FOCUS随动已生效":"，当前为单轴变倍") : "")}；松开按钮立即停止。";
                OnPropertyChanged(nameof(ManualJogStatus));
            }
        }
        catch(Exception ex)
        {
            _manualJogHeld=false;_manualJogStatus=$"连续运动未启动：{ex.Message}";OnPropertyChanged(nameof(ManualJogStatus));OnPropertyChanged(nameof(CanManualJog));OnPropertyChanged(nameof(CanAdjustManualJogSpeed));
            try { await _connection.RequestStopAllAsync("jog_start_uncertain"); } catch { }
        }
        finally { _manualJogGate.Release(); }
        if(stopImmediately) await EndManualJogAsync("按钮已松开");
    }

    private async Task KeepaliveManualJogAsync(string jogId,CancellationToken cancellationToken)
    {
        ulong sequence=0;
        try
        {
            using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            while(await timer.WaitForNextTickAsync(cancellationToken))
            {
                if(!_manualJogHeld || _manualJogId!=jogId) return;
                await _connection.KeepaliveJogAsync(jogId,++sequence,cancellationToken);
            }
        }
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) { }
        catch(Exception ex)
        {
            OnUi(() => { _manualJogStatus=$"连续运动保活中断：{ex.Message}；设备看门狗将停止电机。";OnPropertyChanged(nameof(ManualJogStatus));OnPropertyChanged(nameof(CanManualJog));OnPropertyChanged(nameof(CanAdjustManualJogSpeed)); });
            _manualJogHeld=false;
            try { await _connection.RequestStopAllAsync("jog_keepalive_failed",CancellationToken.None); } catch { }
        }
    }

    public async Task EndManualJogAsync(string reason="按钮已松开")
    {
        _manualJogHeld=false;_manualJogKeepaliveCancellation?.Cancel();
        await _manualJogGate.WaitAsync();
        try
        {
            var jogId=_manualJogId;_manualJogId=null;
            if(jogId is not null)
            {
                try { await _connection.StopJogAsync(jogId); }
                catch
                {
                    try { await _connection.RequestStopAllAsync("jog_stop_failed"); } catch { }
                }
            }
            _manualJogStatus=$"{reason}，停止请求已发送；请观察位置反馈确认停稳。";
            OnPropertyChanged(nameof(ManualJogStatus));
            OnPropertyChanged(nameof(CanManualJog));OnPropertyChanged(nameof(CanAdjustManualJogSpeed));
        }
        finally { _manualJogGate.Release(); }
    }

    public async Task StartAutofocusAsync()
    {
        if (!CanStartAutofocus)
            return;
        await RunOperationAsync(async () =>
        {
            _autofocusResult = null;
            OnPropertyChanged(nameof(AutofocusResultText));
            var result = await _connection.StartAutofocusAsync();
            _lastTaskId = ReadString(result, "task_id");
            _lastTaskKind = "af";
            _latestTaskStatus = result;
            var state = ReadString(result, "state") ?? "设备已受理";
            _statusNotice = _lastTaskId is null
                ? $"AF 请求已响应（{state}），但响应没有 task_id。"
                : $"设备已受理 AF 任务：{_lastTaskId}（{state}）。结果以设备任务状态为准。";
            AddActivity(_statusNotice);
            if (_lastTaskId is null)
            {
                await RequestStopAfterFailureAsync("workbench_missing_task_id");
                return;
            }
            StartTaskMonitor(_lastTaskId, "af");
        }, stopOnFailure: true);
    }

    public async Task AcquireSceneControlAsync()
    {
        if (!CanAcquireSceneControl)
            return;
        await RunOperationAsync(async () =>
        {
            var lease = await _connection.AcquireControlAsync("scene_init");
            _lease = lease;
            _statusNotice = $"已获取场景初始化租约，epoch {lease.ControlEpoch}。";
            AddActivity(_statusNotice);
            await _connection.RefreshSnapshotAsync();
        });
    }

    public async Task StartSceneInitializationAsync()
    {
        if (!CanStartSceneInitialization || !int.TryParse(SceneBudgetMilliseconds, out var budget))
            return;
        await RunOperationAsync(async () =>
        {
            var result = await _connection.StartSceneInitializationAsync(budget);
            _lastTaskId = ReadString(result, "task_id");
            _lastTaskKind = "scene";
            _latestTaskStatus = result;
            var state = ReadString(result, "state") ?? "设备已受理";
            _statusNotice = _lastTaskId is null
                ? $"场景初始化请求已响应（{state}），但响应没有 task_id。"
                : $"设备已受理场景初始化任务：{_lastTaskId}（{state}）。范围为全光学行程；这不代表验收通过。";
            AddActivity(_statusNotice);
            if (_lastTaskId is null)
            {
                await RequestStopAfterFailureAsync("workbench_missing_task_id");
                return;
            }
            StartTaskMonitor(_lastTaskId, "scene");
        }, stopOnFailure: true);
    }

    public async Task StartFactoryCalibrationAsync()
    {
        if (!CanStartFactoryCalibration)
            return;
        if (!TryBuildFactoryRecipe(out var recipe, out var validationError))
        {
            _statusNotice = validationError;
            OnPropertyChanged(nameof(StatusNotice));
            OnPropertyChanged(nameof(CanStartFactoryCalibration));
            OnPropertyChanged(nameof(FactoryNodeLimitLabel));
            OnPropertyChanged(nameof(FactorySweepGearVisibility)); OnPropertyChanged(nameof(FactoryAdaptiveControlsVisibility)); OnPropertyChanged(nameof(FactorySingleSweepVisibility)); OnPropertyChanged(nameof(FactoryAcquisitionNodesLabel)); OnPropertyChanged(nameof(FactorySweepStrategyLabel)); OnPropertyChanged(nameof(FactorySweepStrategyDescription));
        OnPropertyChanged(nameof(FactoryReadinessText));
            return;
        }

        await RunOperationAsync(async () =>
        {
            _factoryValidation = null;
            _factoryResult = null;
            _factoryRecipeHash = null;
            OnPropertyChanged(nameof(FactoryCalibrationStatusText));

            var validation = await _connection.ValidateFactoryRecipeAsync(recipe);
            var recipeHash = ReadString(validation, "recipe_sha256");
            if (string.IsNullOrWhiteSpace(recipeHash))
                throw new InvalidOperationException("factory.validate_recipe 未返回 recipe_sha256，无法安全启动同一份配方。");
            _factoryValidation = validation.Clone();
            _factoryRecipeHash = recipeHash;
            _statusNotice = $"单物距配方预检通过：hash {recipeHash}；设备将执行 {ReadScalarText(validation, "planned_nodes") ?? "未知"} 个节点。";
            AddActivity(_statusNotice);
            OnPropertyChanged(nameof(FactoryCalibrationStatusText));

            var lease = await _connection.AcquireControlAsync("factory");
            _lease = lease;
            _statusNotice = $"已获取工厂标定租约，epoch {lease.ControlEpoch}；正在刷新快照并订阅事件。";
            AddActivity(_statusNotice);
            await _connection.RefreshSnapshotAndResubscribeAsync();
            if (!FactoryHardwareReady() || HasActiveTask() || !NoActiveTaskConfirmed())
            {
                var reason = !NoActiveTaskConfirmed() || HasActiveTask()
                    ? "设备仍有活动任务"
                    : !AxesPositionAvailable() && _device?.DataSource != DataSourceKind.Simulator
                        ? AxesReadinessProblems()
                        : "设备机械、图像或行程状态未就绪";
                await _connection.ReleaseControlAsync();
                _lease = null;
                _statusNotice = $"未发送 factory.start：{reason}；本次工厂租约已释放。";
                AddActivity(_statusNotice);
                return;
            }

            _profileTaskOperation=_profileTaskId=null;
            var result = Supports("profile.list")
                ? await _connection.StartNamedFactoryCalibrationAsync(recipe,recipeHash,SelectedProfile?.Id,string.IsNullOrWhiteSpace(ProfileName)||ProfileName.Trim()=="新物距标定"?$"{recipe["distance_layers"]!.AsArray()[0]!["measured_distance_m"]!.GetValue<double>().ToString("0.###",CultureInfo.InvariantCulture)} m":ProfileName.Trim())
                : await _connection.StartFactoryCalibrationAsync(recipe, recipeHash);
            _runningProfileId=ReadString(result,"profile_id");
            _runningLensModel=FactoryLensModel;
            _lastTaskId = ReadString(result, "task_id");
            _lastTaskKind = "factory";
            _latestTaskStatus = result;
            var state = ReadString(result, "state") ?? "设备已受理";
            _statusNotice = _lastTaskId is null
                ? $"factory.start 已响应（{state}），但响应没有 task_id。"
                : $"设备已受理单物距标定任务：{_lastTaskId}（{state}）；结果通过 factory.get_result 核对。";
            AddActivity(_statusNotice);
            if (_lastTaskId is null)
            {
                await RequestStopAfterFailureAsync("workbench_missing_factory_task_id");
                return;
            }
            Charts.Begin(_lastTaskId);MainSelectedTabIndex=1;
            StartTaskMonitor(_lastTaskId, "factory");
        }, stopOnFailure: true);
    }

    public async Task RefreshTaskAsync()
    {
        var taskId = CurrentTaskId();
        if (!CanRefreshTask || taskId is null)
            return;
        await RunOperationAsync(async () =>
        {
            _latestTaskStatus = _lastTaskKind == "factory"
                ? await _connection.GetFactoryResultAsync(taskId)
                : await _connection.GetTaskAsync(taskId);
            _lastTaskId = ReadString(_latestTaskStatus.Value, "task_id") ?? taskId;
            if (_lastTaskKind == "factory")
            {
                _factoryResult = _latestTaskStatus.Value.Clone();
                OnPropertyChanged(nameof(FactoryCalibrationStatusText));
            }
            _statusNotice = $"已刷新任务 {taskId}：{FormatTaskObject(_latestTaskStatus.Value)}。";
            AddActivity(_statusNotice);
        }, stopOnFailure: true);
    }

    public async Task CancelCurrentTaskAsync()
    {
        var taskId = CurrentTaskId();
        if (!CanCancelTask || taskId is null)
            return;
        await RunOperationAsync(async () =>
        {
            await _connection.CancelTaskAsync(taskId, "operator_cancel");
            _statusNotice = $"已发送任务 {taskId} 的取消请求；正在核对设备任务和轴位置。";
            OnPropertyChanged(nameof(StatusNotice));
            AddActivity(_statusNotice);
            JsonElement? finalObservation = null;
            try
            {
                if (Supports("task.get"))
                {
                    for (var attempt = 0; attempt < 10; attempt++)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(500));
                        var observation = await _connection.GetTaskAsync(taskId);
                        finalObservation = observation;
                        var observedState = ReadString(observation, "state");
                        if (observedState is "SUCCEEDED" or "PARTIAL" or "FAILED" or "CANCELLED" or "ABORTED_RESTART")
                            break;
                    }
                }
                if (finalObservation is { } observedTask)
                    _latestTaskStatus = observedTask;
            }
            finally
            {
                if (Supports("state.get_snapshot"))
                    await _connection.RefreshSnapshotAsync();
            }

            var state = finalObservation is { } taskObservation ? ReadString(taskObservation, "state") : null;
            var isTerminal = state is "SUCCEEDED" or "PARTIAL" or "FAILED" or "CANCELLED" or "ABORTED_RESTART";
            var stateNote = state is null ? string.Empty : $"（当前 {state}）";
            _statusNotice = isTerminal
                ? $"任务 {taskId} 的设备终态为 {state}；已刷新位置快照，请人工确认双轴停稳。"
                : $"已发送任务 {taskId} 的取消请求；取消终态未确认{stateNote}，已刷新位置快照并继续监听设备事件。";
            AddActivity(_statusNotice);
        }, stopOnFailure: true);
    }

    public async Task ReadAutofocusResultAsync()
    {
        var taskId = CurrentTaskId();
        if (!CanReadAutofocusResult || taskId is null)
            return;
        await RunOperationAsync(async () =>
        {
            var result = await _connection.GetAutofocusResultAsync(taskId);
            ApplyAutofocusResult(result);
            AddActivity(_statusNotice);
        }, stopOnFailure: true);
    }

    public async Task RefreshTrackingAsync()
    {
        if (!CanRefreshTracking) return;
        await RunOperationAsync(async () => {
            await _connection.RefreshSnapshotAsync();
            ApplyTrackingState(await _connection.GetTrackingAsync(),true);
        });
    }
    public async Task EnableTrackingAsync()
    {
        if (!CanEnableTracking) return;
        await RunOperationAsync(async () => {
            var ratios=ParseTrackingRatios();
            if(!int.TryParse(TrackingDefaultGear,NumberStyles.Integer,CultureInfo.InvariantCulture,out var gear) || gear is < 1 or > 9)
                throw new InvalidOperationException("默认ZOOM档位须为1～9，建议先使用1档。");
            var state=await _connection.GetTrackingAsync();
            await _connection.SetTrackingConfigurationAsync(TrackingEnabled,ReadScalarText(state,"revision") ?? throw new InvalidOperationException("设备未返回联动版本。"),gear,ratios);
            ApplyTrackingState(await _connection.GetTrackingAsync(),true);
            AddActivity("联动开关、默认变倍档位和九档FOCUS速度比例已保存到设备，重启后仍有效。");
        });
    }
    public async Task StartTrackingAsync(bool verify)
    {
        if (verify ? !CanVerifyTracking : !CanMoveTracking) return;
        await RunOperationAsync(async () => {
            var target=verify?0:int.Parse(TrackingZoomTarget,CultureInfo.InvariantCulture);
            var result=await _connection.StartTrackingAsync(target,verify);
            _lastTaskId=ReadScalarText(result,"task_id") ?? throw new InvalidOperationException("设备没有返回联动任务。");
            _lastTaskKind="tracking"; _latestTaskStatus=result.Clone();
            _statusNotice=verify?"设备已受理全程低速试运行，将先验证双轴运动能力。":"设备已受理目标位置联动。";
            AddActivity(_statusNotice); StartTaskMonitor(_lastTaskId,"tracking");
        },stopOnFailure:true);
    }
    public async Task StopTrackingAsync()
    {
        if (!CanStopTracking || CurrentTaskId() is not { } task) return;
        await RunOperationAsync(async () => { await _connection.StopTrackingAsync(task); AddActivity("已请求停止联动，等待设备任务终态。"); },stopOnFailure:true);
    }

    public async Task ReadTemperatureAsync()
    {
        if (!CanReadTemperature)
            return;
        await RunOperationAsync(async () =>
        {
            _temperature = await _connection.GetTemperatureAsync();
            OnPropertyChanged(nameof(TemperatureStatus));
            _statusNotice = $"已读取温度快照：{TemperatureStatus}。";
            AddActivity(_statusNotice);
        });
    }

    public async Task RefreshSnapshotAsync()
    {
        if (!CanRefreshSnapshot)
            return;
        await RunOperationAsync(async () =>
        {
            await _connection.RefreshSnapshotAsync();
            if (Supports("af.get_config")) await ReadAutofocusConfigurationCoreAsync();
            _statusNotice = AxesPositionAvailable()
                ? "已读取设备缓存状态；请人工确认双轴停稳后再开始单物距标定。"
                : $"状态已刷新；位置前置条件未满足：{AxesReadinessProblems()}。停稳由人工确认。";
            AddActivity(_statusNotice);
        });
    }

    public async Task QueryUncertainRequestAsync()
    {
        if (!CanQueryRequestStatus || _uncertainRequestIds.Count == 0)
            return;
        var requestId = _uncertainRequestIds[^1];
        await RunOperationAsync(async () =>
        {
            var response = await _connection.GetRequestStatusAsync(requestId);
            ApplyRequestStatus(requestId, response);
            AddActivity(_statusNotice);
        });
    }

    private void ApplyRequestStatus(Guid requestId, JsonElement response)
    {
        if (ReadString(response, "status") == "error")
        {
            var error = response.TryGetProperty("error", out var errorElement) ? errorElement : default;
            _statusNotice = $"请求 {requestId} 状态查询失败：{ReadString(error, "code") ?? "UNKNOWN_ERROR"}。原操作结果仍未知。";
            return;
        }

        if (!response.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
        {
            _statusNotice = $"请求 {requestId} 的状态响应格式未知；不会重发原操作。";
            return;
        }

        JsonElement? originalResponse = result.TryGetProperty("original_response", out var original)
            && original.ValueKind == JsonValueKind.Object
                ? original
                : null;
        var originalStatus = originalResponse is { } originalValue ? ReadString(originalValue, "status") : null;
        var originalResult = originalResponse is { } originalEnvelope
            && originalEnvelope.TryGetProperty("result", out var originalResultElement)
                ? originalResultElement
                : result;
        var taskId = ReadString(originalResult, "task_id") ?? ReadString(result, "task_id");

        if (originalStatus == "error")
        {
            var error = originalResponse is { } errorEnvelope && errorEnvelope.TryGetProperty("error", out var errorElement)
                ? errorElement
                : default;
            _statusNotice = $"原请求 {requestId} 已确认失败：{ReadString(error, "code") ?? "UNKNOWN_ERROR"}。";
            ClearUncertainRequest(requestId);
            return;
        }

        if (taskId is not null)
        {
            _lastTaskId = taskId;
            _lastTaskKind = UncertainMethod(requestId) switch
            {
                "af.start" => "af",
                "scene.initialize" => "scene",
                "lens.activate" or "lens.initialize" => "lens",
                "tracking.move" or "tracking.verify" => "tracking",
                "profile.activate" or "profile.verify" or "profile.update_nodes" => "factory",
                _ => _lastTaskKind
            };
            _latestTaskStatus = originalResult.Clone();
            if(_lastTaskKind=="lens") {
                _lensTaskId=taskId;_lensTaskModel=ReadString(originalResult,"lens_model")??_lensTaskModel;
                StartTaskMonitor(taskId,"lens");
            }
            if(UncertainMethod(requestId)=="profile.update_nodes") {
                _runningLensModel=ReadString(originalResult,"lens_model");_runningProfileId=ReadString(originalResult,"profile_id");
                _profileTaskOperation="edit";_profileTaskId=taskId;
                _curveSavingModelId=ReadString(originalResult,"new_model_id");
                Charts.MarkCurveSavePending();StartTaskMonitor(taskId,"factory");
            }
            _statusNotice = $"原请求 {requestId} 已确认；设备任务 ID 为 {taskId}，后续以设备任务状态为准。";
            ClearUncertainRequest(requestId);
            RefreshOperationProperties();
            return;
        }

        if (originalResponse is not null)
        {
            _statusNotice = $"原请求 {requestId} 已收到设备响应（{originalStatus ?? "状态未知"}）；{UncertainMethod(requestId)} 不会自动重发。";
            ClearUncertainRequest(requestId);
            return;
        }

        var state = ReadString(result, "state") ?? ReadString(result, "status") ?? result.GetRawText();
        _statusNotice = $"请求 {requestId} 查询结果：{state}。尚未取回原响应，仍不要重发操作。";
    }

    private void ClearUncertainRequest(Guid requestId)
    {
        if (!_uncertainRequestMethods.ContainsKey(requestId))
            return;
        _uncertainRequestIds.Remove(requestId);
        _uncertainRequestMethods.Remove(requestId);
        RefreshOperationProperties();
    }

    private void RememberUncertainRequest(Guid requestId, string method)
    {
        if (_uncertainRequestMethods.TryAdd(requestId, method))
            _uncertainRequestIds.Add(requestId);
        RefreshOperationProperties();
    }

    private string UncertainMethod(Guid requestId) =>
        _uncertainRequestMethods.TryGetValue(requestId, out var method) ? method : "操作";

    private async Task RunOperationAsync(Func<Task> operation, bool stopOnFailure = false)
    {
        _operationBusy = true;
        RefreshOperationProperties();
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            if (stopOnFailure)
                await RequestStopAfterFailureAsync("workbench_operation_error");
            if (ex is RequestTimeoutException timeout)
            {
                if (timeout.Method != "request.get_status")
                {
                    RememberUncertainRequest(timeout.RequestId, timeout.Method);
                }
                _statusNotice = timeout.Method == "request.get_status"
                    ? $"超时请求状态查询未获响应；原操作 {(_uncertainRequestIds.Count > 0 ? UncertainMethod(_uncertainRequestIds[^1]) : "未知")} 的结果仍未知，不要重发。"
                    : $"设备操作响应超时，执行结果未知（request_id {timeout.RequestId}）。不要盲目重试；可查询原请求状态。";
            }
            else
            {
                _statusNotice = $"设备操作未完成：{ex.Message}";
            }
            AddActivity(_statusNotice);
        }
        finally
        {
            _operationBusy = false;
            OnPropertyChanged(nameof(StatusNotice));
            RefreshOperationProperties();
        }
    }

    private void StartTaskMonitor(string taskId, string taskKind)
    {
        _taskMonitorCancellation?.Cancel();
        while (_taskRefreshNotifications.Reader.TryRead(out _)) { }

        var cancellation = new CancellationTokenSource();
        _taskMonitorCancellation = cancellation;
        _factoryTaskFinalizing = taskKind is "factory" or "lens";
        RefreshOperationProperties();
        _taskMonitorTask = MonitorTaskAsync(taskId, taskKind, cancellation.Token);
    }

    private async Task StopTaskMonitorAsync()
    {
        var cancellation = _taskMonitorCancellation;
        _taskMonitorCancellation = null;
        cancellation?.Cancel();
        var monitor = _taskMonitorTask;
        _taskMonitorTask = null;
        if (monitor is not null)
        {
            try { await monitor.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch { }
        }
        cancellation?.Dispose();
    }

    private async Task MonitorTaskAsync(string taskId, string taskKind, CancellationToken cancellationToken)
    {
        var canReadAutofocusResult = Supports("af.get_result");
        var canReadFactoryResult = Supports("factory.get_result");
        var profileActivation=_profileTaskId==taskId && _profileTaskOperation is "activate" or "edit";
        var nextTaskPollAt = DateTimeOffset.UtcNow.AddMilliseconds(400);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await WaitForTaskEventOrPollAsync(nextTaskPollAt, cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                    return;

                JsonElement task;
                try
                {
                    task = taskKind == "factory"
                        ? await _connection.GetFactoryResultAsync(taskId, cancellationToken).ConfigureAwait(false)
                        : await _connection.GetTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    await RequestStopAfterFailureAsync("workbench_task_query_error").ConfigureAwait(false);
                    OnUi(() =>
                    {
                        _statusNotice = $"任务 {taskId} 状态查询异常：{ex.Message}；已请求立即停止，任务终态仍需设备确认。";
                        AddActivity(_statusNotice);
                        OnPropertyChanged(nameof(StatusNotice));
                    });
                    return;
                }
                if(taskKind=="factory" && !profileActivation && Supports("factory.get_nodes") && Supports("factory.get_samples"))
                {
                    try { await ReadFactoryChartsAsync(taskId, false, cancellationToken).ConfigureAwait(false); }
                    catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) { return; }
                    catch(Exception ex) { OnUi(()=>Charts.SetError($"曲线读取失败，将保留游标重试：{ex.Message}")); }
                }
                if(taskKind=="tracking" && Supports("tracking.get")) {
                    var tracking=await _connection.GetTrackingAsync(cancellationToken).ConfigureAwait(false);
                    OnUi(()=> { ApplyTrackingState(tracking); });
                }
                nextTaskPollAt = DateTimeOffset.UtcNow.AddMilliseconds(400);

                var state = ReadString(task, "state");
                var isTerminal = state is "SUCCEEDED" or "PARTIAL" or "FAILED" or "CANCELLED" or "ABORTED_RESTART";
                if (taskKind is not ("factory" or "tracking") && !isTerminal && Supports("state.get_snapshot"))
                {
                    bool positionSafe;
                    try
                    {
                        positionSafe = await RefreshAndCheckPositionSafetyAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        await RequestStopAfterFailureAsync("workbench_position_check_error").ConfigureAwait(false);
                        OnUi(() =>
                        {
                            _statusNotice = $"任务 {taskId} 位置核对异常：{ex.Message}；已请求立即停止。";
                            AddActivity(_statusNotice);
                            OnPropertyChanged(nameof(StatusNotice));
                        });
                        return;
                    }

                    if (!positionSafe)
                    {
                        await RequestStopAfterFailureAsync("workbench_position_abnormal").ConfigureAwait(false);
                        OnUi(() =>
                        {
                            _statusNotice = $"任务 {taskId} 位置数据无效、过期或越界；已请求立即停止，等待设备确认。";
                            AddActivity(_statusNotice);
                            OnPropertyChanged(nameof(StatusNotice));
                        });
                        return;
                    }
                }
                OnUi(() =>
                {
                    if (_lastTaskId != taskId)
                        return;
                    _latestTaskStatus = task.Clone();
                    if(taskKind=="lens") ApplyLensTaskProgress(task);
                    if (taskKind == "factory")
                    {
                        _factoryResult = task.Clone();
                        OnPropertyChanged(nameof(FactoryCalibrationStatusText));
                    }
                    RefreshOperationProperties();
                    if (isTerminal)
                    {
                        _statusNotice = $"设备任务 {taskId} 已进入终态 {state ?? "未知"}；正在读取最终结果。";
                        AddActivity(_statusNotice);
                        OnPropertyChanged(nameof(StatusNotice));
                    }
                });

                if (!isTerminal)
                    continue;

                if (Supports("state.get_snapshot"))
                {
                    try
                    {
                        await _connection.RefreshSnapshotAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        await RequestStopAfterFailureAsync("workbench_terminal_snapshot_error").ConfigureAwait(false);
                        OnUi(() =>
                        {
                            _statusNotice = $"任务 {taskId} 已进入终态，但最终位置快照读取失败：{ex.Message}；已请求立即停止。";
                            AddActivity(_statusNotice);
                            OnPropertyChanged(nameof(StatusNotice));
                        });
                        return;
                    }
                }

                if (taskKind == "af" && canReadAutofocusResult && !cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        var result = await _connection.GetAutofocusResultAsync(taskId, cancellationToken).ConfigureAwait(false);
                        OnUi(() =>
                        {
                            if (_lastTaskId != taskId)
                                return;
                            ApplyAutofocusResult(result);
                            AddActivity(_statusNotice);
                        });
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                    catch (Exception ex)
                    {
                        await RequestStopAfterFailureAsync("workbench_af_result_error").ConfigureAwait(false);
                        OnUi(() =>
                        {
                            _statusNotice = $"AF 终态已确认，但结果读取失败：{ex.Message}；已请求立即停止。";
                            AddActivity(_statusNotice);
                            OnPropertyChanged(nameof(StatusNotice));
                        });
                    }
                }
                else if (taskKind == "factory" && canReadFactoryResult && !cancellationToken.IsCancellationRequested)
                {
                    OnUi(() =>
                    {
                        if (_lastTaskId != taskId)
                            return;
                        _factoryResult = task.Clone();
                        _statusNotice = FormatFactoryCalibrationResult();
                        AddActivity(_statusNotice);
                        OnPropertyChanged(nameof(FactoryCalibrationStatusText));
                        OnPropertyChanged(nameof(StatusNotice));
                    });
                }

                if(taskKind=="lens" && !cancellationToken.IsCancellationRequested)
                    await FinishLensTaskAsync(taskId,task,cancellationToken).ConfigureAwait(false);

                // A factory lease is needed only while the asynchronous task is running.
                // Release it after the terminal result so a failed run cannot leave the
                // factory page locked until the operator performs a second manual action.
                if (taskKind == "factory" && !cancellationToken.IsCancellationRequested)
                {
                    await ReleaseFactoryLeaseAfterTerminalAsync(taskId, cancellationToken).ConfigureAwait(false);
                    if(Supports("tracking.get")) {
                        try {
                            var tracking=await _connection.GetTrackingAsync(cancellationToken).ConfigureAwait(false);
                            OnUi(()=> { ApplyTrackingState(tracking,true); });
                        }
                        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) { return; }
                        catch(Exception ex) { OnUi(()=>AddActivity($"标定已结束，联动状态读取失败：{ex.Message}；可刷新重试。")); }
                    }
                    if(profileActivation && _profileTaskOperation!="edit" && _runningProfileId is { } profileId && Supports("profile.get")) {
                        try { var profile=await _connection.GetProfileAsync(_runningLensModel ?? FactoryLensModel,profileId,cancellationToken).ConfigureAwait(false);OnUi(()=> { if(_lastTaskId==taskId) {Charts.ShowProfile(profile);_profileDetails=$"{ReadString(profile,"name")} · {(ReadBoolean(profile,"active")==true?"当前已激活":"激活未完成，保留原模型")}";OnPropertyChanged(nameof(ProfileDetails));} }); }
                        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) { return; }
                        catch(Exception ex) { OnUi(()=>AddActivity($"激活任务已结束，模型读取失败：{ex.Message}")); }
                    }
                    if(!profileActivation && Supports("factory.get_nodes") && Supports("factory.get_samples")) {
                        try { await ReadFactoryChartsAsync(taskId,true,cancellationToken).ConfigureAwait(false); }
                        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) { return; }
                        catch(Exception ex) { OnUi(()=>Charts.SetError($"终态已确认，完整记录尚未读取：{ex.Message}")); }
                    }
                    if(Supports("profile.list") && _runningLensModel is { } lensModel) {
                        try { await RefreshProfilesAfterTaskAsync(lensModel,_runningProfileId,cancellationToken,Charts.TaskId==taskId && !profileActivation).ConfigureAwait(false); }
                        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) { return; }
                        catch(Exception ex) { OnUi(()=>AddActivity($"任务完成，配置列表刷新失败：{ex.Message}")); }
                    }
                }
                return;
            }
        }
        finally
        {
            if (taskKind is "factory" or "lens")
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                    if (_lastTaskId != taskId) return;
                    _factoryTaskFinalizing = false;
                    if(_profileTaskOperation=="edit" && Charts.IsSavingCurve)
                        Charts.FinishCurveSave(_factoryResult is { } editResult && ReadString(editResult,"state")=="SUCCEEDED"
                            ? "设备已保存，回读未完成；请取消本地编辑后刷新确认最新曲线。"
                            : "保存未成功，草稿仍保留，可继续调整或取消。 "+(_factoryResult is { } failureResult?ReadString(failureResult,"error_message"):""));
                    RefreshOperationProperties();
                });
            // The owning cancellation source is cleared by StopTaskMonitorAsync or by the next task.
        }
    }

    private async Task WaitForTaskEventOrPollAsync(DateTimeOffset nextPollAt, CancellationToken cancellationToken)
    {
        var remaining = nextPollAt - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            await Task.Yield();
            return;
        }

        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tick = Task.Delay(remaining, waitCancellation.Token);
        var taskEvent = _taskRefreshNotifications.Reader.ReadAsync(waitCancellation.Token).AsTask();
        try
        {
            await Task.WhenAny(tick, taskEvent).ConfigureAwait(false);
        }
        finally
        {
            waitCancellation.Cancel();
            try { await Task.WhenAll(tick, taskEvent).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private async Task ReadFactoryChartsAsync(string taskId, bool drain, CancellationToken cancellationToken)
    {
        if(Charts.TaskId!=taskId) return;
        // 只读分页下载；不参与设备运动控制或模型计算。
        bool samplesMore;
        var count=0;
        do {
            var cursor=Charts.SampleCursor;
            var page=await _connection.GetFactorySamplesAsync(taskId,cursor,cancellationToken:cancellationToken).ConfigureAwait(false);
            samplesMore=FactoryDataReader.Boolean(page,"has_more");
            var next=FactoryDataReader.Text(page,"next_cursor",cursor);
            if(samplesMore && next==cursor) throw new InvalidOperationException("设备样本分页游标未前进。");
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(()=> { if(Charts.TaskId==taskId) Charts.ApplySamples(page); });
        } while(samplesMore && (drain || ++count<10) && !cancellationToken.IsCancellationRequested);
        bool nodesMore;
        do {
            var cursor=Charts.NodeCursor;
            var page=await _connection.GetFactoryNodesAsync(taskId,cursor,cancellationToken:cancellationToken).ConfigureAwait(false);
            nodesMore=FactoryDataReader.Boolean(page,"has_more");
            var next=FactoryDataReader.Text(page,"next_cursor",cursor);
            if(nodesMore && next==cursor) throw new InvalidOperationException("设备节点分页游标未前进。");
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(()=> { if(Charts.TaskId==taskId) Charts.ApplyNodes(page,cursor=="0"); });
        } while(nodesMore && !cancellationToken.IsCancellationRequested);
        // 设备端生成最新 PCHIP 绘图点，不在客户端拟合标定真值。
        if(Charts.NodeCursor!="0") {
            var model=await _connection.GetFactoryNodesAsync(taskId,"0",1,cancellationToken).ConfigureAwait(false);
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(()=> {
                if(Charts.TaskId!=taskId) return;
                var saved=Charts.NodeCursor;
                // 仅替换曲线，避免把第一页节点重复加入。
                Charts.UpdateCurve(model); Charts.NodeCursor=saved;
            });
        }
        if(drain && !samplesMore && !nodesMore) await System.Windows.Application.Current.Dispatcher.InvokeAsync(()=> { if(Charts.TaskId==taskId) Charts.MarkComplete(); });
    }

    private async Task ReleaseFactoryLeaseAfterTerminalAsync(string taskId, CancellationToken cancellationToken)
    {
        try
        {
            await _connection.ReleaseControlAsync(cancellationToken).ConfigureAwait(false);
            OnUi(() =>
            {
                _lease = null;
                var finalState = _factoryResult is { } finalResult ? ReadString(finalResult, "state") ?? "未知"
                    : _latestTaskStatus is { } latestStatus ? ReadString(latestStatus, "state") ?? "未知" : "未知";
                _statusNotice = $"工厂任务 {taskId} 已结束（{finalState}），已释放工厂控制租约。";
                if (finalState is not "SUCCEEDED" && _factoryResult is { } failedResult)
                    _statusNotice += " " + ExplainFactoryFailure(ReadScalarText(failedResult, "error_code") ?? "未知", ReadString(failedResult, "error_message"));
                AddActivity(_statusNotice);
                OnPropertyChanged(nameof(StatusNotice));
                RefreshOperationProperties();
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Disconnect or an explicit monitor stop owns lease cleanup in this path.
        }
        catch (Exception ex)
        {
            // ReleaseControlAsync clears the client lease in its finally block even when
            // the device has already released the task's local lease.
            OnUi(() =>
            {
                _lease = null;
                _statusNotice = $"工厂任务 {taskId} 已终态，但自动释放租约返回：{ex.Message}；本地租约已清理。";
                AddActivity(_statusNotice);
                OnPropertyChanged(nameof(StatusNotice));
                RefreshOperationProperties();
            });
        }
    }

    private async Task RequestStopAfterFailureAsync(string reason)
    {
        if (_state != ConnectionState.Connected
            || !Supports("motion.stop_all")
            || _device?.Role is not ("operator" or "factory" or "engineer"))
            return;
        try
        {
            await _connection.RequestStopAllAsync(reason).ConfigureAwait(false);
            OnUi(() => AddActivity("异常路径已发送立即停止请求；请人工确认双轴停稳。"));
        }
        catch (Exception stopException)
        {
            OnUi(() => AddActivity($"异常路径的立即停止请求未确认：{stopException.Message}"));
        }
    }

    private async Task<bool> RefreshAndCheckPositionSafetyAsync(CancellationToken cancellationToken)
    {
        await _connection.RefreshSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return await System.Windows.Application.Current.Dispatcher
            .InvokeAsync(AxesPositionSafe)
            .Task.ConfigureAwait(false);
    }

    private void ApplyAutofocusResult(JsonElement result)
    {
        _autofocusResult = result.Clone();
        var success = ReadBoolean(result, "success");
        var zoom = ReadScalarText(result, "zoom_position") ?? "未知";
        var focus = ReadScalarText(result, "best_focus_position") ?? "未知";
        var score = ReadAnyScalarText(result, "clarity_score", "sharpness_score", "score", "best_score") ?? "未知";
        var confidence = ReadAnyScalarText(result, "confidence", "confidence_score") ?? "未知";
        var errorCode = ReadScalarText(result, "error_code") ?? "未知";
        var restore = ReadAnyScalarText(result, "restore_result", "restore_status", "restored_to_safe_position", "safe_position", "stopped_confirmed") ?? "未知";
        var searchSummary = FormatAutofocusSearchSummary(result);
        _statusNotice = searchSummary is not null
            ? $"AF 结果：{searchSummary}；变焦 raw {zoom}；错误码 {errorCode}；安全位置/停稳 {restore}。这不代表工厂光学验收通过。"
            : $"AF 结果：{success switch { true => "成功", false => "未成功", _ => "状态未知" }}；变焦 raw {zoom}；最佳焦点 raw {focus}；清晰度 {score}；置信度 {confidence}；错误码 {errorCode}；安全位置/停稳 {restore}。这不代表工厂光学验收通过。";
        OnPropertyChanged(nameof(AutofocusResultText));
        OnPropertyChanged(nameof(StatusNotice));
        RefreshOperationProperties();
    }

    private string FormatAutofocusResult()
    {
        if (_autofocusResult is not { } result)
            return "尚未取得 AF 最终结果";
        var success = ReadBoolean(result, "success");
        var zoom = ReadScalarText(result, "zoom_position") ?? "未知";
        var focus = ReadScalarText(result, "best_focus_position") ?? "未知";
        var score = ReadAnyScalarText(result, "clarity_score", "sharpness_score", "score", "best_score") ?? "未知";
        var confidence = ReadAnyScalarText(result, "confidence", "confidence_score") ?? "未知";
        var errorCode = ReadScalarText(result, "error_code") ?? "未知";
        var restore = ReadAnyScalarText(result, "restore_result", "restore_status", "restored_to_safe_position", "safe_position", "stopped_confirmed") ?? "未知";
        var searchSummary = FormatAutofocusSearchSummary(result);
        if (searchSummary is not null)
            return $"{searchSummary} · 变焦 raw {zoom} · 错误码 {errorCode} · 安全位置/停稳 {restore}";
        return $"success={success?.ToString() ?? "unknown"} · zoom_position={zoom} · best_focus_position={focus} · clarity={score} · confidence={confidence} · error_code={errorCode} · safe_position={restore}";
    }

    private bool AxesPositionAvailable() => AxisPositionAvailable("zoom") && AxisPositionAvailable("focus");

    private bool AxisPositionAvailable(string axisName) =>
        TryGetAxis(_snapshot, axisName, out var axis)
        && ReadBoolean(axis, "valid") == true
        && axis.TryGetProperty("position", out var position)
        && IsReportedPositionUsable(axis, position);

    private bool AxesPositionSafe() => AxisPositionSafe("zoom") && AxisPositionSafe("focus");

    private bool AxisPositionSafe(string axisName) =>
        AxisPositionAvailable(axisName)
        && TryGetAxis(_snapshot, axisName, out var axis)
        && TryGetEffectiveAxisAge(axis, out var age) && age <= 1500;

    private bool TryGetEffectiveAxisAge(JsonElement axis, out long age)
    {
        age = 0;
        if (!TryGetInt64(axis, "age_ms", out var deviceAge)
            || deviceAge < 0 || _snapshotReceivedTick is not { } receivedTick)
            return false;
        var elapsed = Math.Max(0, Environment.TickCount64 - receivedTick);
        age = deviceAge > long.MaxValue - elapsed ? long.MaxValue : deviceAge + elapsed;
        return true;
    }

    private static bool TryGetAxis(JsonElement? snapshot, string axisName, out JsonElement axis)
    {
        if (TryGetProperty(snapshot, "axes", out var axes)
            && axes.ValueKind == JsonValueKind.Object
            && axes.TryGetProperty(axisName, out axis))
            return true;
        if (TryGetProperty(snapshot, "lens", out var lens)
            && lens.ValueKind == JsonValueKind.Object
            && lens.TryGetProperty(axisName, out axis))
            return true;
        axis = default;
        return false;
    }

    private string FormatBoolean(string property)
    {
        var value = ReadBoolean(_snapshot, property);
        if (value is null && TryGetProperty(_snapshot, "config", out var config))
            value = ReadBoolean(config, property);
        return value switch
        {
            true => "就绪",
            false => "未就绪",
            _ => "未知"
        };
    }

    private string? AxisReadinessProblem(string axisName)
    {
        var name = axisName.ToUpperInvariant();
        if (!TryGetAxis(_snapshot, axisName, out var axis) || ReadBoolean(axis, "valid") != true
            || !axis.TryGetProperty("position", out var position) || !TryGetFiniteDouble(position, out _))
            return $"{name} 未提供有效位置反馈";
        if (!IsReportedPositionUsable(axis, position))
            return $"{name} 未提供有效的原始行程范围，请刷新设备状态核对行程";


        return null;
    }

    private string AxesReadinessProblems() => string.Join("；",
        new[] { AxisReadinessProblem("zoom"), AxisReadinessProblem("focus") }
            .Where(problem => problem is not null));

    private string FormatAxis(string axisName)
    {
        if (!TryGetAxis(_snapshot, axisName, out var axis)
            || ReadBoolean(axis, "valid") != true
            || !axis.TryGetProperty("position", out var position)
            || !TryGetFiniteDouble(position, out _))
            return "位置未知 · 停稳由人工确认";
        if (AxisReadinessProblem(axisName) is { } problem)
            return $"{problem} · 停稳由人工确认";
        var endpointNotice = string.Empty;
        if (TryGetFiniteDouble(position, out var rawPosition)
            && axis.TryGetProperty("min_position", out var minimum)
            && axis.TryGetProperty("max_position", out var maximum)
            && TryGetFiniteDouble(minimum, out var minimumPosition)
            && TryGetFiniteDouble(maximum, out var maximumPosition))
            endpointNotice = rawPosition < minimumPosition ? " · 低于下限，按下限端点处理"
                : rawPosition > maximumPosition ? " · 高于上限，按上限端点处理" : string.Empty;
        return $"{position} raw{endpointNotice} · 缓存数据龄 {(TryGetEffectiveAxisAge(axis, out var age) ? age.ToString(CultureInfo.InvariantCulture) : "未知")} ms · 停稳由人工确认";
    }

    private string FormatControl()
    {
        if (_lease is not null)
            return $"本会话持有租约 · epoch {_lease.ControlEpoch} · TTL {_lease.LeaseTtlMilliseconds} ms（自动续期）";
        var held = ReadBoolean(_snapshot, "control_held");
        if (held is null && TryGetProperty(_snapshot, "control", out var control))
            held = ReadBoolean(control, "held");
        if (held is null)
            return "租约状态未知";
        if (!held.Value)
            return "无控制租约";
        return "设备有控制租约；当前持有者未知";
    }

    private string FormatTemperature()
    {
        if (_temperature is not { } reading)
            return "尚未读取";
        if (ReadBoolean(reading, "valid") == false)
            return "传感器数据无效";
        var age = ReadString(reading, "age_ms");
        var source = ReadString(reading, "source");
        var suffix = age is null ? string.Empty : $" · 数据龄 {age} ms";
        if (reading.TryGetProperty("value_c", out var celsius) && celsius.ValueKind == JsonValueKind.Number)
            return $"{celsius.GetRawText()} °C{suffix}";
        if (reading.TryGetProperty("raw_value", out var raw) && raw.ValueKind == JsonValueKind.Number)
            return source is null
                ? $"原始值 {raw.GetRawText()}（单位未知）{suffix}"
                : $"原始值 {raw.GetRawText()}（单位未知 · {source}）{suffix}";
        return "没有有效温度读数";
    }

    private bool TryBuildFactoryRecipe(out JsonObject recipe, out string error)
    {
        recipe = new JsonObject();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(FactoryLensModel))
        {
            error = "请先选择或新建镜头型号。";
            return false;
        }
        if (string.IsNullOrWhiteSpace(FactoryTargetIdentifier))
        {
            error = "请填写本次标定的目标标识。";
            return false;
        }

        if (!double.TryParse(FactoryTargetDistanceMeters, NumberStyles.Float, CultureInfo.InvariantCulture, out var distance)
            || !double.IsFinite(distance) || distance <= 0)
        {
            error = "目标物距必须是大于 0 的有限米数。";
            return false;
        }
        if (!int.TryParse(FactorySampleCount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sampleCount)
            || sampleCount is < 1 or > 7)
        {
            error = "每个停稳位置的有效帧数必须是 1 到 7 的整数，不是扫焦次数。";
            return false;
        }
        var simple=FactorySingleSweep || FactoryUseHybrid && _device?.FactorySamplingStrategy=="hybrid_simple";
        var nodeLimit=simple?64:(FactoryBothDirections?16:33);
        if (!int.TryParse(FactorySingleSweep ? FactoryInitialNodes : FactoryMaxNodes, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxNodes)
            || maxNodes<2 || maxNodes>nodeLimit)
        {
            error = $"每方向节点上限必须是 2 到 {nodeLimit} 的整数。";
            return false;
        }
        if (!simple && FactoryBothDirections && maxNodes > 16)
        {
            error = "双向标定时节点数不能超过 16；这是设备端的单次任务限制。";
            return false;
        }
        if (!int.TryParse(FactoryBudgetMilliseconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var budget)
            || budget is < 1 or > 1_800_000)
        {
            error = "任务预算必须是 1 到 1800000 ms 的整数。";
            return false;
        }
        if (!double.TryParse(FactoryClearScoreRatio, NumberStyles.Float, CultureInfo.InvariantCulture, out var clearScoreRatio)
            || !double.IsFinite(clearScoreRatio) || clearScoreRatio <= 0 || clearScoreRatio >= 1)
        {
            error = "清晰度阈值比例必须在 0 和 1 之间（不含边界）。";
            return false;
        }

        double? ambientTemperature = null;
        if (!string.IsNullOrWhiteSpace(FactoryAmbientTemperatureC))
        {
            if (!double.TryParse(FactoryAmbientTemperatureC, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedAmbient)
                || !double.IsFinite(parsedAmbient))
            {
                error = "环境温度必须是有限的摄氏度数；如果设备端不提供该值可以留空。";
                return false;
            }
            ambientTemperature = parsedAmbient;
        }

        if(!int.TryParse(FactoryInitialNodes,NumberStyles.Integer,CultureInfo.InvariantCulture,out var initialNodes)
            || initialNodes<2 || initialNodes>maxNodes) { error=FactorySingleSweep?"每方向采集节点必须为 2 到 64 的整数。":"初始节点必须为 2 到节点上限之间的整数。"; return false; }
        var hybrid=(FactorySingleSweep || FactoryUseHybrid) && Supports("factory.get_nodes") && Supports("factory.get_samples");
        if(simple && !FactorySingleSweep && (sampleCount!=5 || Math.Abs(clearScoreRatio-0.95)>1e-9)) { error="简化标定固定使用 5 个新帧和 0.95 清晰评分比例。";return false; }
        var sweepGear=1;
        var sweepGearSupported=hybrid && _device?.FactorySweepGearSupported==true;
        if(sweepGearSupported && (!int.TryParse(FactorySweepGear,NumberStyles.Integer,CultureInfo.InvariantCulture,out sweepGear) || sweepGear is <1 or >9)) {
            error="FOCUS 扫焦档位必须为 1 到 9 的整数；1 为最低档。";return false;
        }
        if(!hybrid) maxNodes=initialNodes;
        var zoomNodes = new JsonArray();
        for (var index = 0; index < initialNodes; index++)
            zoomNodes.Add(JsonValue.Create((double)index / (initialNodes - 1)));

        var validationNodes = new JsonArray();
        for (var index = 0; index < initialNodes - 1; index++)
            validationNodes.Add(JsonValue.Create(((double)index + 0.5) / (initialNodes - 1)));

        var directions = new JsonArray { JsonValue.Create("wide_to_tele") };
        if (FactoryBothDirections)
            directions.Add(JsonValue.Create("tele_to_wide"));

        var distanceLayer = new JsonObject
        {
            ["layer_id"] = "D1",
            ["measured_distance_m"] = distance,
            ["usage"] = "training",
            ["target_identifier"] = FactoryTargetIdentifier.Trim()
        };
        recipe = new JsonObject
        {
            ["schema_version"] = 1,
            ["recipe_id"] = Guid.NewGuid().ToString(),
            ["kind"] = "type_reference",
            ["lens_model"] = FactoryLensModel.Trim(),
            ["distance_layers"] = new JsonArray(distanceLayer),
            ["directions"] = directions,
            ["sample_count"] = sampleCount,
            ["max_nodes"] = maxNodes,
            ["zoom_nodes"] = zoomNodes,
            ["validation_nodes"] = validationNodes,
            ["clear_score_ratio"] = clearScoreRatio,
            ["budget_ms"] = budget
        };
        if(hybrid) { recipe["sampling_strategy"]=simple?"hybrid_simple":"hybrid"; recipe["initial_nodes_per_direction"]=initialNodes; }
        if(sweepGearSupported) recipe["focus_sweep_gear"]=sweepGear;
        if(simple) recipe.Remove("validation_nodes");
        if (ambientTemperature is { } temperature)
            recipe["ambient_temperature_c"] = temperature;
        return true;
    }

    private bool FactoryHardwareReady() => _device?.DataSource == DataSourceKind.Simulator
        || (_device?.AxisRangesValid == true
            && ReadBoolean(_snapshot, "mechanical_ready") == true
            && ReadBoolean(_snapshot, "image_ready") == true
            && AxesPositionAvailable());

    private string FormatFactoryReadiness()
    {
        if (_state != ConnectionState.Connected)
            return "工厂标定前置条件未满足：请先连接设备。";
        if (FactoryControlsVisibility != Visibility.Visible)
            return "工厂标定前置条件未满足：当前设备角色或能力未开放工厂标定。";
        if (_operationBusy)
            return "工厂标定前置条件检查中：请等待当前操作完成。";
        if (_lease is not null)
            return $"工厂标定前置条件未满足：当前仍持有 {_lease.Purpose} 控制租约，请先释放。";
        if (HasActiveTask() || !NoActiveTaskConfirmed())
            return "工厂标定前置条件未满足：设备仍有活动任务，请等待任务进入终态。";
        if(Supports("profile.list") && !ProfileNameValid)
            return "配置名称未就绪：请输入直观名称，UTF-8 长度须少于 128 字节。";
        if(Supports("profile.list") && !SelectedProfileDistanceMatches)
            return "当前选中的配置对应另一物距。请点击“新建物距”后标定，原配置将保留。";
        if (_device?.DataSource == DataSourceKind.Simulator)
        {
            if (!TryBuildFactoryRecipe(out _, out var simulatorValidationError))
                return $"工厂配方未就绪：{simulatorValidationError}";
            return "模拟器允许验证 Socket 流程；不代表机械、图像或光学标定通过。";
        }
        if (_device?.AxisRangesValid != true)
            return "工厂标定前置条件未满足：设备未报告有效的 zoom/focus 原始行程。";
        if (ReadBoolean(_snapshot, "mechanical_ready") != true)
            return "工厂标定前置条件未满足：设备机械状态未就绪。";
        if (ReadBoolean(_snapshot, "image_ready") != true)
            return "工厂标定前置条件未满足：设备图像链路未就绪。";
        if (!AxesPositionAvailable())
            return $"工厂标定前置条件未满足：{AxesReadinessProblems()}。请刷新设备状态核对位置；停稳由人工确认。";
        if (!TryBuildFactoryRecipe(out _, out var validationError))
            return $"工厂配方未就绪：{validationError}";
        return "工厂标定前置条件已满足；请人工确认 ZOOM 和 FOCUS 轴停稳后，点击验证并开始单物距标定。";
    }

    private string FormatFactoryCalibrationResult()
    {
        if(_profileTaskId is not null && _profileTaskId==_lastTaskId && _profileTaskOperation is not null) {
            var operation=_profileTaskOperation=="verify"?"物距曲线复验":_profileTaskOperation=="edit"?"人工曲线保存并应用":"物距模型激活";
            return $"{operation} · {(_latestTaskStatus is { } status?FormatTaskObject(status):"等待设备结果")}。";
        }
        if (_factoryResult is not { } result)
        {
            return _factoryValidation is not null
                ? $"配方预检通过，等待工厂标定任务；recipe_sha256={_factoryRecipeHash ?? "未知"}。"
                : "尚未提交单物距标定配方。不会预创建 dcAfTypeModel.json。";
        }

        var state = ReadString(result, "state") ?? "未知";
        var phase = ReadString(result, "phase") ?? "未知";
        var errorCode = ReadScalarText(result, "error_code") ?? "未知";
        var resultCode = ReadScalarText(result, "result") ?? "未知";
        var restoreConfirmed = ReadBoolean(result, "restore_confirmed");
        var modelSaved = ReadBoolean(result, "model_saved");
        var completedNodes = TryGetInt64(result, "completed_nodes", out var completed) ? completed : (long?)null;
        var plannedNodes = _factoryValidation is { } validation && TryGetInt64(validation, "planned_nodes", out var planned)
            ? planned : (long?)null;
        var validatedIntervals = TryGetInt64(result, "validated_intervals", out var intervals) ? intervals : (long?)null;
        var sampleMaximum = ReadString(result,"validation_method")=="sample_maximum";
        var lightChecks = ReadString(result,"validation_method")=="light_checks";
        var acceptedChecks = lightChecks ? (TryGetInt64(result,"light_check_count",out var checks) ? checks : 0) : validatedIntervals;
        var elapsedMilliseconds = TryGetInt64(result, "elapsed_ms", out var elapsed) ? elapsed : (long?)null;
        var remainingBudget = TryGetInt64(result, "remaining_budget_ms", out var remaining) ? remaining : (long?)null;
        var errorMessage = ReadString(result, "error_message");
        var modelValid = ReadBoolean(result, "factory_model_valid") ?? ReadBoolean(_snapshot, "factory_model_valid");
        var noError = string.Equals(errorCode, "NONE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(errorCode, "0", StringComparison.OrdinalIgnoreCase);
        var success = state == "SUCCEEDED"
            && noError
            && restoreConfirmed == true
            && modelSaved == true
            && (sampleMaximum ? completedNodes is >= 2 : acceptedChecks is > 0)
            && modelValid == true;

        if (TryGetInt64(result,"maximum_nodes",out var maximum) && maximum>0) plannedNodes=maximum;
        var progress = completedNodes is { } completedValue && plannedNodes is { } plannedValue
            ? $"{completedValue}/{plannedValue}"
            : completedNodes?.ToString() ?? "未知";
        var detail = $"state={state} · phase={phase} · result={resultCode} · error_code={errorCode}"
            + $" · progress={progress} · 实际节点={ReadString(result, "actual_nodes") ?? completedNodes?.ToString() ?? "未知"} · 阶段={FactoryDataReader.StageLabel(ReadString(result, "sampling_stage") ?? phase)} · {(sampleMaximum?"采样最大值曲线（未做区间复验）":(lightChecks?"轻量检查位置":"已验证区间")+"="+(acceptedChecks?.ToString() ?? "未知"))}"
            + $" · elapsed_ms={elapsedMilliseconds?.ToString() ?? "未知"} · remaining_budget_ms={remainingBudget?.ToString() ?? "未知"}"
            + $" · restore_confirmed={restoreConfirmed?.ToString() ?? "未知"} · model_saved={modelSaved?.ToString() ?? "未知"}"
            + $" · factory_model_valid={modelValid?.ToString() ?? "未知"}";
        if (!string.IsNullOrWhiteSpace(errorMessage))
            detail += $" · error_message={errorMessage}";
        if (ReadString(result, "model_revision") is { Length: > 0 } revision)
            detail += $" · model_revision={revision}";
        if (success)
            return $"单物距标定完成；设备已按镜头型号保存到 /mnt/ext_data/config/backup/dcfactory 并激活曲线。{detail}";
        if (IsTerminalState(state))
        {
            var diagnosis = ExplainFactoryFailure(errorCode, errorMessage);
            return $"单物距标定未满足成功判据：{diagnosis}{detail}。不会把本次结果当作有效模型。";
        }
        return $"单物距标定进行中：{detail}。";
    }

    private static string ExplainFactoryFailure(string errorCode, string? errorMessage)
    {
        var explanations = new List<string>();
        foreach (var record in (errorMessage ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var error = ReadDiagnosticField(record, "error") ?? ReadDiagnosticField(record, "subsequent_error");
            var reason = error switch
            {
                "11" => "未取得可用图像样本；精调的无效采样可回退已有粗扫最高分点，其他采样失败需核对设备日志",
                "27" => ReadDiagnosticField(record, "sweep") switch
                {
                    "wrong_direction" => "FOCUS持续反向位移超出允许回差",
                    "no_forward_progress" => "FOCUS连续1秒没有有效前进",
                    _ => "位置停止变化，仍未到达目标"
                },
                "26" => "运动超时",
                "7" => "位置反馈过期或中断",
                "8" => "控制板指令发送失败",
                "18" => "恢复起始位置失败",
                "15" => ReadDiagnosticField(record, "sweep") switch
                {
                    "pilot_insufficient_reports" => "FOCUS低速试探的位置变化次数不足",
                    "pilot_direction" => "FOCUS正反试探未确认相反的运动方向",
                    "pilot_feedback_gap" => "FOCUS低速试探的反馈间隔超过100毫秒",
                    "pilot_stop_distance" => "FOCUS低速试探的停止距离超出允许范围",
                    "pilot_speed_asymmetry" => "FOCUS正反方向速度差异超过检查上限",
                    "scan_speed_bound" => "FOCUS实际扫焦速度超过短行程试探估算上界",
                    "scan_stop_distance" => "FOCUS连续扫焦的停止距离超过试探估算上界",
                    "scan_guard_large" => "连续扫焦所需保护距离大于可用搜索范围",
                    "scan_window_small" => "连续扫焦搜索范围不足",
                    "missing_motion_callback" => "设备连续运动接口未接入",
                    "no_valid_candidates" => "连续正反扫焦未取得有效清晰度候选",
                    _ => "连续扫焦可用性检查未通过"
                },
                "13" => ReadDiagnosticField(record, "peak") switch
                {
                    "nonpositive_score" => "清晰度评分为零或非正值",
                    "no_valid_scores" => "未获得有效清晰度样本",
                    "missing_both_drops" => "未观察到峰值两侧的有效下降",
                    "missing_left_drop" => "峰值缺少左侧下降证据",
                    "missing_right_drop" => "峰值缺少右侧下降证据",
                    "insufficient_position_resolution" => "电机可重复运动分辨率不足以确认该清晰半宽内的精确焦点",
                    "ambiguous_multiple_peaks" => "检测到多个相近峰值，当前证据不足以区分",
                    "return_approach_limited" => "端点限制导致无法完成独立回位复验",
                    "repeat_score_drop" => "候选焦点复验时清晰度下降超出允许范围",
                    _ => "未检测到可靠清晰度峰值，请核对设备逐点评分日志"
                },
                _ => null
            };
            if (reason is null)
                continue;
            var phase = ReadDiagnosticField(record, "phase") switch
            {
                "22" => "接近标定节点",
                "19" => "恢复起始位置",
                "23" => "搜索焦点",
                "26" => "全程恢复搜索",
                _ => "设备运动"
            };
            var axis = ReadDiagnosticField(record, "axis");
            if (ReadDiagnosticField(record, "sweep") is not null)
                phase = "连续粗扫";
            var explanation = $"{phase}：{(axis is "ZOOM" or "FOCUS" ? axis + " " : string.Empty)}{reason}";
            if (long.TryParse(ReadDiagnosticField(record, "target"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var target)
                && long.TryParse(ReadDiagnosticField(record, "actual"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var actual))
                explanation += $"（目标 {target}，实际 {actual}）";
            if (ReadDiagnosticField(record, "origin") is { } sweepOrigin
                && ReadDiagnosticField(record, "actual") is { } sweepPosition)
                explanation += $"（起点 {sweepOrigin}，实际 {sweepPosition} raw）";
            if (ReadDiagnosticField(record, "sweep") is not null
                && ReadDiagnosticField(record, "actual") is { } sweepActual
                && ReadDiagnosticField(record, "limit") is { } sweepLimit)
                explanation += $"（实测 {sweepActual}，检查值 {sweepLimit}）";
            explanations.Add(explanation);
        }
        if (explanations.Count == 0 && errorCode is "RESTORE_FAILED" or "18")
            explanations.Add("设备恢复起始位置失败；需核对失败轴的目标与实际位置");
        if (explanations.Count == 0 && errorCode is "CAPABILITY_UNVERIFIED" or "15")
            explanations.Add("连续扫焦可用性检查未通过；当前设备未返回具体失败项，请核对扫焦试探日志");
        if (explanations.Count == 0 && errorCode is "NO_RELIABLE_PEAK" or "13")
            explanations.Add("未检测到可靠清晰度峰值，请核对设备逐点评分及两侧下降证据");
        return explanations.Count == 0 ? string.Empty : string.Join("；", explanations) + "。";
    }

    private static string? ReadDiagnosticField(string diagnostic, string field)
    {
        var prefix = field + "=";
        foreach (var token in diagnostic.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (token.StartsWith(prefix, StringComparison.Ordinal))
                return token[prefix.Length..];
        return null;
    }

    private void SetFactoryInput(ref string field, string value, [CallerMemberName] string? propertyName = null)
    {
        if (field == value)
            return;
        field = value;
        OnPropertyChanged(propertyName);
        OnPropertyChanged(nameof(CanStartFactoryCalibration));
        OnPropertyChanged(nameof(FactoryNodeLimitLabel));
        OnPropertyChanged(nameof(FactorySweepGearVisibility)); OnPropertyChanged(nameof(FactoryAdaptiveControlsVisibility)); OnPropertyChanged(nameof(FactorySingleSweepVisibility)); OnPropertyChanged(nameof(FactoryAcquisitionNodesLabel)); OnPropertyChanged(nameof(FactorySweepStrategyLabel)); OnPropertyChanged(nameof(FactorySweepStrategyDescription));
        OnPropertyChanged(nameof(FactoryReadinessText));
    }

    private string FormatTask()
    {
        if (TryGetCurrentTaskStatus() is { } currentTask)
            return FormatTaskObject(currentTask);
        if (TryGetProperty(_snapshot, "active_task", out var activeTask) && activeTask.ValueKind == JsonValueKind.Null)
            return "无活动任务";
        return "任务状态未知";
    }

    private static string FormatTaskObject(JsonElement task)
    {
        if (task.ValueKind != JsonValueKind.Object)
            return "任务状态未知";
        var state = task.TryGetProperty("state", out var stateElement) ? stateElement.ToString() : null;
        var phase = task.TryGetProperty("phase", out var phaseElement) ? phaseElement.ToString() : null;
        var id = ReadString(task, "task_id");
        var stateText = state switch
        {
            "QUEUED" => "排队中",
            "RUNNING" => "运行中",
            "WAITING_INPUT" => "等待输入",
            "PAUSING" => "正在暂停",
            "PAUSED" => "已暂停",
            "CANCELLING" => "正在取消",
            "SUCCEEDED" => "成功",
            "PARTIAL" => "部分完成",
            "FAILED" => "失败",
            "CANCELLED" => "已取消",
            "ABORTED_RESTART" => "设备重启后中止",
            not null => state,
            _ => "任务状态未知"
        };
        var parts = new List<string> { stateText! };
        if (!string.IsNullOrWhiteSpace(phase))
            parts.Add(phase);

        if (task.TryGetProperty("progress_percent", out var progress) && progress.ValueKind == JsonValueKind.Number)
            parts.Add($"{progress.GetRawText()}%");
        else if (TryGetInt64(task, "completed_nodes", out var completed)
            && TryGetInt64(task, "planned_nodes", out var planned))
            parts.Add($"{completed}/{planned} 节点");
        if (TryGetInt64(task, "validated_intervals", out var validatedIntervals))
            parts.Add($"已验证区间 {validatedIntervals}");
        if (TryGetInt64(task, "elapsed_ms", out var elapsedMilliseconds))
            parts.Add($"耗时 {elapsedMilliseconds} ms");

        if (TryGetInt64(task, "effective_budget_ms", out var effectiveBudget))
            parts.Add($"预算 {effectiveBudget} ms");
        else if (TryGetInt64(task, "budget_ms", out var budget))
            parts.Add($"预算 {budget} ms");
        if (TryGetInt64(task, "remaining_budget_ms", out var remainingBudget))
            parts.Add($"剩余预算 {remainingBudget} ms");

        var result = task.TryGetProperty("result", out var resultElement) && resultElement.ValueKind == JsonValueKind.Object
            ? resultElement
            : default;
        var modelSaved = ReadBoolean(task, "model_saved") ?? ReadBoolean(result, "model_saved");
        if (modelSaved is { } modelSavedValue)
            parts.Add(modelSavedValue ? "模型已保存" : "模型未保存");
        if ((ReadString(task, "restore_result") ?? ReadString(result, "restore_result")) is { Length: > 0 } restoreResult)
            parts.Add($"恢复 {restoreResult}");
        var errorCode = ReadScalarText(task, "error_code") ?? ReadScalarText(result, "error_code");
        if (errorCode is { Length: > 0 } && errorCode != "0")
            parts.Add($"错误码 {errorCode}");
        var resultCode = ReadScalarText(task, "result");
        if (resultCode is { Length: > 0 })
            parts.Add($"设备结果码 {resultCode}");

        if (state is "SUCCEEDED" or "PARTIAL" or "FAILED" or "CANCELLED" or "ABORTED_RESTART"
            && FormatAutofocusSearchSummary(task) is { } searchSummary)
        {
            if ((state is "SUCCEEDED" or "PARTIAL")
                && TryGetAutofocusSearchResult(task, out var searchResult)
                && ReadString(searchResult, "conclusion") == "best_available")
                parts[0] = "已停在最高评分点（未确认可靠峰）";
            parts.Add(searchSummary);
        }
        var details = string.Join(" · ", parts);
        return id is null ? details : $"{id} · {details}";
    }

    private static bool TryGetAutofocusSearchResult(JsonElement result, out JsonElement search)
    {
        if (result.ValueKind == JsonValueKind.Object)
        {
            if (result.TryGetProperty("af_search_result", out search) && search.ValueKind == JsonValueKind.Object)
                return true;
            if (result.TryGetProperty("result", out var nested) && nested.ValueKind == JsonValueKind.Object
                && nested.TryGetProperty("af_search_result", out search) && search.ValueKind == JsonValueKind.Object)
                return true;
        }
        search = default;
        return false;
    }

    private static string? FormatAutofocusSearchSummary(JsonElement result)
    {
        if (!TryGetAutofocusSearchResult(result, out var search))
            return null;
        var conclusion = ReadString(search, "conclusion");
        var peakConfirmed = ReadBoolean(search, "peak_confirmed");
        var outcome = conclusion switch
        {
            "focused" when peakConfirmed == true => "已聚焦，可靠峰已确认",
            "focused" when peakConfirmed == false => "已完成聚焦，未确认可靠峰",
            "focused" => "已完成聚焦，可靠峰确认状态未知",
            "best_available" => "未确认可靠峰，已回本次最高评分有效点停稳",
            "failed" => "未完成聚焦",
            _ => null
        };
        if (outcome is null)
            return null;
        var parts = new List<string> { outcome };
        if (ReadBoolean(search, "full_range_covered") is { } covered)
            parts.Add(covered ? "已覆盖全行程" : "未覆盖全行程");
        if (ReadScalarText(search, "global_scan_count") is { } count)
            parts.Add($"全局扫描 {count} 次");
        if (ReadScalarText(search, "scanned_min") is { } minimum
            && ReadScalarText(search, "scanned_max") is { } maximum)
            parts.Add($"扫描范围 raw {minimum}～{maximum}");
        if (ReadScalarText(search, "final_focus") is { } focus)
            parts.Add($"最终焦点 raw {focus}");
        if (ReadScalarText(search, "final_score") is { } score)
            parts.Add($"最终清晰度 {score}");
        return string.Join("；", parts);
    }

    private static bool? ReadBoolean(JsonElement? element, string property) =>
        element is { } value ? ReadBoolean(value, property) : null;

    private static bool? ReadBoolean(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static bool TryGetProperty(JsonElement? element, string property, out JsonElement value)
    {
        if (element is { } root && root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out value))
            return true;
        value = default;
        return false;
    }

    private bool Supports(string method) => _device?.SupportedMethods.Contains(method) == true;

    private string? CurrentTaskId()
    {
        if (TryGetCurrentTaskStatus() is { } currentTask && ReadString(currentTask, "task_id") is { } currentTaskId)
            return currentTaskId;
        if (TryGetProperty(_snapshot, "active_task_id", out var activeTaskIdValue)
            && activeTaskIdValue.ValueKind == JsonValueKind.String
            && activeTaskIdValue.GetString() is { Length: > 0 } activeTaskId
            && activeTaskId != "0")
            return activeTaskId;
        return _lastTaskId;
    }

    private string? CurrentTaskState()
    {
        return TryGetCurrentTaskStatus() is { } currentTask ? ReadString(currentTask, "state") : null;
    }

    private JsonElement? TryGetCurrentTaskStatus()
    {
        if (_latestTaskStatus is { } latestTask && ReadString(latestTask, "state") is not null)
            return latestTask;
        if (_latestTaskStatus is { } latestWithoutState
            && ReadString(latestWithoutState, "task_id") is { } latestTaskId
            && latestTaskId == _lastTaskId)
            return latestWithoutState;
        if (TryGetProperty(_snapshot, "active_task", out var activeTask) && activeTask.ValueKind == JsonValueKind.Object)
            return activeTask;
        if (_latestTaskStatus is { } latestStatus)
            return latestStatus;
        if (TryGetProperty(_snapshot, "task", out var eventTask) && eventTask.ValueKind == JsonValueKind.Object)
            return eventTask;
        return null;
    }

    private void ObserveTaskSnapshot(JsonElement snapshot)
    {
        var isTaskEvent = TryGetProperty(snapshot, "last_reliable_event", out var lastEvent)
            && ReadString(lastEvent, "topic") is { } topic
            && topic.StartsWith("task", StringComparison.Ordinal);
        JsonElement? observedTask = null;
        if (isTaskEvent && TryGetProperty(snapshot, "task", out var latestEventTask) && latestEventTask.ValueKind == JsonValueKind.Object)
            observedTask = latestEventTask;
        else if (TryGetProperty(snapshot, "active_task", out var activeTask) && activeTask.ValueKind == JsonValueKind.Object)
            observedTask = activeTask;
        else if (TryGetProperty(snapshot, "task", out var eventTask) && eventTask.ValueKind == JsonValueKind.Object)
            observedTask = eventTask;

        if (observedTask is not { } task || ReadString(task, "task_id") is not { Length: > 0 } taskId)
            return;
        var isActive = ReadString(task, "state") is "QUEUED" or "RUNNING" or "WAITING_INPUT" or "PAUSING" or "PAUSED" or "CANCELLING";
        if (!isTaskEvent && _lastTaskId == taskId)
            return;
        if (_lastTaskId is null || taskId == _lastTaskId || isActive)
        {
            if (_lastTaskId != taskId)
                _lastTaskKind = null;
            _lastTaskId = taskId;
            _latestTaskStatus = task.Clone();
        }
    }

    private static bool TryGetInt64(JsonElement element, string property, out long result)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(property, out var value))
        {
            result = 0;
            return false;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out result))
            return true;
        if (value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
            return true;

        result = 0;
        return false;
    }

    private static string? ReadScalarText(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return null;
        return value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? value.ToString()
            : null;
    }

    private static string? ReadAnyScalarText(JsonElement element, params string[] properties)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var property in properties)
        {
            if (!element.TryGetProperty(property, out var value))
                continue;
            if (value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                return value.ToString();
        }
        return null;
    }

    private bool HasActiveTask()
    {
        var state = CurrentTaskState();
        return state is "QUEUED" or "RUNNING" or "WAITING_INPUT" or "PAUSING" or "PAUSED" or "CANCELLING";
    }

    private bool NoActiveTaskConfirmed()
    {
        if (TryGetProperty(_snapshot, "active_task", out var activeTask))
        {
            if (activeTask.ValueKind == JsonValueKind.Object)
                return IsTerminalState(ReadString(activeTask, "state"));
            if (activeTask.ValueKind == JsonValueKind.Null
                && TryGetProperty(_snapshot, "active_task_id", out var activeTaskIdForNull))
                return IsEmptyTaskId(activeTaskIdForNull);
            return activeTask.ValueKind == JsonValueKind.Null;
        }
        if (TryGetProperty(_snapshot, "task", out var task))
            return task.ValueKind == JsonValueKind.Null || IsTerminalState(ReadString(task, "state"));
        if (TryGetProperty(_snapshot, "active_task_id", out var activeTaskId))
            return IsEmptyTaskId(activeTaskId);
        if (_latestTaskStatus is { } latestTask && ReadString(latestTask, "task_id") is not null)
            return IsTerminalState(ReadString(latestTask, "state"));
        return false;
    }

    private static bool IsEmptyTaskId(JsonElement taskId) => taskId.ValueKind == JsonValueKind.Null
        || (taskId.ValueKind == JsonValueKind.String
            && (string.IsNullOrEmpty(taskId.GetString()) || taskId.GetString() == "0"));

    private static bool IsTerminalState(string? state) => state is "SUCCEEDED" or "PARTIAL" or "FAILED" or "CANCELLED" or "ABORTED_RESTART";

    private bool IsCurrentTaskTerminal() => CurrentTaskState() is
        "SUCCEEDED" or "PARTIAL" or "FAILED" or "CANCELLED" or "ABORTED_RESTART";

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGetFiniteDouble(JsonElement element, out double value)
    {
        if (element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out value)
            && double.IsFinite(value))
            return true;
        if (element.ValueKind == JsonValueKind.String
            && double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value))
            return true;
        value = 0;
        return false;
    }

    private static bool IsReportedPositionUsable(JsonElement axis, JsonElement position)
    {
        if (!TryGetFiniteDouble(position, out _) || ReadBoolean(axis, "range_valid") == false)
            return false;

        var hasMinimumProperty = axis.TryGetProperty("min_position", out var minimum);
        var hasMaximumProperty = axis.TryGetProperty("max_position", out var maximum);
        var minimumValue = 0d;
        var maximumValue = 0d;
        var hasMinimum = hasMinimumProperty && TryGetFiniteDouble(minimum, out minimumValue);
        var hasMaximum = hasMaximumProperty && TryGetFiniteDouble(maximum, out maximumValue);
        if (hasMinimumProperty != hasMinimum || hasMaximumProperty != hasMaximum || hasMinimum != hasMaximum)
            return false;
        // 行程外的有效回读按对应端点处理；是否达到具体目标由设备任务判定。
        return !hasMinimum || minimumValue < maximumValue;
    }

    private void RefreshSnapshotProperties()
    {
        OnPropertyChanged(nameof(MechanicalStatus));
        OnPropertyChanged(nameof(ImageStatus));
        OnPropertyChanged(nameof(ZoomPosition));
        OnPropertyChanged(nameof(FocusPosition));
        OnPropertyChanged(nameof(ControlStatus));
        OnPropertyChanged(nameof(TaskStatus));
        OnPropertyChanged(nameof(FaultStatus));
        OnPropertyChanged(nameof(SceneModelStatus));
        OnPropertyChanged(nameof(AutofocusReadinessText));
        OnPropertyChanged(nameof(AutofocusResultText));
        OnPropertyChanged(nameof(FactoryCalibrationStatusText));
        OnPropertyChanged(nameof(CanStartFactoryCalibration));
        OnPropertyChanged(nameof(FactoryNodeLimitLabel));
        OnPropertyChanged(nameof(FactorySweepGearVisibility)); OnPropertyChanged(nameof(FactoryAdaptiveControlsVisibility)); OnPropertyChanged(nameof(FactorySingleSweepVisibility)); OnPropertyChanged(nameof(FactoryAcquisitionNodesLabel)); OnPropertyChanged(nameof(FactorySweepStrategyLabel)); OnPropertyChanged(nameof(FactorySweepStrategyDescription));
        OnPropertyChanged(nameof(FactoryReadinessText));
    }

    private static Guid GetStableClientId()
    {
        var settingsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThermEye", "FocusIRWorkbench", "settings");
        Directory.CreateDirectory(settingsDirectory);
        var idPath = Path.Combine(settingsDirectory, "client-id");
        if (File.Exists(idPath) && Guid.TryParse(File.ReadAllText(idPath).Trim(), out var existing))
            return existing;
        var created = Guid.NewGuid();
        File.WriteAllText(idPath, created.ToString());
        return created;
    }

    private void RefreshDeviceProperties()
    {
        OnPropertyChanged(nameof(DeviceIdentity));
        OnPropertyChanged(nameof(RoleText));
        OnPropertyChanged(nameof(LensModel));
        OnPropertyChanged(nameof(FirmwareVersion));
        OnPropertyChanged(nameof(ClarityMetricVersion));
        OnPropertyChanged(nameof(SnapshotSequence));
        OnPropertyChanged(nameof(CapabilitySummary));
        OnPropertyChanged(nameof(SourceBadgeText));
        OnPropertyChanged(nameof(SourceBadgeBrush));
        OnPropertyChanged(nameof(StopButtonVisibility));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanManualJog));
        OnPropertyChanged(nameof(CanAdjustManualJogSpeed));
        RefreshOperationProperties();
    }

    private void RefreshOperationProperties()
    {
        RefreshProfileProperties();
        RefreshAutofocusConfigurationProperties();
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanManualJog));
        OnPropertyChanged(nameof(CanAdjustManualJogSpeed));
        OnPropertyChanged(nameof(OperationControlsVisibility));
        OnPropertyChanged(nameof(TrackingControlsVisibility)); OnPropertyChanged(nameof(TrackingStatusText));
        OnPropertyChanged(nameof(CanRefreshTracking)); OnPropertyChanged(nameof(CanVerifyTracking));
        OnPropertyChanged(nameof(TrackingSettingsSupported));
        OnPropertyChanged(nameof(CanMoveTracking)); OnPropertyChanged(nameof(CanEnableTracking)); OnPropertyChanged(nameof(CanStopTracking));
        OnPropertyChanged(nameof(CanAcquireControl));
        OnPropertyChanged(nameof(CanAcquireSceneControl));
        OnPropertyChanged(nameof(CanReleaseControl));
        OnPropertyChanged(nameof(CanStartAutofocus));
        OnPropertyChanged(nameof(CanStartSceneInitialization));
        OnPropertyChanged(nameof(FactoryControlsVisibility));
        OnPropertyChanged(nameof(CanStartFactoryCalibration));
        OnPropertyChanged(nameof(FactoryNodeLimitLabel));
        OnPropertyChanged(nameof(FactorySweepGearVisibility)); OnPropertyChanged(nameof(FactoryAdaptiveControlsVisibility)); OnPropertyChanged(nameof(FactorySingleSweepVisibility)); OnPropertyChanged(nameof(FactoryAcquisitionNodesLabel)); OnPropertyChanged(nameof(FactorySweepStrategyLabel)); OnPropertyChanged(nameof(FactorySweepStrategyDescription));
        OnPropertyChanged(nameof(FactoryReadinessText));
        OnPropertyChanged(nameof(FactoryCalibrationStatusText));
        OnPropertyChanged(nameof(CanRefreshTask));
        OnPropertyChanged(nameof(CanQueryRequestStatus));
        OnPropertyChanged(nameof(RequestStatusButtonText));
        OnPropertyChanged(nameof(CanCancelTask));
        OnPropertyChanged(nameof(CanReadAutofocusResult));
        OnPropertyChanged(nameof(CanReadTemperature));
        OnPropertyChanged(nameof(ControlLeaseText));
        OnPropertyChanged(nameof(SceneControlsVisibility));
        OnPropertyChanged(nameof(FactoryControlsVisibility));
        OnPropertyChanged(nameof(FactoryCalibrationStatusText));
        OnPropertyChanged(nameof(SceneModelStatus));
        OnPropertyChanged(nameof(TemperatureStatus));
        OnPropertyChanged(nameof(CanRefreshSnapshot));
        OnPropertyChanged(nameof(JogCapabilityText));
        OnPropertyChanged(nameof(TaskStatus));
        OnPropertyChanged(nameof(FaultStatus));
        OnPropertyChanged(nameof(AutofocusReadinessText));
        OnPropertyChanged(nameof(AutofocusResultText));
        RefreshUpdateProperties();
    }

    private void AddActivity(string message)
    {
        RecentActivity.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
        while (RecentActivity.Count > 100)
            RecentActivity.RemoveAt(RecentActivity.Count - 1);
    }

    private static void OnUi(Action action)
    {
        if (System.Windows.Application.Current.Dispatcher.CheckAccess()) action();
        else _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(action);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _previewConnectionEnabled = false;
        _connectedHost = null;
        _connectCancellation?.Cancel();
        var previewDispose = Preview.DisposeAsync();
        _snapshotAgeTimer.Stop();
        _updateCancellation?.Cancel();

        try
        {
            await EndManualJogAsync("工作台关闭");
            await StopTaskMonitorAsync();
            Charts.Dispose();
            await _connection.DisposeAsync();
            _updateCancellation?.Dispose();
            _updateHttpClient.Dispose();
            _taskRefreshNotifications.Writer.TryComplete();
        }
        finally
        {
            await previewDispose;
        }
    }
}
