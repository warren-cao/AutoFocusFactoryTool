using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FocusIR.Workbench.Domain;

namespace FocusIR.Workbench.App.ViewModels;

public sealed partial class CalibrationChartsViewModel
{
    private readonly List<FactoryNode> _profileNodes=[];
    private readonly List<FactoryNode> _draftNodes=[];
    private IReadOnlyList<FactoryCurvePoint> _draftCurve=[];
    private bool _editing, _savingCurve, _editAvailable, _curveReloadRequired;
    private double _focusLow, _focusHigh, _zoomLow, _zoomHigh;
    private string _editUnavailableNotice="请先选择有效物距曲线。";
    private string _editorModelId="",_editorProfileId="",_editorLensModel="";
    public Func<Task<bool>>? SaveCurveRequested { get; set; }
    public bool IsEditing => _editing;
    public bool IsSavingCurve => _savingCurve;
    public bool CanExportRecords => TaskId is not null && !_editing && !_savingCurve;
    public string CurveEditButtonText => _editing ? "取消" : "编辑曲线";
    public bool HasCurveChanges => _editing && _draftNodes.Where((n,i)=>n.Focus!=_profileNodes[i].Focus).Any();
    public bool CanToggleCurveEdit => !_savingCurve && (_editing || !_curveReloadRequired && _editAvailable && _editorModelId.Length>0 && _editorProfileId.Length>0 && _editorLensModel.Length>0 && _profileNodes.Count>=2 && _focusHigh>_focusLow);
    public bool CanSaveCurve => _editing && HasCurveChanges && _editAvailable && !_savingCurve && !_curveReloadRequired;
    public bool CanDragCurveNodes => _editing && _editAvailable && !_savingCurve && !_curveReloadRequired;
    public string CurveEditNotice => _savingCurve ? "正在保存到设备，请等待设备任务结果。"
        : _curveReloadRequired ? (_editing ? "设备模型已变化，草稿保留；请取消编辑后刷新，再基于最新曲线调整。" : "设备模型已变化，请刷新配置后再编辑。")
        : _editing ? "上下拖动节点调整 FOCUS，ZOOM 固定；滚轮缩放、右键平移。取消恢复原曲线，保存会写入并应用到设备。"
        : !_editAvailable ? _editUnavailableNotice
        : _focusHigh<=_focusLow ? "当前配置缺少编辑行程信息，请更新设备库并刷新配置。"
        : _validationMethod=="manual_adjustment" ? "当前为人工调整曲线，原复验结论已清除；固定对应目标后可重新复验。" : "点击编辑曲线后可拖动节点；调整不会移动镜头。";
    public string EditingModelId => _editorModelId;
    public string EditingProfileId => _editorProfileId;
    public string EditingLensModel => _editorLensModel;
    private IEnumerable<FactoryNode> RenderedModelNodes => _editing ? _draftNodes : _profileNodes.Count>0 ? _profileNodes : _nodes.Where(n=>n.Kind=="training");
    private IEnumerable<FactoryCurvePoint> RenderedCurve => _editing && HasCurveChanges ? _draftCurve : _curve;

