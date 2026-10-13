using System.Text.Json;
using FocusIR.Workbench.Infrastructure;

namespace FocusIR.Workbench.App.ViewModels;

public sealed partial class MainViewModel
{
    private string? _curveSavingModelId;
    private async Task<bool> SaveEditedCurveAsync() {
        if(!Charts.IsEditing || !Charts.HasCurveChanges || SelectedProfile is not { } profile || SelectedLensModel is not { } lens
            || Charts.EditingLensModel!=lens.Model || Charts.EditingProfileId!=profile.Id || !Supports("profile.update_nodes")) return false;
        var expected=Charts.EditingModelId;var nodes=Charts.BuildCurveNodeEdits();bool accepted=false;
        await RunOperationAsync(async()=> {
            _lease=await _connection.AcquireControlAsync("factory");
            try {
                await _connection.RefreshSnapshotAndResubscribeAsync();
                if(HasActiveTask() || !NoActiveTaskConfirmed()) throw new InvalidOperationException("设备仍有任务，不能覆盖当前曲线。");
                var result=await _connection.UpdateProfileNodesAsync(lens.Model,profile.Id,expected,nodes);
                _lastTaskId=ReadString(result,"task_id");
                if(_lastTaskId is null) throw new InvalidOperationException("设备未返回保存任务编号，请查询请求结果。");
                _curveSavingModelId=ReadString(result,"new_model_id");
                _runningLensModel=lens.Model;_runningProfileId=profile.Id;_profileTaskOperation="edit";_profileTaskId=_lastTaskId;
                _lastTaskKind="factory";_latestTaskStatus=result;_factoryResult=null;
                _statusNotice="设备已受理人工曲线保存，成功后回读最新曲线；此操作不扫描或移动镜头。";
                AddActivity(_statusNotice);StartTaskMonitor(_lastTaskId,"factory");accepted=true;
            }
            catch(Exception ex) { if(ex is DeviceRpcException { Code: "MODEL_REVISION_CONFLICT" or "MODEL_VERSION_MISMATCH" }) Charts.RequireCurveReload();
                if(!HasActiveTask()) {await _connection.ReleaseControlAsync();_lease=null;}throw; }
        });
        return accepted;
    }
}
