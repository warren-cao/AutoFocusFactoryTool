using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using System.Windows;
using FocusIR.Workbench.App.Controls;
using FocusIR.Workbench.Domain;
namespace FocusIR.Workbench.App.ViewModels;

public sealed record CalibrationNodeChoice(string Id,string Label);
public sealed partial class CalibrationChartsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly List<FactorySample> _samples=[];
    private readonly List<FactoryNode> _nodes=[];
    private readonly List<FactoryCurvePoint> _curve=[];
    private readonly HashSet<string> _choices=[];
    private bool _dirty;
    private ulong _generation;
    private string _stage="", _error="";
    private bool _simulator, _modelValidated;
    private string _validationMethod="";
    private string? _profileLabel;
    private bool _profileVerification,_profileActive;
    private int _selectedPlotIndex;
    public int SelectedPlotIndex { get=>_selectedPlotIndex;set { _selectedPlotIndex=value;Changed(); } }
    private string CurveLabel => _profileVerification?"（原模型曲线）":_profileLabel is not null?(_profileActive?"（当前激活）":"（已保存）"):_modelValidated?(_validationMethod=="sample_maximum"?"（已激活）":"（已验收）"):"（采集中）";
    private CalibrationNodeChoice? _selected;
    private bool _follow=true;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string? TaskId { get; private set; }
    public string SampleCursor { get; set; }="0";
    public string NodeCursor { get; set; }="0";
    public bool Complete { get; private set; }
    public bool FollowLive { get=>_follow; set { _follow=value; Changed(); } }
    public ObservableCollection<CalibrationNodeChoice> NodeChoices { get; }=[];
    public CalibrationNodeChoice? SelectedNode { get=>_selected; set { _selected=value; FollowLive=false; _dirty=true; Changed(); } }
    public IReadOnlyList<PlotPoint> FocusPoints { get; private set; }=[];
    public IReadOnlyList<PlotPoint> ModelPoints { get; private set; }=[];
    public IReadOnlyList<PlotPoint> ErrorPoints { get; private set; }=[];
    public IReadOnlyList<PlotPoint> RatioPoints { get; private set; }=[];
    public IReadOnlyList<FactorySample> InvalidSamples { get; private set; }=[];
    public Visibility ValidationVisibility => _validationMethod is "sample_maximum" or "manual_adjustment" ? Visibility.Collapsed : Visibility.Visible;
    private string ModelConclusion => _validationMethod=="manual_adjustment" ? "人工调整曲线（需重新复验）" : _profileLabel is not null ? _profileLabel : _validationMethod=="sample_maximum" ? (_modelValidated?"采样最大值曲线已激活":"正在采集最大值曲线") : (_modelValidated?"设备模型最终验收通过":"曲线尚未取得最终验收");
    public string Status => TaskId is null ? "尚无标定任务" : $"{(_simulator?"仅模拟数据 · 不代表光学验收 · ":"")}任务 {TaskId} · {FactoryDataReader.StageLabel(_stage)} · {ModelConclusion} · {(_validationMethod=="sample_maximum"?"未执行区间复验":_validationMethod=="profile_verification"?"同倍率局部评分复验":_validationMethod=="light_checks"?"轻量检查":"区间验收")} · 训练节点 {_nodes.Count(n=>n.Kind=="training")} · 样本 {_samples.Count}/100000 · {(Complete?"完整记录已读取":"正在读取增量记录")} {_error}";
    public CalibrationChartsViewModel() {
        _timer=new DispatcherTimer(TimeSpan.FromMilliseconds(100),DispatcherPriority.Background,(_,_)=>RenderBatch(),Dispatcher.CurrentDispatcher);
    }
    public void Begin(string? taskId) {
        ResetCurveEditor();
        _profileLabel=null;_profileVerification=false;_profileActive=false;SelectedPlotIndex=0;TaskId=taskId; _validationMethod=""; _generation=0; SampleCursor=NodeCursor="0"; Complete=false; _stage=_error=""; _simulator=_modelValidated=false;
        _samples.Clear(); _nodes.Clear(); _curve.Clear(); _choices.Clear(); NodeChoices.Clear(); _selected=null;
        _follow=true; _dirty=true; Changed(nameof(SelectedNode)); Changed(nameof(FollowLive)); Changed(nameof(CanExportRecords)); Changed(nameof(Status)); Changed(nameof(ValidationVisibility));
    }
    public void SetProfileVerification() { _profileLabel="物距模型局部评分复验（保持原曲线）";_profileVerification=true;SelectedPlotIndex=2;_validationMethod="profile_verification";_dirty=true;Changed(nameof(ValidationVisibility)); }
    public void ShowProfile(JsonElement page, bool preserveTaskSamples = false) {
        var retainTask = preserveTaskSamples && TaskId is not null;
        if (!retainTask) {
            Begin("配置 "+FactoryDataReader.Text(page,"profile_id"));
            ApplyNodes(page,true);
        }
        LoadCurveEditorProfile(page);
        // 当前任务的原始样本和节点保留用于 CSV；保存模型只更新曲线。
        UpdateCurve(page);
        _profileLabel=(FactoryDataReader.Text(page,"name")=="新物距标定"?$"{FactoryDataReader.Number(page,"target_distance_m"):0.###} m":FactoryDataReader.Text(page,"name"))+" · "+(FactoryDataReader.Boolean(page,"active")?"当前已激活":"已保存，未激活");
        if (!retainTask) Complete=true;
        _stage="saved_profile";
        if(page.TryGetProperty("verification",out var report)) {
            SetProfileVerification();
            if (!retainTask) {
                var checks=FactoryDataReader.Items(report,"checks").Select((p,i)=> new FactoryNode("verify-"+i,"validation",FactoryDataReader.Text(p,"direction"),FactoryDataReader.Number(p,"zoom_position"),FactoryDataReader.Number(p,"best_focus"),FactoryDataReader.Number(p,"best_score"),0,0,0,null,FactoryDataReader.Number(p,"predicted_focus"),FactoryDataReader.Number(p,"predicted_score"),FactoryDataReader.Number(p,"score_ratio"),Math.Abs(FactoryDataReader.Number(p,"best_focus")-FactoryDataReader.Number(p,"predicted_focus")),0,FactoryDataReader.Boolean(p,"passed"),0,0,0,false,false,false,true,"同倍率局部评分检查；不重建模型"));
                _nodes.AddRange(checks);
            }
        }
        SelectedPlotIndex=1;_profileActive=FactoryDataReader.Boolean(page,"active");_dirty=true;
    }
    public void ApplySamples(JsonElement page) {
        _simulator=FactoryDataReader.Text(page,"data_source")=="simulator";
        foreach(var element in FactoryDataReader.Items(page,"samples")) {
            if(_samples.Count>=100000) throw new InvalidOperationException("上位机样本数量超限，已停止读取曲线。");
            var sample=FactoryDataReader.Sample(element); _samples.Add(sample);
            if(_choices.Add(sample.NodeId)) NodeChoices.Add(new(sample.NodeId,$"节点 {sample.NodeId} · ZOOM {sample.TargetZoom:0} raw"));
            if(_follow || _selected is null) {
                _selected=NodeChoices.First(n=>n.Id==sample.NodeId); Changed(nameof(SelectedNode));
            }
        }
        SampleCursor=FactoryDataReader.Text(page,"next_cursor",SampleCursor);
        _stage=FactoryDataReader.Text(page,"sampling_stage",_stage); _dirty=true;
    }
    public void ApplyNodes(JsonElement page,bool replaceCurve) {
        foreach(var element in FactoryDataReader.Items(page,"nodes")) _nodes.Add(FactoryDataReader.Node(element));
        NodeCursor=FactoryDataReader.Text(page,"next_cursor",NodeCursor);
        if(replaceCurve) {
            _curve.Clear();
            foreach(var e in FactoryDataReader.Items(page,"curve_points")) _curve.Add(new(FactoryDataReader.Text(e,"direction"),FactoryDataReader.Number(e,"zoom_position"),FactoryDataReader.Number(e,"focus_position")));
        }
        _dirty=true;
    }
    public void UpdateCurve(JsonElement page) {
        _generation=FactoryDataReader.UInt64(page,"validation_generation");
        _validationMethod=_profileVerification?"profile_verification":FactoryDataReader.Text(page,"validation_method");
        _modelValidated=FactoryDataReader.Boolean(page,"model_validated");
        _curve.Clear();
        foreach(var e in FactoryDataReader.Items(page,"curve_points")) _curve.Add(new(FactoryDataReader.Text(e,"direction"),FactoryDataReader.Number(e,"zoom_position"),FactoryDataReader.Number(e,"focus_position")));
        _dirty=true;
    }
    public void SetStage(string stage) { _stage=stage; _dirty=true; }
    public void MarkComplete() { Complete=true; _dirty=true; }
    public void SetError(string error) { _error=error; Changed(nameof(Status)); Changed(nameof(ValidationVisibility)); }
    private static PlotPoint FocusPoint(FactorySample s) => new(s.Focus,s.Score??0,
        (s.Valid?FactoryDataReader.StageLabel(s.Stage):"无效点")+" / "+(s.Direction=="negative"?"反向":"正向"),
        $"{FactoryDataReader.StageLabel(s.Stage)} · FOCUS {s.Focus:0}（目标 {s.TargetFocus:0}）· 评分 {s.Score:0.######}\n帧 {s.FrameId} · 位置反馈序号 Z:{s.ZoomRxSequence} F:{s.FocusRxSequence} · 时间来源 {s.TimeSource} · {s.CaptureTime} ms\n{s.InvalidReason}",!s.Valid,s.Stage=="continuous_candidate" && s.Valid);
    // 每桶保留最小/最大评分，避免显示抽稀时丢掉窄峰；CSV保留所有原始帧。
    private static IReadOnlyList<PlotPoint> Reduce(IEnumerable<PlotPoint> points) {
        var all=points.ToArray(); if(all.Length<=5000) return all;
        var result=new List<PlotPoint>(); var groups=all.GroupBy(p=>p.Series).ToArray();
        var perGroup=Math.Max(16,5000/groups.Length);
        foreach(var g in groups) {
            var v=g.ToArray(); var stride=Math.Max(1,(int)Math.Ceiling(v.Length/(perGroup/2.0)));
            for(var i=0;i<v.Length;i+=stride) {
                var bucket=v.Skip(i).Take(stride).ToArray(); var min=bucket.MinBy(p=>p.Y)!; var max=bucket.MaxBy(p=>p.Y)!;
                result.Add(min); if(max!=min) result.Add(max);
            }
        }
        return result;
    }
    private void RenderBatch() {
        if(!_dirty) return; _dirty=false;
        var selected=_samples.Where(s=>s.NodeId==_selected?.Id).ToArray();
        FocusPoints=Reduce(selected.Select(FocusPoint));
        ModelPoints=RenderedCurve.Select(p=>new PlotPoint(p.Zoom,p.Focus,(p.Direction=="tele_to_wide"?"反向 PCHIP":"正向 PCHIP")+CurveLabel,$"设备模型：ZOOM {p.Zoom:0.##} → FOCUS {p.Focus:0.##}",Line:true))
            .Concat(RenderedModelNodes.Select(n=>new PlotPoint(n.Zoom,n.Focus,(n.Direction=="tele_to_wide"?"反向":"正向")+(IsEditing?"可拖动节点":n.MeasurementValid?"实测节点":"人工调整节点"),$"节点 {n.Id} · 评分 {n.Score:0.###} · 已采样清晰范围 [{(n.ClearLeftValid?n.ClearLeft.ToString("0.##",CultureInfo.InvariantCulture):"左侧未测到合格点")},{(n.ClearRightValid?n.ClearRight.ToString("0.##",CultureInfo.InvariantCulture):"右侧未测到合格点")}] · {(n.BoundaryLimited?"边界受限":"")} · 回位重复性 {(n.Repeatability is null?"未测量":n.Repeatability.Value.ToString("0.##",CultureInfo.InvariantCulture))}",NodeId:n.Id))).ToArray();
        var validation=_nodes.Where(n=>n.Kind!="training" && (n.Generation==_generation || n.Kind=="promoted")).ToArray();
        ErrorPoints=_profileVerification ? validation.Select(n=>new PlotPoint(n.Zoom,n.PredictionError,"局部最佳与曲线位置偏差",$"ZOOM {n.Zoom:0} · 偏差 {n.PredictionError:0.###} raw · 本复验以同倍率评分比例判断")).ToArray() : validation.SelectMany(n=>new[] {
            new PlotPoint(n.Zoom,n.PredictionError,$"{(n.Direction=="tele_to_wide"?"反":"正")}向预测误差",$"区间 [{n.IntervalLeft},{n.IntervalRight}] · 误差 {n.PredictionError:0.###} · 允许 {n.AllowedError:0.###} · {(!n.MeasurementValid?"未取得有效焦点 · ":"")}{n.FailureReason} · {(n.Passed?"通过":n.Kind=="promoted"?"已补点，旧验证不作为通过证据":"失败")}"),
            new PlotPoint(n.Zoom,n.AllowedError,"允许误差",$"ZOOM {n.Zoom:0} · 设备验收允许误差 = {n.AllowedError:0.###}") }).ToArray();
        RatioPoints=validation.SelectMany(n=>new[] {new PlotPoint(n.Zoom,n.ScoreRatio,"预测评分 / 参考峰值",$"ZOOM {n.Zoom:0} · 比例 {n.ScoreRatio:0.######} · {(n.Passed?"通过":"失败/已补点")}"),new PlotPoint(n.Zoom,0.95,"要求 ≥ 0.95","评分验收下限")}).ToArray();
        InvalidSamples=selected.Where(s=>!s.Valid).TakeLast(100).Reverse().ToArray();
        Changed(nameof(FocusPoints)); Changed(nameof(ModelPoints)); Changed(nameof(ErrorPoints)); Changed(nameof(RatioPoints)); Changed(nameof(InvalidSamples)); Changed(nameof(Status)); Changed(nameof(ValidationVisibility));
    }
    public void ExportCsv(string path) {
        static string C(object? value) => '"'+Convert.ToString(value,CultureInfo.InvariantCulture)?.Replace("\"","\"\"")+'"';
        using var output=new StreamWriter(path,false,new UTF8Encoding(true));
        output.WriteLine("row_type,task_id,id,node_id,stage,direction,frame_id,capture_monotonic_ms,time_source,target_zoom,target_focus,actual_zoom,actual_focus,score,valid,invalid_reason,predicted_focus,predicted_score,score_ratio,prediction_error,allowed_error,repeatability,clear_left,clear_right,record_complete,validation_generation,zoom_rx_seq,focus_rx_seq,repeatability_measured,clear_left_valid,clear_right_valid,boundary_limited");
        foreach(var s in _samples) output.WriteLine(string.Join(',',new object?[]{"sample",TaskId,s.Id,s.NodeId,s.Stage,s.Direction,s.FrameId,s.CaptureTime,s.TimeSource,s.TargetZoom,s.TargetFocus,s.Zoom,s.Focus,s.Score,s.Valid,s.InvalidReason,null,null,null,null,null,null,null,null,Complete,null,s.ZoomRxSequence,s.FocusRxSequence,null,null,null,null}.Select(C)));
        foreach(var n in _nodes) output.WriteLine(string.Join(',',new object?[]{"node",TaskId,n.Id,n.Id,n.Kind,n.Direction,null,null,null,null,null,n.Zoom,n.Focus,n.Score,n.Passed && n.MeasurementValid,n.FailureReason,n.PredictedFocus,n.PredictedScore,n.ScoreRatio,n.PredictionError,n.AllowedError,n.Repeatability,n.ClearLeft,n.ClearRight,Complete,n.Generation,null,null,n.Repeatability is not null,n.ClearLeftValid,n.ClearRightValid,n.BoundaryLimited}.Select(C)));
        foreach(var p in _curve) output.WriteLine(string.Join(',',new object?[]{"model",TaskId,null,null,"device_pchip",p.Direction,null,null,null,null,null,p.Zoom,p.Focus,null,_modelValidated,null,null,null,null,null,null,null,null,null,Complete,null,null,null,null,null,null,null}.Select(C)));
    }
    private void Changed([CallerMemberName]string? name=null) => PropertyChanged?.Invoke(this,new(name));
    public void Dispose() { _timer.Stop(); }
}