    public void SetEditAvailability(bool available,string notice) {
        if(_editAvailable==available && _editUnavailableNotice==notice) return;
        _editAvailable=available;_editUnavailableNotice=notice;EditorChanged();
    }
    private void ResetCurveEditor() {
        _editing=_savingCurve=_curveReloadRequired=false;_editorModelId=_editorProfileId=_editorLensModel="";
        _profileNodes.Clear();_draftNodes.Clear();_draftCurve=[];EditorChanged(true);
    }
    private void LoadCurveEditorProfile(JsonElement page) {
        _editing=_savingCurve=_curveReloadRequired=false;
        _editorModelId=FactoryDataReader.Text(page,"model_id");_editorProfileId=FactoryDataReader.Text(page,"profile_id");_editorLensModel=FactoryDataReader.Text(page,"lens_model");
        _profileNodes.Clear();_profileNodes.AddRange(FactoryDataReader.Items(page,"nodes").Select(FactoryDataReader.Node));
        _draftNodes.Clear();_draftCurve=[];
        _focusLow=FactoryDataReader.Number(page,"focus_near_position");_focusHigh=FactoryDataReader.Number(page,"focus_far_position");
        if(_focusLow>_focusHigh) (_focusLow,_focusHigh)=(_focusHigh,_focusLow);
        _zoomLow=FactoryDataReader.Number(page,"zoom_wide_position");_zoomHigh=FactoryDataReader.Number(page,"zoom_tele_position");
        EditorChanged(true);
    }
    public void ToggleCurveEditing() {
        if(!CanToggleCurveEdit) return;
        if(_editing) { _editing=false;_draftNodes.Clear();_draftCurve=[]; }
        else { _draftNodes.Clear();_draftNodes.AddRange(_profileNodes);_editing=true;SelectedPlotIndex=1; }
        _dirty=true;EditorChanged(true);RenderBatch();
    }
    public void MoveCurveNode(string id,double focus) {
        if(!CanDragCurveNodes || !double.IsFinite(focus)) return;
        var index=_draftNodes.FindIndex(n=>n.Id==id);if(index<0) return;
        var next=Math.Clamp(Math.Round(focus,MidpointRounding.AwayFromZero),_focusLow,_focusHigh);
        if(_draftNodes[index].Focus==next) return;
        _draftNodes[index]=_draftNodes[index] with { Focus=next, MeasurementValid=false, ClearLeftValid=false, ClearRightValid=false, Score=0 };
        _draftCurve=PchipPreview(_draftNodes,_zoomLow,_zoomHigh);
        _dirty=true;EditorChanged();RenderBatch();
    }
    public JsonArray BuildCurveNodeEdits() {
        var nodes=new JsonArray();
        foreach(var n in _draftNodes) nodes.Add(new JsonObject { ["node_id"]=n.Id,["focus_position"]=(int)n.Focus });
        return nodes;
    }
    public async Task SaveCurveAsync() {
        if(!CanSaveCurve || SaveCurveRequested is null) return;
        _savingCurve=true;EditorChanged();bool accepted=false;
        try { accepted=await SaveCurveRequested(); }
        finally { if(!accepted) {_savingCurve=false;EditorChanged();} }
    }
    public void RequireCurveReload() { _curveReloadRequired=true;EditorChanged(); }
    public void MarkCurveSavePending() { _savingCurve=true;EditorChanged(); }
    public void FinishCurveSave(string notice) { _savingCurve=false;SetError(notice);EditorChanged(); }
    private void EditorChanged(bool editingChanged=false) {
        if(editingChanged) Changed(nameof(IsEditing));Changed(nameof(IsSavingCurve));Changed(nameof(CurveEditButtonText));Changed(nameof(HasCurveChanges));
        Changed(nameof(CanToggleCurveEdit));Changed(nameof(CanSaveCurve));Changed(nameof(CanDragCurveNodes));Changed(nameof(CurveEditNotice));Changed(nameof(CanExportRecords));
    }
    // 仅用于本地草稿预览；保存后显示设备重新计算的权威曲线。
    private static IReadOnlyList<FactoryCurvePoint> PchipPreview(IEnumerable<FactoryNode> nodes,double low,double high) {
        var result=new List<FactoryCurvePoint>();
        foreach(var group in nodes.GroupBy(n=>n.Direction)) {
            var p=group.OrderBy(n=>n.Zoom).ToArray();int count=p.Length;if(count<2) continue;
            var h=new double[count-1];var delta=new double[count-1];var slopes=new double[count];
            for(int i=0;i<count-1;i++) {h[i]=p[i+1].Zoom-p[i].Zoom;if(h[i]<=0) throw new InvalidOperationException("ZOOM节点必须严格有序。");delta[i]=(p[i+1].Focus-p[i].Focus)/h[i];}
            if(count==2) slopes[0]=slopes[1]=delta[0];
            else {
                for(int i=1;i<count-1;i++) {
                    if(delta[i-1]==0 || delta[i]==0 || Math.Sign(delta[i-1])!=Math.Sign(delta[i])) slopes[i]=0;
                    else {double w1=2*h[i]+h[i-1],w2=h[i]+2*h[i-1];slopes[i]=(w1+w2)/(w1/delta[i-1]+w2/delta[i]);}
                }
                static double EndSlope(double a,double b,double da,double db) {
                    double v=((2*a+b)*da-a*db)/(a+b);
                    if(Math.Sign(v)!=Math.Sign(da)) return 0;
                    return Math.Sign(da)!=Math.Sign(db) && Math.Abs(v)>Math.Abs(3*da)?3*da:v;
                }
                slopes[0]=EndSlope(h[0],h[1],delta[0],delta[1]);slopes[^1]=EndSlope(h[^1],h[^2],delta[^1],delta[^2]);
            }
            for(int i=0;i<=256;i++) {
                double x=low+(high-low)*i/256.0,v=Math.Clamp(x,p[0].Zoom,p[^1].Zoom);int k=0;
                while(k<count-2 && v>p[k+1].Zoom) k++;
                double t=(v-p[k].Zoom)/h[k],t2=t*t,t3=t2*t;
                double y=(2*t3-3*t2+1)*p[k].Focus+(t3-2*t2+t)*h[k]*slopes[k]+(-2*t3+3*t2)*p[k+1].Focus+(t3-t2)*h[k]*slopes[k+1];
                result.Add(new(group.Key,x,y));
            }
        }
        return result;
    }
}
